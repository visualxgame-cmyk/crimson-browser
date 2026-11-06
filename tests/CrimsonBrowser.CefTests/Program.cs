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
                var settings = new CefSharp.Wpf.CefSettings
                {
                    BrowserSubprocessPath = args.FirstOrDefault(a => a.StartsWith("--subprocess=", StringComparison.Ordinal))?[13..]
                        ?? Path.Combine(AppContext.BaseDirectory, "CrimsonBrowser.exe"),
                    RootCachePath = root,
                    CachePath = Path.Combine(root, "Default"),
                    LogFile = Path.Combine(root, "cef.log")
                };
                Cef.EnableWaitForBrowsersToClose();
                Check(Cef.Initialize(settings, false, browserProcessHandler: null), "CEF initialized with Crimson subprocesses");
                var context = Cef.GetGlobalRequestContext();
                context.RegisterSchemeHandlerFactory("https", "start.crimson",
                    new FolderSchemeHandlerFactory(Path.Combine(AppContext.BaseDirectory, "Resources"), schemeName: "https", hostName: "start.crimson", defaultPage: "startpage.html"));
                File.WriteAllText(Path.Combine(root, "one.html"), "<!doctype html><title>CEF Test One</title><body>Crimson needle needle<input id='form'><a id='popup' href='https://smoke.crimson/two.html' target='_blank' style='position:absolute;left:20px;top:100px;width:200px;height:40px'>New tab</a></body>");
                File.WriteAllText(Path.Combine(root, "two.html"), "<!doctype html><title>CEF Test Two</title><body>Second page<audio id='media' src='silence.wav' loop preload='auto'></audio></body>");
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
                // Incognito prevents tests from writing real Crimson history.
                tab = new BrowserTab(context, isIncognito: true) { IsActive = true };
                window = new Window { Title = "Crimson CEF integration checks", Width = 1000, Height = 700, Content = tab.WebView };
                tab.WebViewChanged += (_, previous, current) => window.Content = current;
                window.Show();
                await Until(() => tab.WebView.IsBrowserInitialized && !tab.IsLoading && tab.Title != "", "browser initialized");
                await UntilAsync(async () => tab.WebView.CanExecuteJavascriptInMainFrame &&
                    (await tab.WebView.EvaluateScriptAsync("typeof openSettings")).Result as string == "function", "bundled new tab rendered");
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
                string? popup = null;
                tab.RequestNewTab += (_, url) => popup = url;
                var host = tab.WebView.GetBrowser().GetHost();
                // DevTools supplies user activation without desktop coordinates or timing races.
                await Cef.UIThreadTaskFactory.StartNew(() => host.ExecuteDevToolsMethod(0, "Runtime.evaluate",
                    "{\"expression\":\"document.getElementById('popup').click()\",\"userGesture\":true}"));
                await Until(() => popup == "https://smoke.crimson/two.html", "CEF popup callback");
                Check(tab.Url == "https://smoke.crimson/one.html", "popup preserves source tab");
                tab.Navigate("https://smoke.crimson/two.html");
                await Until(() => tab.Title == "CEF Test Two" && !tab.IsLoading, "second navigation");
                tab.GoBack();
                await Until(() => tab.Title == "CEF Test One" && !tab.IsLoading, "back navigation");
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
                tab.Discard();
                Check(tab.IsDiscarded, "idle tab document discarded");
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
                }
                finally { main.Close(); }
                Console.WriteLine("PASS: CEF integration");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally
            {
                tab?.Dispose();
                if (window != null) { window.Content = null; window.Close(); }
                privateContext?.Dispose();
                if (Cef.IsInitialized == true) { Cef.WaitForBrowsersToClose(); Cef.Shutdown(); }
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
    private static async Task Until(Func<bool> condition, string description)
    {
        await UntilAsync(() => Task.FromResult(condition()), description);
    }
    private static async Task UntilAsync(Func<Task<bool>> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(description);
            await Task.Delay(50);
        }
        Console.WriteLine("PASS: " + description);
    }
}
