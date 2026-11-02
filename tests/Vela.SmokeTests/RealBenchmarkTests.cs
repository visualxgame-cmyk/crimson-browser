using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Vela.Models;
using Vela.Services;

namespace Vela.Tests;

internal static partial class Program
{
    private static async Task RealBenchmarkAsync(bool economy)
    {
        SettingsStore.Save(new Settings { EconomyMode = economy, StartupMode = "newtab" });
        var window = new MainWindow(); window.Show();
        var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Benchmark starts", 30000);
        await (Task)Invoke(window, "WaitForViewInitializationAsync")!;
        await ScriptUntil(tabs[0].View!.CoreWebView2, "location.hostname==='vela.internal' && document.readyState==='complete'", "Benchmark startup completes before public navigation");
        var environment = window.BrowserEnvironment!;
        var urls = new[] { "https://www.wikipedia.org/", "https://learn.microsoft.com/en-us/microsoft-edge/webview2/", "https://example.com/", "https://developer.mozilla.org/en-US/", "https://github.com/visualxstudio/vela-browser", "https://www.youtube.com/" };
        var results = new List<object>(); var failures = new List<object>();
        (int, string)[] Ids() => environment.GetProcessInfos().Select(p => ((int)p.ProcessId, p.Kind.ToString())).Append((Environment.ProcessId, "Application")).ToArray();
        async Task<object> Sample(string stage)
        {
            var before = PerformanceProbe.Capture(Ids());
            await Task.Delay(5000); // A fixed measurement window, including any residual page activity.
            var after = PerformanceProbe.Capture(Ids());
            var live = tabs.Count(t => t.View is not null);
            var previous = before.Processes.ToDictionary(p => p.ProcessId, p => p.CpuTicks);
            var elapsed = (after.CapturedAt - before.CapturedAt).TotalSeconds;
            return new { stage, tabs = tabs.Count, live, suspended = tabs.Count(t => t.IsSuspended), discarded = tabs.Count - live,
                nativeSuspended = tabs.Count(t => t.View?.CoreWebView2?.IsSuspended == true),
                processesCpu = after.Processes.Select(p => new { p.ProcessId, p.Kind, cpuPercent = Math.Round(previous.TryGetValue(p.ProcessId, out var ticks) && elapsed > 0 ? Math.Max(0, p.CpuTicks - ticks) / (elapsed * TimeSpan.TicksPerSecond * Environment.ProcessorCount) * 100 : 0, 2) }),
                protectedTabs = tabs.Count(t => (bool)Invoke(window, "IsMemoryProtected", t)!), ramMB = Math.Round(after.WorkingSetBytes / 1048576d, 1),
                protectionReasons = new { unload = tabs.Count(t => t.HasUnloadHandler), edits = tabs.Count(t => t.HasUnsavedChanges), media = tabs.Count(t => t.HasActiveMedia) },
                privateMB = Math.Round(after.PrivateBytes / 1048576d, 1), cpuPercent = Math.Round(PerformanceProbe.CpuPercent(before, after), 2), processes = after.Processes.Count };
        }
        foreach (var count in new[] { 1, 10, 30 })
        {
            while (tabs.Count < count) await (Task<BrowserTab>)Invoke(window, "AddTabAsync", urls[(tabs.Count - 1) % urls.Length], false, null)!;
            foreach (var tab in tabs.ToArray())
            {
                try
                {
                    await (Task)Invoke(window, "ActivateTabAsync", tab)!;
                    if (count == 1) { tabs[0].View!.CoreWebView2.Navigate(urls[0]); await ScriptUntil(tabs[0].View!.CoreWebView2, "location.hostname==='www.wikipedia.org' && document.readyState==='complete'", "First public benchmark page loads"); }
                    var core = tab.View!.CoreWebView2;
                    var deadline = DateTime.UtcNow.AddSeconds(40);
                    while (tab.IsLoading || await core.ExecuteScriptAsync("document.readyState==='complete' && document.title.trim().length>0") != "true")
                    { if (DateTime.UtcNow > deadline) throw new TimeoutException("Page did not finish loading."); await Task.Delay(100); }
                    if (!tab.IsSecureConnection) throw new IOException("HTTPS navigation did not complete successfully.");
                }
                catch (Exception ex) { failures.Add(new { url = tab.Url, error = ex.Message }); }
            }
            await (Task)Invoke(window, "EnforceLiveTabLimitAsync")!;
            results.Add(await Sample("loaded"));
            foreach (var tab in tabs.Where(t => !t.IsSelected)) tab.LastActiveUtc = DateTime.UtcNow.AddMinutes(-2);
            await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
            results.Add(await Sample("background-suspended"));
            foreach (var tab in tabs.Where(t => !t.IsSelected)) tab.LastActiveUtc = DateTime.UtcNow.AddMinutes(economy ? -6 : -16);
            await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
            results.Add(await Sample("background-discarded"));
            Console.WriteLine($"Measured {count} real tabs; economy={economy}");
        }
        var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "benchmarks", economy ? "real-economy.json" : "real-normal.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.UtcNow, economy, engine = environment.BrowserVersionString, urls, results, failures,
            methodology = "All tabs activated sequentially. Fixed five-second CPU windows. Background age advanced explicitly to exercise the real suspension/discard policy without waiting minutes. Includes application and WebView2 processes; not a stabilized-idle or cross-browser comparison." }, new JsonSerializerOptions { WriteIndented = true }));
        // Only public test pages exist here. Teardown must not wait on their generic beforeunload dialogs.
        window.GetType().GetField("_windowCloseApproved", Private)!.SetValue(window, true);
        await CloseWindowAsync(window); Console.WriteLine("Report: " + path);
    }
}
