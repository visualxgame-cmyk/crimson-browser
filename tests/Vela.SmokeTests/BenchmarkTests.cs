using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Vela.Models;
using Vela.Services;
using Microsoft.Web.WebView2.Core;

namespace Vela.Tests;

internal static partial class Program
{
    private static async Task BenchmarkAsync()
    {
        var startup = Stopwatch.StartNew();
        var window = new MainWindow();
        window.Show();
        var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Initialize benchmark window");
        var env = Field<CoreWebView2Environment>(window, "_env");
        await ScriptUntil(tabs[0].View!.CoreWebView2, "document.readyState === 'complete'", "Benchmark first page loaded");
        startup.Stop();
        var rows = new List<object>();
        (int Id, string Kind)[] ProcessIds() => env.GetProcessInfos().Select(p => ((int)p.ProcessId, p.Kind.ToString()))
            .Append((Environment.ProcessId, "Application")).ToArray();
        foreach (var count in new[] { 1, 10, 30 })
        {
            while (tabs.Count < count)
                await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, false, null)!;
            foreach (var tab in tabs.ToArray())
            {
                await (Task)Invoke(window, "ActivateTabAsync", tab)!;
                if (tab.View?.CoreWebView2 is { } core)
                    await ScriptUntil(core, "document.readyState === 'complete'", "Benchmark local page loaded");
            }
            await (Task)Invoke(window, "EnforceLiveTabLimitAsync")!;
            var before = PerformanceProbe.Capture(ProcessIds());
            await Task.Delay(1000); // A defined one-second CPU sampling interval, not a stabilization workaround.
            var after = PerformanceProbe.Capture(ProcessIds());
            var live = tabs.Count(t => t.View is not null);
            Check(live <= 6, "Default policy limits live views with " + count + " tabs");
            rows.Add(new { tabs = count, liveTabs = live, discardedTabs = tabs.Count - live,
                ramMB = Math.Round(after.WorkingSetBytes / 1048576d, 1), privateMB = Math.Round(after.PrivateBytes / 1048576d, 1),
                cpuPercentOneSecondAfterNavigation = Math.Round(PerformanceProbe.CpuPercent(before, after), 2), processes = after.Processes.Count });
        }
        var report = new { fixture = "Bundled offline New Tab; local assets only", measuredAt = DateTimeOffset.UtcNow,
            firstWindowAndPageMilliseconds = startup.ElapsedMilliseconds, engineVersion = env.BrowserVersionString, results = rows };
        var path = Environment.GetEnvironmentVariable("VELA_BENCHMARK_OUTPUT") ?? Path.Combine(AppPaths.Root, "benchmark.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Benchmark report: " + path);
        await CloseWindowAsync(window);
    }
}
