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
                tab.IsActive = true;
                tab.Navigate("https://www.youtube.com/youtube-fixture.html");
                await Until(() => tab.Title == "YouTube script fixture" && !tab.IsLoading, "local YouTube-origin fixture loads");
                await tab.WebView.EvaluateScriptAsync("delete window.__crimsonAdShield;" +
                    "Object.defineProperty(window,'ytInitialData',{configurable:true,writable:true,value:{marker:'keep-existing',adSlots:[1]}});" +
                    "window.testAccessor=function(){return {marker:'keep-accessor'}};" +
                    "Object.defineProperty(window,'ytInitialPlayerResponse',{configurable:true,get:window.testAccessor});");
                await tab.WebView.EvaluateScriptAsync(CrimsonBrowser.Services.BrowserScripts.AdBlock);
                var initialData = await tab.WebView.EvaluateScriptAsync("ytInitialData.marker==='keep-existing' && ytInitialData.adSlots.length===0 && " +
                    "Object.getOwnPropertyDescriptor(window,'ytInitialPlayerResponse').get===window.testAccessor");
                Check(initialData.Result is true, "late ad-block injection preserves existing YouTube data and accessors");
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
