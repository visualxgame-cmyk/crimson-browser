using System.IO;
using System.Windows;
using System.Windows.Controls;
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
                File.WriteAllText(Path.Combine(root, "two.html"), "<!doctype html><title>CEF Test Two</title><body>Second page</body>");
                context.RegisterSchemeHandlerFactory("https", "smoke.crimson", new FolderSchemeHandlerFactory(root, schemeName: "https", hostName: "smoke.crimson"));
                // Incognito prevents tests from writing real Crimson history.
                tab = new BrowserTab(context, isIncognito: true) { IsActive = true };
                window = new Window { Title = "Crimson CEF integration checks", Width = 1000, Height = 700, Content = tab.WebView };
                window.Show();
                await Until(() => tab.WebView.IsBrowserInitialized && !tab.IsLoading && tab.Title != "", "browser initialized");
                await UntilAsync(async () => (await tab.WebView.EvaluateScriptAsync("typeof openSettings")).Result as string == "function", "bundled new tab rendered");
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
                Check(tab.IsDiscarded, "idle tab document discarded");
                tab.IsActive = true;
                await Until(() => !tab.IsDiscarded && tab.Title == "CEF Test Two" && !tab.IsLoading, "discarded tab restores");
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
