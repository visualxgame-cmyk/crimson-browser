using System.Diagnostics;
using System.Text.Json;

namespace Vela.Services;

public sealed record ProcessUsage(int ProcessId, string Kind, long WorkingSetBytes, long PrivateBytes, long CpuTicks);
public sealed record PerformanceSample(DateTimeOffset CapturedAt, IReadOnlyList<ProcessUsage> Processes)
{
    public long WorkingSetBytes => Processes.Sum(p => p.WorkingSetBytes);
    public long PrivateBytes => Processes.Sum(p => p.PrivateBytes);
    public long CpuTicks => Processes.Sum(p => p.CpuTicks);
    public MemorySnapshot Memory => new(WorkingSetBytes, Processes.Count);
}

internal static class PerformanceProbe
{
    // Process IDs come directly from the shared WebView2 environment, avoiding scans of every
    // process on the computer. The application process is included exactly once.
    public static PerformanceSample Capture(IEnumerable<(int Id, string Kind)> processIds)
    {
        var rows = new List<ProcessUsage>();
        foreach (var (id, kind) in processIds.DistinctBy(p => p.Id))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                rows.Add(new(id, kind, process.WorkingSet64, process.PrivateMemorySize64, process.TotalProcessorTime.Ticks));
            }
            catch (ArgumentException) { } // Process exited after the environment snapshot.
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return new(DateTimeOffset.UtcNow, rows);
    }

    public static double CpuPercent(PerformanceSample before, PerformanceSample after)
    {
        var elapsedTicks = (after.CapturedAt - before.CapturedAt).Ticks;
        if (elapsedTicks <= 0) return 0;
        var earlier = before.Processes.ToDictionary(p => p.ProcessId, p => p.CpuTicks);
        long used = 0;
        foreach (var process in after.Processes)
            if (earlier.TryGetValue(process.ProcessId, out var ticks)) used += Math.Max(0, process.CpuTicks - ticks);
        return Math.Clamp(100d * used / elapsedTicks / Environment.ProcessorCount, 0, 100);
    }

    public static string DiagnosticJson(PerformanceSample sample, double cpuPercent, int tabs, int liveTabs, int sleepingTabs, string engineVersion) =>
        JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            capturedAt = sample.CapturedAt,
            applicationVersion = AppConstants.Version,
            engineVersion,
            osVersion = Environment.OSVersion.VersionString,
            logicalProcessors = Environment.ProcessorCount,
            workingSetBytes = sample.WorkingSetBytes,
            privateBytes = sample.PrivateBytes,
            cpuPercent,
            tabs, liveTabs, sleepingTabs,
            // Deliberate allowlist: no URLs, titles, paths, usernames, cookies or raw logs.
            processes = sample.Processes.Select(p => new { kind = p.Kind, workingSetBytes = p.WorkingSetBytes, privateBytes = p.PrivateBytes })
        }, new JsonSerializerOptions { WriteIndented = true });
}
