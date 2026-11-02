using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Vela.Models;
using Vela.Services;

namespace Vela.Tests;

internal static partial class Program
{
    private static int _assertions;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _assertions++;
        Console.WriteLine("PASS " + message);
    }
    private static async Task Until(Func<bool> predicate, string description, int timeout = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(description);
            await Task.Delay(50);
        }
    }
    private static async Task ScriptUntil(CoreWebView2 core, string expression, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await core.ExecuteScriptAsync(expression).WaitAsync(TimeSpan.FromSeconds(10)) != "true")
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(description);
            await Task.Delay(50);
        }
        Check(true, description);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Vela.SmokeTests;component/Theme/Theme.xaml", UriKind.Relative) });
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); Environment.ExitCode = 1; app.Shutdown(1); };
        app.Startup += async (_, _) =>
        {
            try
            {
                ProfileContext.Initialize(args, AppPaths.BaseRoot);
                AppPaths.EnsureCreated();
                ThemeCatalog.Apply(new Settings());
                if (args.Contains("--permissions")) { await PermissionPromptTests(); app.Shutdown(0); return; }
                if (args.Contains("--fullscreen")) { await FullscreenTests(); app.Shutdown(0); return; }
                if (args.Contains("--auth-compat")) { await AuthenticationCompatibilityTests(); app.Shutdown(0); return; }
                if (args.Contains("--real-benchmark")) { await RealBenchmarkAsync(args.Contains("--economy")); app.Shutdown(0); return; }
                if (args.Contains("--live-update"))
                {
                    var release = await UpdateService.CheckAsync(AppConstants.DefaultUpdateRepository);
                    Check(!release.IsNewer && release.LatestVersion == Version.Parse(AppConstants.Version), "Published GitHub release is reachable through the real Vela update checker");
                    Console.WriteLine(release.ReleaseUrl); app.Shutdown(0); return;
                }
                if (args.Contains("--extensions"))
                {
                    await ExtensionTests(); app.Shutdown(0); return;
                }
                if (args.Contains("--public-sites")) { await PublicSiteTests(); app.Shutdown(0); return; }
                if (args.Contains("--profile-fixture")) { await ProfileChildTests(); app.Shutdown(0); return; }
                if (args.Contains("--benchmark"))
                {
                    await BenchmarkAsync();
                    app.Shutdown(0);
                    return;
                }
                if (args.Contains("--crash-seed") || args.Contains("--recover"))
                {
                    await RecoveryChild(args.Contains("--crash-seed"));
                    app.Shutdown(0);
                    return;
                }
                StoreTests();
                await AdditionalStoreTests();
                await ModernStoreTests();
                MessageDialogTests();
                var window = new MainWindow { Title = "Vela — verificare izolată" };
                window.Show();
                if (args.Contains("--preview")) { window.Closed += (_, _) => app.Shutdown(); return; }
                if (args.Contains("--stability"))
                {
                    await Until(() => Field<ObservableCollection<BrowserTab>>(window, "_tabs").FirstOrDefault()?.View?.CoreWebView2 is not null, "Stability engine starts", 30000);
                    await MediaProtectionTests(window); await SmallWindowTests(window);
                    await CloseWindowAsync(window); Console.WriteLine($"SUCCESS: {_assertions} stability assertions."); app.Shutdown(0); return;
                }
                if (args.Contains("--modern"))
                {
                    var initialTabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
                    await Until(() => initialTabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Initialize modern tests", 30000);
                    await ModernBrowserTests(window); await CloseWindowAsync(window); app.Shutdown(0); return;
                }
                await BrowserTests(window);
                await WindowCloseDialogTests(window);
                await WorkspaceTests(window);
                await ModernBrowserTests(window);
                await ExpansionTests(window);
                await MediaProtectionTests(window);
                await SmallWindowTests(window);
                await AuthenticationCompatibilityTests();
                await PermissionPromptTests();
                await FullscreenTests();
                await CloseWindowAsync(window);
                await CrashRecoveryTests();
                Console.WriteLine($"SUCCESS: {_assertions} assertions. Isolated profile: {AppPaths.Root}");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Console.Error.WriteLine("Isolated profile: " + AppPaths.Root);
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static async Task RecoveryChild(bool seed)
    {
        var window = new MainWindow();
        window.Show();
        var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Recovery window initialization");
        if (seed)
        {
            var tab = await (Task<BrowserTab>)Invoke(window, "AddTabAsync", "https://restore.test/important", false, null)!;
            tab.Title = "Unsaved work tab";
            await Until(() => SessionStore.Load()?.Tabs.Count == 2, "Session is persisted before crashing");
            // No Closing/Closed handlers: model an actual process failure.
            Process.GetCurrentProcess().Kill();
            return;
        }
        Check(tabs.Count == 2 && tabs[1].Url == "https://restore.test/important" && tabs[1].Title == "Unsaved work tab",
            "Fresh process restores the live snapshot after a forced termination");
        Check(tabs[1].View is null, "Recovered background tabs remain unloaded to save resources");
        await CloseWindowAsync(window);
    }

    private static async Task CrashRecoveryTests()
    {
        var data = Path.Combine(AppPaths.Root, "RecoveryChild");
        async Task<int> Run(string mode)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!, mode)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.Environment["VELA_SMOKE_DATA"] = data;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Console.Write(await output);
            if (process.ExitCode != 0 && mode == "--recover") Console.Error.Write(await error);
            return process.ExitCode;
        }
        Check(await Run("--crash-seed") != 0 && File.Exists(Path.Combine(data, "Data", "session.json")),
            "Live session survives an actual process kill");
        Check(await Run("--recover") == 0, "Recovery succeeds in a new browser process");
    }

    private static void StoreTests()
    {
        var path = Path.Combine(AppPaths.Data, "atomic.json");
        AtomicFile.Write(path, "first");
        AtomicFile.Write(path, "second");
        Check(File.ReadAllText(path) == "second" && File.ReadAllText(path + ".bak") == "first", "Atomic replacement retains previous data");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SessionStore.UpdateWindow(a, new SessionState { Tabs = [new("https://one.test", "One")] });
        SessionStore.UpdateWindow(b, new SessionState { Tabs = [new("https://two.test", "Two")] });
        Check(SessionStore.Load()!.Tabs.Count == 2 && SessionStore.Load()!.ActiveIndex == 1, "Session includes both windows and the active tab");
        SessionStore.CloseWindow(b);
        Check(SessionStore.Load()!.Tabs.Single().Title == "One", "Closing a window retains the remaining window");
        SessionStore.UpdateWindow(a, new SessionState { Tabs = [new("https://three.test", "Three")] });
        File.WriteAllText(Path.Combine(AppPaths.Data, "session.json"), "{broken");
        Check(SessionStore.Load()!.Tabs.Single().Title == "One", "Corrupt session recovers from the atomic backup");
        SessionStore.Clear();
        Check(SessionStore.Load() is null, "Disabling restoration removes primary and backup session");

        Check(ShortcutStore.All.Count == 6, "Six bundled default shortcuts");
        ShortcutStore.Save(null, "Smoke", "smoke.test");
        var shortcut = ShortcutStore.All.Last();
        Check(shortcut.Url == "https://smoke.test", "Shortcut normalizes a bare host");
        ShortcutStore.Move(shortcut.Id, -1);
        Check(ShortcutStore.All[^2].Id == shortcut.Id, "Shortcut order persists");
        ShortcutStore.Save(shortcut.Id, "Renamed", "https://smoke.test/smoke-page.html");
        Check(ShortcutStore.All.Single(s => s.Id == shortcut.Id).Name == "Renamed", "Shortcut editing retains its stable identity");
        foreach (var bad in new[] { "file:///secret", "javascript://alert(1)", "https://user:password@example.com" })
        {
            var rejected = false;
            try { ShortcutStore.Save(null, "Invalid", bad); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Reject unsafe shortcut: " + bad.Split(':')[0]);
        }
        ShortcutStore.Remove(shortcut.Id);
        Check(ShortcutStore.All.Count == 6, "Shortcut removal persists");
        ShortcutStore.Save(null, "Smoke", "https://smoke.test/smoke-page.html");
        TabProtectionStore.Set("smoke.test", true);
        Check(TabProtectionStore.Contains("https://smoke.test/path") && !TabProtectionStore.Contains("https://sub.smoke.test"), "Sleep exception matches the exact host across paths");
        TabProtectionStore.Set("smoke.test", false);
        Check(!TabProtectionStore.Contains("https://smoke.test/path"), "Sleep exception can be removed");
    }

    private static async Task BrowserTests(MainWindow window)
    {
        var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Initialize WebView2");
        var original = tabs[0];
        var core = original.View!.CoreWebView2;
        await ScriptUntil(core, "document.querySelectorAll('.tile').length === 7", "New Tab receives persisted shortcuts through WebView2");
        await ScriptUntil(core, "document.querySelectorAll('.avatar.has-icon').length === 6", "All six bundled logos decode locally");
        await core.ExecuteScriptAsync("document.getElementById('add-shortcut').click(); document.getElementById('shortcut-name').value = '<Local UI>'; document.getElementById('shortcut-url').value = 'https://ui.test'; document.getElementById('shortcut-form').requestSubmit();");
        await ScriptUntil(core, "document.querySelectorAll('.tile').length === 8 && !document.getElementById('shortcut-dialog').open", "New Tab editor saves and receives acknowledgement");
        await ScriptUntil(core, "Array.from(document.querySelectorAll('.tile span')).some(s => s.textContent === '<Local UI>') && !document.querySelector('local')", "Shortcut titles render as text, without markup injection");
        var uiShortcut = ShortcutStore.All.Last();
        await core.ExecuteScriptAsync("chrome.webview.postMessage({type:'shortcut-move', id:'" + uiShortcut.Id + "', direction:-1})");
        await Until(() => ShortcutStore.All[^2].Id == uiShortcut.Id, "Reorder through the native bridge");
        Check(true, "Reordering is persisted by the browser bridge");
        await core.ExecuteScriptAsync("chrome.webview.postMessage({type:'shortcut-remove', id:'" + uiShortcut.Id + "'})");
        await ScriptUntil(core, "document.querySelectorAll('.tile').length === 7", "Removing a shortcut updates the live page");

        File.WriteAllText(Path.Combine(AppPaths.Assets, "smoke-page.html"), "<!doctype html><html><head><title>Local smoke page</title><link rel='icon' href='site-icons/github.ico'></head><body>Vela smoke Vela smoke Vela smoke <input></body></html>");
        core.SetVirtualHostNameToFolderMapping("smoke.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        var history = (HistoryStore)typeof(MainWindow).GetField("_history", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var bookmarks = (BookmarksStore)typeof(MainWindow).GetField("_bookmarks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        history.Add("https://smoke.test/smoke-page.html", "History smoke");
        bookmarks.Toggle("https://smoke.test/smoke-page.html", "Bookmark smoke");
        var omnibox = (TextBox)window.FindName("Omnibox");
        omnibox.Focus();
        omnibox.Text = "smoke";
        Invoke(window, "RefreshSuggestions");
        var list = (ListBox)window.FindName("SuggestionsList");
        Check(list.Items.Count == 1, "Address suggestions combine and deduplicate local history and bookmarks");
        Check((bool)Invoke(window, "HandleSuggestionKey", Key.Down)!, "Down arrow selects a local suggestion");
        Check((bool)Invoke(window, "HandleSuggestionKey", Key.Enter)!, "Enter accepts the selected suggestion");
        await ScriptUntil(core, "document.title === 'Local smoke page'", "Selected suggestion navigates to its full URL");
        _ = (Task)Invoke(window, "ShowFindAsync")!;
        var environment = Field<CoreWebView2Environment>(window, "_env");
        var find = environment.CreateFindOptions();
        find.FindTerm = "Vela smoke";
        find.ShouldHighlightAllMatches = true;
        await core.Find.StartAsync(find).WaitAsync(TimeSpan.FromSeconds(15));
        Check(core.Find.MatchCount == 3, "Native find counts all three matches");
        var oldIndex = core.Find.ActiveMatchIndex;
        core.Find.FindNext();
        await Until(() => core.Find.ActiveMatchIndex != oldIndex, "Find next");
        Check(true, "Native find navigates between matches");
        core.Find.Stop();
        await Until(() => ShortcutStore.IconUrl(ShortcutStore.All.Single(s => s.Name == "Smoke"))!.StartsWith("https://vela.icons"), "Cache visited shortcut favicon");
        Check(true, "Visited shortcut favicon is cached as a local PNG");

        Invoke(window, "SetSiteProtection", "smoke.test", true);
        var other = await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        original.LastActiveUtc = DateTime.UtcNow.AddHours(-1);
        Invoke(window, "OnSleepTick", null, EventArgs.Empty);
        await Task.Delay(200);
        Check(!original.IsSuspended && original.View is not null, "Protected background site stays awake");
        Invoke(window, "DiscardTab", original, false);
        Check(original.View is not null, "Memory eviction respects site protection");
        Invoke(window, "SetSiteProtection", "smoke.test", false);
        Invoke(window, "OnSleepTick", null, EventArgs.Empty);
        await Until(() => original.View is null, "Unprotected tab is eligible for eviction");
        Check(true, "Removing protection restores automatic eviction");
        var otherCore = other.View!.CoreWebView2;
        await ScriptUntil(otherCore, "document.querySelector('.tile img[src^=\"https://vela.icons/\"]')?.complete === true", "Cached custom logo loads in another New Tab without a network request");

        await DownloadTests(window, other, otherCore);
        Invoke(window, "SaveSession");
        var saved = SessionStore.Load()!;
        Check(saved.Tabs.Count == 2 && saved.Tabs.Any(t => t.Url.Contains("smoke.test")), "Live session snapshot includes discarded tabs");
        var second = new MainWindow(environment);
        second.Show();
        var secondTabs = Field<ObservableCollection<BrowserTab>>(second, "_tabs");
        await Until(() => secondTabs.Count > 0 && secondTabs[0].View?.CoreWebView2 is not null, "Second window starts");
        Check(secondTabs.Count == 1, "Opening another window does not duplicate the restored session");
        Invoke(second, "SaveSession");
        Check(SessionStore.Load()!.Tabs.Count == 3, "Snapshots aggregate all open windows");
        await CloseWindowAsync(second);
        Check(SessionStore.Load()!.Tabs.Count == 2, "Closing one window preserves the other window's session");
        await AdditionalBrowserTests(window, other);
    }

    private static async Task CloseWindowAsync(Window window)
    {
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task DownloadTests(MainWindow window, BrowserTab tab, CoreWebView2 core)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var requestReader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await requestReader.ReadLineAsync())) { }
            var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=vela-smoke.bin\r\nContent-Length: 262144\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            var chunk = new byte[8192];
            for (var i = 0; i < 32; i++) { await stream.WriteAsync(chunk); await Task.Delay(25); }
        });
        CoreWebView2DownloadOperation? download = null;
        var completed = new TaskCompletionSource();
        core.DownloadStarting += (_, e) =>
        {
            e.ResultFilePath = Path.Combine(AppPaths.Data, "vela-smoke.bin");
            download = e.DownloadOperation;
            download.StateChanged += (_, _) =>
            {
                if (download.State == CoreWebView2DownloadState.Completed) completed.TrySetResult();
                else if (download.State == CoreWebView2DownloadState.Interrupted) completed.TrySetException(new IOException(download.InterruptReason.ToString()));
            };
        };
        core.Navigate($"http://127.0.0.1:{port}/download");
        await Until(() => download is not null || server.IsFaulted, "DownloadStarting; source=" + core.Source);
        if (server.IsFaulted) await server;
        Check((bool)Invoke(window, "HasPendingDownload", tab)!, "Browser tracks in-progress downloads");
        Check((bool)Invoke(window, "IsMemoryProtected", tab)!, "Downloading tab is protected from automatic eviction");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await server;
        Check(new FileInfo(Path.Combine(AppPaths.Data, "vela-smoke.bin")).Length == 262144, "Download completes with the exact byte count");
        Invoke(window, "Downloads_Click", null, new RoutedEventArgs());
        Check(core.IsDefaultDownloadDialogOpen, "Downloads button opens the native management panel");
        core.CloseDefaultDownloadDialog();
        Check(!(bool)Invoke(window, "HasPendingDownload", tab)!, "Completed download releases its memory protection");
    }
}

