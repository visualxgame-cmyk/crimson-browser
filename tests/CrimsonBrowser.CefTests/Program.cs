using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using CefSharp;
using CefSharp.SchemeHandler;
using CrimsonBrowser.Models;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "CrimsonCefTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Load the production theme without running production startup or accessing its profile.
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CrimsonThemeSource.xaml")).Root!;
        var theme = new XElement(source.Name.Namespace + "ResourceDictionary",
            source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
            source.Element(source.Name.Namespace + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(theme.ToString());
        app.Startup += async (_, _) =>
        {
            BrowserTab? tab = null;
            IRequestContext? privateContext = null;
            Window? window = null;
            int exitCode = 0;
            try
            {
                CrimsonBrowser.Services.CefRuntime.Initialize(root,
                    args.FirstOrDefault(a => a.StartsWith("--subprocess=", StringComparison.Ordinal))?[13..]);
                Check(CrimsonBrowser.Services.CefRuntime.IsInitialized, "production CEF initialization succeeds with an isolated profile");
                var context = Cef.GetGlobalRequestContext();
                string? probeUrl = args.FirstOrDefault(argument => argument.StartsWith("--probe-live=", StringComparison.Ordinal))?[13..];
                if (probeUrl != null)
                {
                    tab = new BrowserTab(context, isIncognito: true);
                    int navigationEpoch = 0;
                    tab.WebView.FrameLoadStart += (_, frame) => { if (frame.Frame.IsMain) Interlocked.Increment(ref navigationEpoch); };
                    tab.InitializationFailed += (_, reason) => Console.Error.WriteLine("LIVE ENGINE ERROR: " + reason);
                    async Task<JavascriptResponse?> ReadPublicPage(string script)
                    {
                        if (tab.WebView.IsLoading || !tab.WebView.CanExecuteJavascriptInMainFrame) return null;
                        int epoch = Volatile.Read(ref navigationEpoch);
                        try { return await tab.WebView.EvaluateScriptAsync(script, timeout: TimeSpan.FromSeconds(5)); }
                        catch (TaskCanceledException) when (epoch != Volatile.Read(ref navigationEpoch) || tab.WebView.IsLoading ||
                            !tab.WebView.CanExecuteJavascriptInMainFrame)
                        {
                            // Navigation invalidates in-flight script replies. Observe the next document instead.
                            return null;
                        }
                    }
                    window = new Window { Title = "Crimson media diagnostics", Width = 1000, Height = 700, Content = tab.WebView };
                    tab.IsActive = true;
                    window.Show();
                    await Until(() => tab.WebView.IsBrowserInitialized && tab.WebView.CanExecuteJavascriptInMainFrame, "media probe ready");
                    Console.WriteLine("CODECS: " + (await tab.WebView.EvaluateScriptAsync("JSON.stringify({h264:MediaSource.isTypeSupported('video/mp4; codecs=\"avc1.42E01E\"'),aac:MediaSource.isTypeSupported('audio/mp4; codecs=\"mp4a.40.2\"'),vp9:MediaSource.isTypeSupported('video/webm; codecs=\"vp9\"'),opus:MediaSource.isTypeSupported('audio/webm; codecs=\"opus\"')})")).Result);
                    using (var consentCookies = await context.GetCookieManagerAsync())
                    {
                        await consentCookies.SetCookieAsync("https://www.youtube.com/", new Cookie { Name = "SOCS", Value = "CAI", Domain = ".youtube.com", Path = "/" });
                        await consentCookies.SetCookieAsync("https://www.youtube.com/", new Cookie { Name = "CONSENT", Value = "YES+cb", Domain = ".youtube.com", Path = "/" });
                    }
                    tab.Navigate(probeUrl);
                    string readyScript = probeUrl.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase)
                        ? "!!document.querySelector('video') || location.hostname === 'consent.youtube.com'"
                        : "!!window.ytInitialPlayerResponse || location.hostname === 'consent.youtube.com'";
                    await UntilAsync(async () =>
                    {
                        var response = await ReadPublicPage(readyScript);
                        return response?.Result is true;
                    }, "public live response available");
                    Console.WriteLine("LIVE: " + (await tab.WebView.EvaluateScriptAsync("JSON.stringify({host:location.hostname,title:document.title,status:window.ytInitialPlayerResponse?.playabilityStatus,live:window.ytInitialPlayerResponse?.videoDetails?.isLiveContent,liveNow:window.ytInitialPlayerResponse?.microformat?.playerMicroformatRenderer?.liveBroadcastDetails?.isLiveNow,formats:[...new Set((window.ytInitialPlayerResponse?.streamingData?.adaptiveFormats||[]).map(f=>f.mimeType))],mediaError:document.querySelector('video')?.error?.code})")).Result);
                    if (args.Contains("--require-live-playback"))
                    {
                        await UntilAsync(async () => (await ReadPublicPage("(()=>{const consent=document.querySelector('ytd-consent-bump-v2-lightbox');return location.hostname!=='consent.youtube.com' && !!document.querySelector('video') && !(consent && consent.getClientRects().length)})()"))?.Result is true,
                            "public live player is created", args.Contains("--interactive-consent") ? 120 : 20);
                        var firstFrame = await tab.WebView.EvaluateScriptAsync("document.querySelector('video').getVideoPlaybackQuality().totalVideoFrames");
                        double initialFrames = Convert.ToDouble(firstFrame.Result);
                        // Muted playback does not require a fabricated user gesture, but
                        // DevTools supplies activation consistently across CEF builds.
                        var liveHost = tab.WebView.GetBrowser().GetHost();
                        await Cef.UIThreadTaskFactory.StartNew(() => liveHost.ExecuteDevToolsMethod(0, "Runtime.evaluate",
                            "{\"expression\":\"(()=>{const v=document.querySelector('video');v.muted=true;document.getElementById('movie_player')?.playVideo?.();v.play()})()\",\"userGesture\":true}"));
                        try { await UntilAsync(async () =>
                        {
                            var playback = await ReadPublicPage("(()=>{const v=document.querySelector('video'),p=document.getElementById('movie_player');return !!v && !!p && !p.classList.contains('ad-showing') && !v.error && !v.paused && v.videoWidth>0 && v.getVideoPlaybackQuality().totalVideoFrames>" +
                                (initialFrames + 90).ToString(System.Globalization.CultureInfo.InvariantCulture) + "})()");
                            return playback?.Result is true;
                        }, "public live decodes and advances video frames", args.Contains("--interactive-consent") ? 120 : 20); }
                        finally
                        {
                            Console.WriteLine("PLAYER STATE: " + (await ReadPublicPage("JSON.stringify({state:document.getElementById('movie_player')?.getPlayerState?.(),error:document.getElementById('movie_player')?.getPlayerError?.(),playError:window.__crimsonPlaybackError,video:[...document.querySelectorAll('video')].map(v=>({paused:v.paused,ready:v.readyState,network:v.networkState,width:v.videoWidth,time:v.currentTime,frames:v.getVideoPlaybackQuality().totalVideoFrames,error:v.error?.code})),message:document.querySelector('.ytp-error-content-wrap')?.innerText,consent:!!document.querySelector('ytd-consent-bump-v2-lightbox')})"))?.Result);
                        }
                        Console.WriteLine("PLAYBACK: " + (await tab.WebView.EvaluateScriptAsync("JSON.stringify({liveNow:window.ytInitialPlayerResponse?.microformat?.playerMicroformatRenderer?.liveBroadcastDetails?.isLiveNow,title:document.title,adShowing:document.getElementById('movie_player').classList.contains('ad-showing'),width:document.querySelector('video').videoWidth,height:document.querySelector('video').videoHeight,frames:document.querySelector('video').getVideoPlaybackQuality().totalVideoFrames,error:document.querySelector('video').error?.code||0})")).Result);
                    }
                    return;
                }
                context.RegisterSchemeHandlerFactory("https", "start.crimson",
                    new FolderSchemeHandlerFactory(Path.Combine(AppContext.BaseDirectory, "Resources"), schemeName: "https", hostName: "start.crimson", defaultPage: "startpage.html"));
                File.WriteAllText(Path.Combine(root, "one.html"), "<!doctype html><title>CEF Test One</title><body>Crimson needle needle<input id='form'><a id='popup' href='https://smoke.crimson/two.html' target='_blank' style='position:absolute;left:20px;top:100px;width:200px;height:40px'>New tab</a></body>");
                File.WriteAllText(Path.Combine(root, "two.html"), "<!doctype html><title>CEF Test Two</title><body>Second page<audio id='media' src='silence.wav' loop preload='auto'></audio></body>");
                File.WriteAllText(Path.Combine(root, "player-fixture.html"), """
                    <!doctype html><title>Popup player fixture</title><body style="margin:0">
                    <video style="position:absolute;left:0;top:0;width:320px;height:180px"></video>
                    <a id="overlay" href="https://random-ad.test/landing" target="_blank"
                       style="position:absolute;left:0;top:0;width:320px;height:180px" onclick="window.playerClicks=(window.playerClicks||0)+1">Play</a>
                    <button id="play" style="position:absolute;top:200px" onclick="window.openResult=window.open('https://random-ad.test/landing');window.playStarted=true">Play video</button>
                    <a id="normal-link" href="https://smoke.crimson/two.html" target="_blank" style="position:absolute;top:250px">Article</a>
                    </body>
                    """);
                File.WriteAllText(Path.Combine(root, "player-poster-fixture.html"), """
                    <!doctype html><title>Poster overlay fixture</title><body style="margin:0">
                    <div class="video-player" style="position:absolute;left:0;top:0;width:320px;height:180px;background:#000">
                      <a id="poster-ad" href="https://random-ad.test/landing" target="_blank"
                         style="position:absolute;left:0;top:0;width:320px;height:180px;z-index:2"
                         onclick="window.posterAdClicks=(window.posterAdClicks||0)+1"></a>
                      <button id="poster-play" style="position:absolute;left:10px;top:150px;z-index:1"
                              onclick="window.posterPlayClicks=(window.posterPlayClicks||0)+1">Play</button>
                    </div>
                    </body>
                    """);
                File.WriteAllText(Path.Combine(root, "trap-fixture.html"), """
                    <!doctype html><title>Popunder trap fixture</title><body style="margin:0">
                    <button id="make-trap" onclick="window.makeTrapClicks=(window.makeTrapClicks||0)+1;var t=document.createElement('div');t.style='position:fixed;display:block;width:100%;height:100%;top:0;left:0;right:0;bottom:0;background-color:rgba(0,0,0,0);z-index:300000;';t.setAttribute('data-trap','1');t.addEventListener('click',function(){window.trapClicks=(window.trapClicks||0)+1;});document.body.appendChild(t);">Make trap</button>
                    </body>
                    """);
                using (var wave = new BinaryWriter(File.Create(Path.Combine(root, "silence.wav"))))
                {
                    wave.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wave.Write(8036);
                    wave.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16);
                    wave.Write((short)1); wave.Write((short)1); wave.Write(8000); wave.Write(8000);
                    wave.Write((short)1); wave.Write((short)8);
                    wave.Write(System.Text.Encoding.ASCII.GetBytes("data")); wave.Write(8000);
                    wave.Write(Enumerable.Repeat((byte)128, 8000).ToArray());
                }
                context.RegisterSchemeHandlerFactory("https", "smoke.crimson", new FolderSchemeHandlerFactory(root, schemeName: "https", hostName: "smoke.crimson"));
                // These origin mappings are local to this temporary test context, with no account/network traffic.
                context.RegisterSchemeHandlerFactory("https", "www.youtube.com", new FolderSchemeHandlerFactory(root, schemeName: "https", hostName: "www.youtube.com"));
                context.RegisterSchemeHandlerFactory("https", "accounts.google.com", new FolderSchemeHandlerFactory(root, schemeName: "https", hostName: "accounts.google.com"));
                File.WriteAllText(Path.Combine(root, "youtube-fixture.html"), "<!doctype html><title>YouTube script fixture</title><body>Local integration fixture</body>");
                File.WriteAllText(Path.Combine(root, "auth-fixture.html"), "<!doctype html><title>Authentication script fixture</title><body>Local integration fixture</body>");
                // Incognito prevents tests from writing real Crimson history.
                tab = new BrowserTab(context, isIncognito: true) { IsActive = true };
                window = new Window { Title = "Crimson CEF integration checks", Width = 1000, Height = 700, Content = tab.WebView };
                tab.WebViewChanged += (_, previous, current) => window.Content = current;
                window.Show();
                await Until(() => tab.WebView.IsBrowserInitialized && !tab.IsLoading && tab.Title != "", "browser initialized");
                await UntilAsync(async () => tab.WebView.CanExecuteJavascriptInMainFrame &&
                    (await tab.WebView.EvaluateScriptAsync("typeof openSettings")).Result as string == "function", "bundled new tab rendered");
                if (args.Contains("--require-aac-h264"))
                {
                    var mediaCapabilities = await tab.WebView.EvaluateScriptAsync("MediaSource.isTypeSupported('video/mp4; codecs=\"avc1.42E01E\"') && MediaSource.isTypeSupported('audio/mp4; codecs=\"mp4a.40.2\"')");
                    Check(mediaCapabilities.Result is true, "staged native engine exposes H.264 and AAC through MediaSource");
                }
                string? mediaFixture = args.FirstOrDefault(argument => argument.StartsWith("--codec-fixture=", StringComparison.Ordinal))?[16..];
                if (mediaFixture != null)
                {
                    File.Copy(Path.GetFullPath(mediaFixture), Path.Combine(root, "codec-fixture.mp4"));
                    File.WriteAllText(Path.Combine(root, "codec-fixture.html"), "<!doctype html><title>Codec decoding fixture</title><video id='decode' src='codec-fixture.mp4' muted autoplay></video>");
                    tab.Navigate("https://smoke.crimson/codec-fixture.html");
                    await UntilAsync(async () => tab.WebView.CanExecuteJavascriptInMainFrame &&
                        (await tab.WebView.EvaluateScriptAsync("(()=>{const video=document.getElementById('decode');return !!video && !video.error && video.videoWidth>0 && video.currentTime>0.2 && !video.paused})()")).Result is true,
                        "staged native engine decodes and plays the supplied local MP4 fixture");
                    tab.Navigate("https://start.crimson/startpage.html");
                    await UntilAsync(async () => tab.WebView.CanExecuteJavascriptInMainFrame &&
                        (await tab.WebView.EvaluateScriptAsync("typeof openSettings")).Result as string == "function", "new tab restored after decoding fixture");
                }
                int settingsRequests = 0;
                tab.RequestOpenSettings += () => settingsRequests++;
                await tab.WebView.EvaluateScriptAsync("openSettings()");
                await Until(() => settingsRequests == 1, "new tab opens settings");
                tab.Navigate("https://smoke.crimson/one.html");
                await Until(() => tab.Title == "CEF Test One" && !tab.IsLoading, "navigation and title");
                await tab.WebView.EvaluateScriptAsync("CefSharp.PostMessage('open-settings')");
                await Task.Delay(150);
                Check(settingsRequests == 1, "untrusted page cannot invoke settings");
                var found = await tab.FindAsync("needle");
                Check(found.Count == 2, "native find returns two matches");
                tab.StopFind();
                tab.WebView.ZoomFactor = 1.2;
                Check(Math.Abs(tab.WebView.ZoomFactor - 1.2) < .01, "zoom conversion");
                tab.WebView.ZoomFactor = 1;
                string? popup = null;
                tab.RequestNewTab += (_, url) => popup = url;
                var host = tab.WebView.GetBrowser().GetHost();
                // DevTools supplies user activation without desktop coordinates or timing races.
                await Cef.UIThreadTaskFactory.StartNew(() =>
                {
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 120, CefEventFlags.None), MouseButtonType.Left, false, 1);
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 120, CefEventFlags.None), MouseButtonType.Left, true, 1);
                });
                await Until(() => popup == "https://smoke.crimson/two.html", "CEF popup callback");
                Check(tab.Url == "https://smoke.crimson/one.html", "popup preserves source tab");
                tab.Navigate("https://smoke.crimson/two.html");
                await Until(() => tab.Title == "CEF Test Two" && !tab.IsLoading, "second navigation");
                tab.GoBack();
                await Until(() => tab.Title == "CEF Test One" && !tab.IsLoading, "back navigation");
                tab.Navigate("https://smoke.crimson/player-fixture.html");
                await UntilAsync(async () => tab.WebView.CanExecuteJavascriptInMainFrame &&
                    (await tab.WebView.EvaluateScriptAsync("document.title==='Popup player fixture' && typeof window.__crimsonSetPopupProtection==='function' && typeof window.googleAd==='object'")).Result is true,
                    "player popup protection is installed");
                popup = null;
                await Cef.UIThreadTaskFactory.StartNew(() => host.ExecuteDevToolsMethod(0, "Runtime.evaluate",
                    "{\"expression\":\"document.getElementById('play').click()\",\"userGesture\":true}"));
                await UntilAsync(async () => (await tab.WebView.EvaluateScriptAsync("window.playStarted===true && window.openResult===null")).Result is true,
                    "play handler continues while its advertising window.open is blocked");
                Check(popup == null, "a play click does not create an advertising tab");
                await Cef.UIThreadTaskFactory.StartNew(() =>
                {
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 60, CefEventFlags.None), MouseButtonType.Left, false, 1);
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 60, CefEventFlags.None), MouseButtonType.Left, true, 1);
                });
                await UntilAsync(async () => (await tab.WebView.EvaluateScriptAsync("window.playerClicks===1")).Result is true,
                    "trusted overlay click still reaches the player handler");
                Check(popup == null && tab.Url == "https://smoke.crimson/player-fixture.html", "advertising link covering the video is cancelled");
                await tab.WebView.EvaluateScriptAsync("document.getElementById('overlay').removeAttribute('target')");
                await Cef.UIThreadTaskFactory.StartNew(() =>
                {
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 60, CefEventFlags.None), MouseButtonType.Left, false, 1);
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 60, CefEventFlags.None), MouseButtonType.Left, true, 1);
                });
                await UntilAsync(async () => (await tab.WebView.EvaluateScriptAsync("window.playerClicks===2")).Result is true,
                    "same-tab overlay click reaches the player handler");
                Check(tab.Url == "https://smoke.crimson/player-fixture.html", "overlay advertising cannot replace the current tab");
                await Cef.UIThreadTaskFactory.StartNew(() =>
                {
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(20, 260, CefEventFlags.None), MouseButtonType.Left, false, 1);
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(20, 260, CefEventFlags.None), MouseButtonType.Left, true, 1);
                });
                await Until(() => popup == "https://smoke.crimson/two.html", "ordinary new-tab links remain functional with popup protection");
                popup = null;
                await Cef.UIThreadTaskFactory.StartNew(() =>
                {
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(20, 260, CefEventFlags.ShiftDown), MouseButtonType.Left, false, 1);
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(20, 260, CefEventFlags.ShiftDown), MouseButtonType.Left, true, 1);
                });
                await Until(() => popup == "https://smoke.crimson/two.html", "Shift-click keeps an explicit link available");
                await tab.WebView.EvaluateScriptAsync("window.__crimsonSetPopupProtection(false);window.playStarted=false;");
                popup = null;
                int blockedBefore = CrimsonBrowser.Services.ShieldService.Instance.BlockedCount;
                await Cef.UIThreadTaskFactory.StartNew(() => host.ExecuteDevToolsMethod(0, "Runtime.evaluate",
                    "{\"expression\":\"document.getElementById('play').click()\",\"userGesture\":true}"));
                await Until(() => CrimsonBrowser.Services.ShieldService.Instance.BlockedCount > blockedBefore,
                    "native protection rejects an advertising popup even when the page script guard is disabled");
                Check(popup == null, "native popup verification does not authorize the play button as a link");
                blockedBefore = CrimsonBrowser.Services.ShieldService.Instance.BlockedCount;
                await Cef.UIThreadTaskFactory.StartNew(() => host.ExecuteDevToolsMethod(0, "Runtime.evaluate",
                    "{\"expression\":\"window.open('https://random-ad.test/landing','ad','width=400,height=500')\",\"userGesture\":true}"));
                await Until(() => CrimsonBrowser.Services.ShieldService.Instance.BlockedCount > blockedBefore,
                    "native protection rejects advertising windows with custom popup dimensions");
                await tab.WebView.EvaluateScriptAsync("window.__crimsonSetPopupProtection(true)");
                await Cef.UIThreadTaskFactory.StartNew(() => host.ExecuteDevToolsMethod(0, "Runtime.evaluate",
                    "{\"expression\":\"window.open('https://accounts.google.com/ServiceLogin','signin','width=400,height=500')\",\"userGesture\":true}"));
                await Until(() => popup == "https://accounts.google.com/ServiceLogin", "Google authentication windows remain available");
                tab.Navigate("https://smoke.crimson/player-poster-fixture.html");
                await UntilAsync(async () => tab.WebView.CanExecuteJavascriptInMainFrame &&
                    (await tab.WebView.EvaluateScriptAsync("document.title==='Poster overlay fixture' && typeof window.__crimsonAllowPopup==='function'")).Result is true,
                    "poster player fixture ready");
                popup = null;
                await Cef.UIThreadTaskFactory.StartNew(() =>
                {
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 60, CefEventFlags.None), MouseButtonType.Left, false, 1);
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 60, CefEventFlags.None), MouseButtonType.Left, true, 1);
                });
                await UntilAsync(async () => (await tab.WebView.EvaluateScriptAsync("window.posterAdClicks===1")).Result is true,
                    "poster overlay click reaches its own handler");
                Check(popup == null && tab.Url == "https://smoke.crimson/player-poster-fixture.html",
                    "an empty player-sized ad overlay cannot open or replace the tab");
                await tab.WebView.EvaluateScriptAsync("document.getElementById('poster-ad').removeAttribute('target')");
                popup = null;
                await Cef.UIThreadTaskFactory.StartNew(() =>
                {
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 60, CefEventFlags.None), MouseButtonType.Left, false, 1);
                    host.SendMouseClickEvent(new CefSharp.MouseEvent(60, 60, CefEventFlags.None), MouseButtonType.Left, true, 1);
                });
                await UntilAsync(async () => (await tab.WebView.EvaluateScriptAsync("window.posterAdClicks===2")).Result is true,
                    "same-tab poster overlay click reaches its own handler");
                Check(tab.Url == "https://smoke.crimson/player-poster-fixture.html",
                    "an empty player-sized ad overlay cannot navigate the current tab");
                tab.Navigate("https://smoke.crimson/trap-fixture.html");
                await UntilAsync(async () => tab.WebView.CanExecuteJavascriptInMainFrame &&
                    (await tab.WebView.EvaluateScriptAsync("document.title==='Popunder trap fixture' && typeof window.__crimsonSetAdProtection==='function'")).Result is true,
                    "popunder click-trap fixture ready");
                await tab.WebView.EvaluateScriptAsync("document.getElementById('make-trap').click()");
                await UntilAsync(async () => (await tab.WebView.EvaluateScriptAsync("document.querySelector('[data-trap]')===null")).Result is true,
                    "transparent full-screen popunder click trap is removed");
                Check((await tab.WebView.EvaluateScriptAsync("window.makeTrapClicks===1 && typeof window.trapClicks==='undefined'")).Result is true,
                    "removing the click trap leaves the page's own handlers intact");
                tab.Navigate("https://smoke.crimson/one.html");
                await Until(() => tab.Title == "CEF Test One" && !tab.IsLoading, "normal fixture restored");
                string? shortcut = null;
                tab.RequestShortcut += (_, action) => shortcut = action;
                tab.WebView.GetBrowser().GetHost().SendKeyEvent(new KeyEvent
                {
                    Type = KeyEventType.RawKeyDown, WindowsKeyCode = 84, Modifiers = CefEventFlags.ControlDown
                });
                await Until(() => shortcut == "new-tab", "native Ctrl+T");
                using var cookies = await context.GetCookieManagerAsync();
                Check(await cookies.SetCookieAsync("https://smoke.crimson/", new Cookie { Name = "isolation", Value = "normal", Domain = "smoke.crimson", Path = "/" }), "normal context cookie stored");
                privateContext = new RequestContext(new RequestContextSettings { CachePath = "" });
                using var privateCookies = await privateContext.GetCookieManagerAsync();
                var isolated = await privateCookies.VisitUrlCookiesAsync("https://smoke.crimson/", true);
                Check(!isolated.Any(c => c.Name == "isolation"), "private context isolates cookies");
                await tab.ClearBrowsingDataAsync();
                Check(!(await cookies.VisitUrlCookiesAsync("https://smoke.crimson/", true)).Any(), "clear browsing cookies");
                using (var lazy = new BrowserTab(context, isIncognito: true))
                {
                    lazy.Navigate("https://smoke.crimson/two.html");
                    Check(lazy.Url == "https://smoke.crimson/two.html", "uninitialized background tab retains session URL");
                }
                bool fullscreen = false;
                tab.FullscreenChanged += value => fullscreen = value;
                await Cef.UIThreadTaskFactory.StartNew(() => host.ExecuteDevToolsMethod(0, "Runtime.evaluate",
                    "{\"expression\":\"document.body.requestFullscreen()\",\"userGesture\":true}"));
                await Until(() => fullscreen, "document fullscreen enters");
                tab.ExitDocumentFullscreen();
                await Until(() => !fullscreen, "document fullscreen exits");
                tab.Navigate("https://smoke.crimson/two.html");
                await Until(() => tab.Title == "CEF Test Two" && !tab.IsLoading, "discard test document loads");
                tab.IsActive = false;
                tab.Discard();
                Check(!tab.IsDiscarded, "tabs with navigation history retain Back and Forward state");
                tab.Dispose();
                window.Content = null;
                tab = new BrowserTab(context, isIncognito: true);
                tab.WebViewChanged += (_, previous, current) => window.Content = current;
                tab.Navigate("https://smoke.crimson/two.html");
                window.Content = tab.WebView;
                tab.IsActive = true;
                await Until(() => tab.Title == "CEF Test Two" && !tab.IsLoading, "single-entry tab loads without eager start page");
                var mediaHost = tab.WebView.GetBrowser().GetHost();
                mediaHost.SetAudioMuted(true);
                await Cef.UIThreadTaskFactory.StartNew(() => mediaHost.ExecuteDevToolsMethod(0, "Runtime.evaluate",
                    "{\"expression\":\"document.getElementById('media').play()\",\"userGesture\":true}"));
                await Until(() => tab.IsDocumentPlayingAudio, "playing media protects a tab");
                tab.IsActive = false;
                tab.Discard();
                Check(!tab.IsDiscarded, "playing background media remains alive");
                tab.WebView.ExecuteScriptAsync("document.getElementById('media').pause()");
                await Until(() => !tab.IsDocumentPlayingAudio, "paused media releases its memory-saver exemption");
                var releasedView = tab.WebView;
                string unrelatedDownload = "unrelated-" + Guid.NewGuid().ToString("N");
                CrimsonBrowser.Services.DownloadsService.Instance.Update(unrelatedDownload, Path.Combine(root, "other.bin"),
                    "https://smoke.crimson/other.bin", 100, 10, CrimsonBrowser.Services.DownloadStatus.InProgress, isPrivate: true);
                tab.Discard();
                Check(tab.IsDiscarded, "idle tab document discarded");
                Check(tab.IsDiscarded && CrimsonBrowser.Services.DownloadsService.Instance.ActiveCount > 0,
                    "a download in another tab does not prevent idle tab discard");
                CrimsonBrowser.Services.DownloadsService.Instance.Update(unrelatedDownload, Path.Combine(root, "other.bin"),
                    "https://smoke.crimson/other.bin", 100, 10, CrimsonBrowser.Services.DownloadStatus.Cancelled, isPrivate: true);
                Check(releasedView.IsDisposed, "discard releases native browser and surface");
                tab.IsActive = true;
                await Until(() => !tab.IsDiscarded && tab.Title == "CEF Test Two" && !tab.IsLoading, "discarded tab restores");
                var formMessage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tab.WebView.JavascriptMessageReceived += (_, message) =>
                {
                    if (message.Message as string == "crimson-form-dirty") formMessage.TrySetResult();
                };
                tab.WebView.ExecuteScriptAsync("const draft=document.createElement('input');document.body.append(draft);" +
                    "draft.value='unsaved';draft.dispatchEvent(new Event('input',{bubbles:true}));history.replaceState(null,'','#draft');");
                await formMessage.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                tab.IsActive = false;
                tab.Discard();
                Check(!tab.IsDiscarded, "unsaved form input survives same-document URL changes and memory saving");
                tab.IsActive = true;
                tab.Navigate("https://www.youtube.com/youtube-fixture.html");
                await Until(() => tab.Title == "YouTube script fixture" && !tab.IsLoading, "local YouTube-origin fixture loads");
                await YouTubeShieldChecks.Run(tab);
                tab.Navigate("https://accounts.google.com/auth-fixture.html");
                await Until(() => tab.Title == "Authentication script fixture" && !tab.IsLoading, "local authentication-origin fixture loads");
                await tab.WebView.EvaluateScriptAsync(CrimsonBrowser.Services.BrowserScripts.AdBlock);
                Check((await tab.WebView.EvaluateScriptAsync("Boolean(window.__crimsonAdShield)")).Result is false,
                    "credential pages are not modified by the ad-block script");
                var fixture = new[] { new CrimsonBrowser.Services.HistoryEntry("Conversation", "https://chatgpt.com/c/example", DateTime.Now) };
                var main = new CrimsonBrowser.MainWindow(addressSuggestions: query => CrimsonBrowser.Services.OmniboxSuggestions.Search(fixture, query));
                main.Show();
                try
                {
                    await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var address = (TextBox)main.FindName("OmniboxTextBox");
                    var addressPopup = (Popup)main.FindName("OmniboxSuggestionsPopup");
                    main.Activate();
                    Keyboard.Focus(address);
                    TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, address, "chat"));
                    await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(address.Text == "chatgpt.com" && address.SelectionStart == 4 && address.SelectionLength == 7,
                        "address bar completes typed domain with selected suffix");
                    Check(addressPopup.IsOpen, "address suggestions open below the bar");
                    Check(Math.Abs(((FrameworkElement)addressPopup.Child).ActualWidth -
                        ((FrameworkElement)main.FindName("OmniboxBorder")).ActualWidth) < 2,
                        "suggestion panel matches the address bar width");
                    TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, address, "g"));
                    await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(address.Text == "chatgpt.com" && address.SelectionStart == 5 && address.SelectionLength == 6,
                        "continued typing replaces the selected completion");
                    address.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, Key.Back)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    EditingCommands.Backspace.Execute(null, address);
                    await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(address.Text == "chatg", "Backspace removes completion without reinserting it");
                    address.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Check(!addressPopup.IsOpen, "Escape dismisses address suggestions");
                    main.AddNewTab("https://smoke.crimson/two.html", activate: false);
                    await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(!main.Tabs[^1].WebView.IsBrowserInitialized,
                        "background session tabs do not initialize native renderers before selection");
                    var bookmarks = (ItemsControl)main.FindName("BookmarksBarItemsControl");
                    bookmarks.ItemsSource = new[]
                    {
                        new CrimsonBrowser.Services.Bookmark { Title = "YouTube", Url = "https://youtube.com/" },
                        new CrimsonBrowser.Services.Bookmark { Title = new string('W', 120), Url = "https://example.test/long" },
                        new CrimsonBrowser.Services.Bookmark { Title = "", Url = "https://chatgpt.com/" }
                    };
                    main.ToggleBookmarksBar();
                    await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    bookmarks.UpdateLayout();
                    var firstBookmark = FindChild<Button>(bookmarks.ItemContainerGenerator.ContainerFromIndex(0));
                    var longBookmark = FindChild<Button>(bookmarks.ItemContainerGenerator.ContainerFromIndex(1));
                    var unnamedBookmark = FindChild<TextBlock>(bookmarks.ItemContainerGenerator.ContainerFromIndex(2));
                    Check(firstBookmark.ActualWidth > 50, "bookmark labels are not constrained to navigation icon width");
                    Check(longBookmark.ActualWidth <= 180 && longBookmark.ActualHeight >= 26, "long bookmark labels remain bounded and fully visible vertically");
                    Check(unnamedBookmark.Text == "chatgpt.com", "bookmarks without a title display their site name");
                    if (args.Contains("--capture-bookmarks"))
                    {
                        var bar = (FrameworkElement)main.FindName("BookmarksBarBorder");
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(bar.ActualWidth),
                            (int)Math.Ceiling(bar.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        var drawing = new System.Windows.Media.DrawingVisual();
                        using (var canvas = drawing.RenderOpen())
                            canvas.DrawRectangle(new System.Windows.Media.VisualBrush(bar), null, new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
                        bitmap.Render(drawing);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var image = File.Create(Path.Combine(AppContext.BaseDirectory, "bookmarks-preview.png"));
                        encoder.Save(image);
                    }
                    bookmarks.ItemsSource = Enumerable.Range(1, 16).Select(index => new CrimsonBrowser.Services.Bookmark
                    {
                        Title = "Bookmark " + index + " with a longer title", Url = "https://example.test/" + index
                    }).ToArray();
                    main.Width = 700;
                    bookmarks.UpdateLayout();
                    main.UpdateLayout();
                    await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var bookmarkScroll = (ScrollViewer)main.FindName("BookmarksScrollViewer");
                    bookmarkScroll.UpdateLayout();
                    Check(bookmarkScroll.ScrollableWidth > 0 && bookmarkScroll.ViewportHeight >= 26,
                        "overflow bookmarks keep full label height in a narrow window");
                    bookmarkScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                        { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                    await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(bookmarkScroll.HorizontalOffset > 0, "mouse wheel reaches overflow bookmarks horizontally");
                }
                finally { main.Close(); }
                var deferred = new CrimsonBrowser.MainWindow(deferBrowserStartup: true);
                deferred.Show();
                try
                {
                    await deferred.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(deferred.Tabs.Count == 0 && !deferred.NewWindowCommand.CanExecute(null), "startup shell waits for engine readiness before creating browsers");
                    deferred.CompleteBrowserStartup();
                    deferred.CompleteBrowserStartup();
                    Check(deferred.Tabs.Count == 1 && deferred.NewTabCommand.CanExecute(null), "engine completion initializes the shell once and enables navigation");
                    Check(((Border)deferred.FindName("StartupPlaceholder")).Visibility == Visibility.Collapsed,
                        "startup placeholder is removed after initialization");
                }
                finally { deferred.Close(); }
                File.WriteAllBytes(Path.Combine(root, "payload.bin"), new byte[65536]);
                string downloadPath = Path.Combine(root, "saved-payload.bin");
                using (var downloading = new BrowserTab(context, isIncognito: true))
                {
                    CrimsonBrowser.Services.TabDownloadHandler? handler = null;
                    handler = new CrimsonBrowser.Services.TabDownloadHandler(downloading, (_, _) =>
                    {
                        Check(handler!.HasActiveDownloads, "download owner is protected while choosing a save location");
                        downloading.IsActive = false;
                        downloading.Discard();
                        Check(!downloading.IsDiscarded, "memory saving preserves the tab that owns a pending download");
                        return downloadPath;
                    });
                    downloading.WebView.DownloadHandler = handler;
                    downloading.Navigate("https://smoke.crimson/two.html");
                    window.Content = downloading.WebView;
                    downloading.IsActive = true;
                    await Until(() => downloading.Title == "CEF Test Two" && !downloading.IsLoading, "download owner fixture loads");
                    downloading.WebView.StartDownload("https://smoke.crimson/payload.bin");
                    await Until(() => File.Exists(downloadPath) && CrimsonBrowser.Services.DownloadsService.Instance.Downloads.Any(item =>
                        item.SavePath == downloadPath && item.Status == CrimsonBrowser.Services.DownloadStatus.Completed), "native CEF download completes");
                    Check(File.ReadAllBytes(downloadPath).Length == 65536, "downloaded fixture retains its complete payload");
                    downloading.Discard();
                    Check(downloading.IsDiscarded, "completed download releases its tab memory-saver exemption");
                    window.Content = null;
                }
                Console.WriteLine("PASS: CEF integration");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally
            {
                tab?.Dispose();
                if (window != null) { window.Content = null; window.Close(); }
                privateContext?.Dispose();
                CrimsonBrowser.Services.CefRuntime.Shutdown();
                CrimsonBrowser.Services.CefRuntime.Shutdown();
                Console.WriteLine("Test profile: " + root);
                app.Shutdown(exitCode);
            }
        };
        return app.Run();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
    private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = FindChildOrNull<T>(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
            if (child != null) return child;
        }
        throw new InvalidOperationException("Missing visual element " + typeof(T).Name);
    }
    private static T? FindChildOrNull<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = FindChildOrNull<T>(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
            if (child != null) return child;
        }
        return null;
    }
    private static async Task Until(Func<bool> condition, string description)
    {
        await UntilAsync(() => Task.FromResult(condition()), description);
    }
    private static async Task UntilAsync(Func<Task<bool>> condition, string description, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(description);
            await Task.Delay(50);
        }
        Console.WriteLine("PASS: " + description);
    }
}
