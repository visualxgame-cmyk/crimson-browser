using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using Vela.Models;
using Vela.Services;

namespace Vela.Tests;

internal static partial class Program
{
    private static async Task ExpansionTests(MainWindow window)
    {
        var settings = new Settings { MaxLiveTabs = 8, SuspendAfterMinutes = 10, DiscardAfterMinutes = 30, EconomyMode = true };
        Check(settings.EffectiveMaxLiveTabs == 3 && settings.EffectiveSuspendMinutes == 1 && settings.EffectiveDiscardMinutes == 5, "Economy mode sets a bounded policy without changing custom preferences");
        settings.EconomyMode = false;
        Check(settings.EffectiveMaxLiveTabs == 8 && settings.EffectiveSuspendMinutes == 10 && settings.EffectiveDiscardMinutes == 30, "Disabling Economy restores all customized limits");
        var beforeSettings = window.LibrarySettings; var economy = beforeSettings.Clone(); economy.EconomyMode = true;
        Invoke(window, "CommitSettings", economy);
        var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        var grouped = new List<BrowserTab>();
        for (var i = 0; i < 5; i++)
        {
            var added = await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
            await ScriptUntil(added.View!.CoreWebView2, "document.readyState==='complete'", "Economy fixture loads"); grouped.Add(added);
        }
        await (Task)Invoke(window, "EnforceLiveTabLimitAsync")!;
        Check(grouped.Count(t => t.View is not null) <= 3, "Economy enforces three live controls across newly activated pages");
        foreach (var tab in grouped) window.SetTabGroup(tab, "Documentare");
        window.ToggleGroup("Documentare");
        Check(grouped.Count(t => t.HeaderVisible) == 2 && grouped.Last().IsSelected, "Collapsed groups preserve their leader and active tab");
        var liveBefore = tabs.Count(t => t.View is not null);
        window.ToggleGroup("Documentare");
        Check(grouped.All(t => t.HeaderVisible) && tabs.Count(t => t.View is not null) == liveBefore, "Expanding a group reveals headers without loading its pages");
        Invoke(window, "SaveSession");
        Check(SessionStore.Load()!.Tabs.Count(t => t.GroupName == "Documentare") == 5, "Tab grouping persists in recovery snapshots");
        window.PinTab(grouped[0], true);
        Check(grouped[0].GroupName.Length == 0 && grouped[0].HeaderVisible, "Pinning a grouped tab safely removes its grouping");
        var imported = BrowserBookmarkImport.ParseChromium("""{"roots":{"bookmark_bar":{"type":"folder","name":"Lucru","children":[{"type":"folder","name":"Știri","children":[{"type":"url","name":"Articol","url":"https://import.test/article","date_added":"13344473600000000"},{"type":"url","name":"Invalid","url":"javascript:alert(1)"}]}]}}} """);
        Check(imported.Entries.Count == 1 && imported.Skipped == 1 && imported.Entries[0].Folder == "Lucru/Știri", "Chromium import preserves nested folders and rejects executable URLs");
        var first = MainWindow.Bookmarks.ImportEntries(imported.Entries); var repeated = MainWindow.Bookmarks.ImportEntries(imported.Entries);
        Check(first.Added == 1 && repeated.Added == 0 && repeated.Duplicates == 1, "Cross-browser imports merge favorites without duplicates");
        var readingId = MainWindow.ReadingList.Save(null, "De citit", "https://reading-list.test/article", "");
        MainWindow.ReadingList.SetRead(readingId, true);
        Check(new BookmarksStore("reading-list.json").All.Single(b => b.Id == readingId).IsRead, "Reading list read state survives persistence");
        var transfer = window.CreateTransfer("sync");
        Check(transfer.ReadingList!.Any(b => b.Id == readingId && b.IsRead) && transfer.Preferences.EconomyMode, "Encrypted transfer includes the reading list and Economy preference");
        var resources = await window.CaptureRendererUsageAsync();
        Check(resources.Count > 0 && resources.Select(p => p.ProcessId).Distinct().Count() == resources.Count && resources.All(p => p.RamMB >= 0 && p.CpuPercent >= 0), "Resource measurements list each native process exactly once");
        Check(tabs.Count(t => t.View is not null) == liveBefore, "Resource measurement never loads discarded tabs");
        var hub = new WorkspaceWindow(window, "profiles") { Owner = window }; hub.Show();
        foreach (var mode in new[] { "import", "resources", "groups", "profiles" }) hub.ShowMode(mode);
        Check(tabs.Count(t => t.View is not null) == liveBefore, "Workspace pages open without creating renderers");
        foreach (var scale in new[] { 1.25, 1.5, 2.0 })
        {
            var layout = new WorkspaceWindow(window, "groups"); WindowSizing.FitAvailable(layout, 1920 / scale, 1080 / scale);
            Check(layout.Width <= 1920 / scale && layout.Height <= 1080 / scale && layout.MinWidth <= layout.Width, "Workspace bounds fit a 1080p work area at " + scale * 100 + "% scaling");
            layout.Close();
        }
        hub.Close();
        Invoke(window, "CommitSettings", beforeSettings);
        await SupersededNavigationTest(window, grouped.Last());
        await LargeDownloadTest(window, grouped.Last());
        await ProfileIsolationTests();
    }
    private static async Task ProfileIsolationTests()
    {
        var root = Path.Combine(AppPaths.Root, "ProfileIsolation"); var catalog = new ProfileCatalog(root);
        var profile = catalog.Save(null, "Lucru"); catalog.Save(profile.Id, "Personal");
        Check(catalog.All.Single(p => p.Id == profile.Id).Name == "Personal" && catalog.All.Any(p => p.Id == "default"), "Profile creation and rename preserve the existing default identity");
        var rejected = false; try { ProfileContext.Initialize(["--profile=../../outside"], root); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Profile arguments cannot escape the profile directory");
        foreach (var (id, phase) in new[] { ("default", "seed"), (profile.Id, "seed"), ("default", "verify"), (profile.Id, "verify") })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--profile-fixture"); start.ArgumentList.Add("--profile=" + id); start.ArgumentList.Add("--phase=" + phase);
            start.Environment["VELA_SMOKE_DATA"] = root;
            using var child = Process.Start(start)!; var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); Console.Write(await output);
            if (child.ExitCode != 0) throw new InvalidOperationException(await error);
            Check(true, "Isolated profile child passed: " + (id == "default" ? "default" : "named") + " / " + phase);
        }
    }
    private static async Task ProfileChildTests()
    {
        var phase = Environment.GetCommandLineArgs().Single(a => a.StartsWith("--phase="))[8..];
        var expected = ProfileContext.Id;
        var store = new BookmarksStore(); var settings = SettingsStore.Load();
        if (phase == "seed") Check(store.All.Count == 0, "New profile starts with an empty independent favorites library");
        else Check(store.All.Single().Title == expected && settings.HomePage == "https://" + expected + ".profile.test", "Profile settings and favorites remain isolated across restarts");
        var window = new MainWindow(); window.Show(); var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Profile native engine starts", 30000);
        var core = tabs[0].View!.CoreWebView2;
        await ScriptUntil(core, "location.hostname==='vela.internal' && document.readyState==='complete'", "Profile startup remains local");
        var cookies = await core.CookieManager.GetCookiesAsync("https://profile-isolation.test");
        if (phase == "seed")
        {
            Check(cookies.Count == 0, "New profile cannot read another profile's cookies");
            var cookie = core.CookieManager.CreateCookie("marker", expected, "profile-isolation.test", "/"); cookie.Expires = DateTime.UtcNow.AddDays(1); core.CookieManager.AddOrUpdateCookie(cookie);
            MainWindow.Bookmarks.Save(null, expected, "https://profile-marker.test", "");
            settings.HomePage = "https://" + expected + ".profile.test"; settings.StartupMode = "newtab";
            Invoke(window, "CommitSettings", settings);
        }
        else Check(cookies.Single(c => c.Name == "marker").Value == expected, "Persistent cookies remain isolated across browser processes");
        await CloseWindowAsync(window);
    }
    private static async Task SupersededNavigationTest(MainWindow window, BrowserTab tab)
    {
        await (Task)Invoke(window, "ActivateTabAsync", tab)!;
        var core = tab.View!.CoreWebView2;
        using var first = new TcpListener(IPAddress.Loopback, 0); first.Start();
        using var second = new TcpListener(IPAddress.Loopback, 0); second.Start();
        var firstArrived = new TaskCompletionSource(); var secondArrived = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource(); var releaseSecond = new TaskCompletionSource();
        Task Serve(TcpListener listener, TaskCompletionSource arrived, TaskCompletionSource release, string title) => Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(); await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true); while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            arrived.SetResult(); await release.Task;
            var body = Encoding.UTF8.GetBytes("<!doctype html><title>" + title + "</title><p>Navigation fixture</p>");
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(body);
            }
            catch (IOException) when (title == "First navigation") { /* The first client is intentionally canceled. */ }
        });
        var servingFirst = Serve(first, firstArrived, releaseFirst, "First navigation");
        var servingSecond = Serve(second, secondArrived, releaseSecond, "Second navigation");
        var canceled = new TaskCompletionSource(); ulong firstId = 0;
        var firstUrl = $"http://127.0.0.1:{((IPEndPoint)first.LocalEndpoint).Port}/first";
        var secondUrl = $"http://127.0.0.1:{((IPEndPoint)second.LocalEndpoint).Port}/second";
        EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs> starting = (_, e) => { if (e.Uri == firstUrl) firstId = e.NavigationId; };
        EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs> completed = (_, e) => { if (e.NavigationId == firstId && !e.IsSuccess) canceled.TrySetResult(); };
        core.NavigationStarting += starting; core.NavigationCompleted += completed;
        try
        {
            core.Navigate(firstUrl); await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            core.Navigate(secondUrl); await secondArrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(tab.IsLoading && tab.Url == secondUrl, "A superseded navigation cannot clear the replacement page's loading state");
            releaseSecond.SetResult();
            await ScriptUntil(core, "document.title==='Second navigation' && document.readyState==='complete'", "Replacement navigation completes normally after the first request is canceled");
        }
        finally
        {
            core.NavigationStarting -= starting; core.NavigationCompleted -= completed;
            releaseFirst.TrySetResult(); releaseSecond.TrySetResult();
            await Task.WhenAll(servingFirst, servingSecond).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
    private static async Task LargeDownloadTest(MainWindow window, BrowserTab tab)
    {
        await (Task)Invoke(window, "ActivateTabAsync", tab)!; var core = tab.View!.CoreWebView2;
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start(); var port = ((IPEndPoint)server.LocalEndpoint).Port;
        const int size = 64 * 1024 * 1024;
        var serving = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(); await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true); while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=large-fixture.bin\r\nContent-Length: {size}\r\nConnection: close\r\n\r\n"));
            var chunk = new byte[128 * 1024]; for (var sent = 0; sent < size; sent += chunk.Length) await stream.WriteAsync(chunk);
        });
        var completed = new TaskCompletionSource(); var path = Path.Combine(AppPaths.Data, "large-fixture.bin");
        EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2DownloadStartingEventArgs> handler = (_, e) =>
        {
            e.ResultFilePath = path; e.Handled = true; var operation = e.DownloadOperation;
            operation.StateChanged += (_, _) => { if (operation.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Completed) completed.TrySetResult(); else if (operation.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Interrupted) completed.TrySetException(new IOException(operation.InterruptReason.ToString())); };
        };
        core.DownloadStarting += handler;
        try { core.Navigate($"http://127.0.0.1:{port}/fixture"); await completed.Task.WaitAsync(TimeSpan.FromSeconds(45)); await serving; Check(new FileInfo(path).Length == size, "A 64 MB download completes with a bounded 128 KB server buffer"); }
        finally { core.DownloadStarting -= handler; }
    }
}
