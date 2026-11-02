using Vela.Services;

namespace Vela;

internal sealed record RendererUsage(int ProcessId, string Pages, double RamMB, double CpuPercent, bool Shared);

public partial class MainWindow
{
    private bool _memoryProbeRunning;
    private (int Id, string Kind)[] BrowserProcessIds() => _env is null ? [(Environment.ProcessId, "Application")] :
        _env.GetProcessInfos().Select(p => ((int)p.ProcessId, p.Kind.ToString())).Append((Environment.ProcessId, "Application")).ToArray();

    internal async Task<string> CaptureDiagnosticAsync()
    {
        var initialIds = BrowserProcessIds();
        var before = await Task.Run(() => PerformanceProbe.Capture(initialIds));
        await Task.Delay(1000);
        var ids = BrowserProcessIds();
        var after = await Task.Run(() => PerformanceProbe.Capture(ids));
        return PerformanceProbe.DiagnosticJson(after, PerformanceProbe.CpuPercent(before, after), _tabs.Count,
            _tabs.Count(t => t.View is not null), _tabs.Count(t => t.IsSuspended), _env?.BrowserVersionString ?? "Unavailable");
    }
    internal async Task<IReadOnlyList<RendererUsage>> CaptureRendererUsageAsync()
    {
        if (_env is null) throw new InvalidOperationException("Motorul nu este încă pornit.");
        var tabs = LibraryTabs.Where(t => t.View?.CoreWebView2 is not null).ToArray();
        var frames = tabs.GroupBy(t => t.View!.CoreWebView2.FrameId).ToDictionary(g => g.Key, g => g.First());
        var extended = await _env.GetProcessExtendedInfosAsync();
        var owners = new Dictionary<int, (string Label, bool Shared)>();
        foreach (var process in extended)
        {
            var pageIds = new HashSet<uint>(); bool unknown = false;
            foreach (var frame in process.AssociatedFrameInfos)
            {
                var root = frame; var depth = 0;
                while (root.ParentFrameInfo is { } parent && depth++ < 64) root = parent;
                if (frames.ContainsKey(root.FrameId)) pageIds.Add(root.FrameId); else unknown = true;
            }
            var titles = pageIds.Select(id => frames[id].Title).ToArray();
            owners[(int)process.ProcessInfo.ProcessId] = (titles.Length > 0 ? string.Join(" · ", titles) + (unknown ? " · alte pagini" : "") : process.ProcessInfo.Kind + " — motor / alte ferestre", titles.Length > 1 || unknown);
        }
        var ids = extended.Select(p => ((int)p.ProcessInfo.ProcessId, p.ProcessInfo.Kind.ToString())).Append((Environment.ProcessId, "Vela")).ToArray();
        var before = await Task.Run(() => PerformanceProbe.Capture(ids));
        await Task.Delay(1000); // Explicit, user-requested CPU sampling window.
        var after = await Task.Run(() => PerformanceProbe.Capture(ids));
        var elapsed = (after.CapturedAt - before.CapturedAt).TotalSeconds;
        var previous = before.Processes.ToDictionary(p => p.ProcessId, p => p.CpuTicks);
        return after.Processes.Select(p => new RendererUsage(p.ProcessId,
            p.ProcessId == Environment.ProcessId ? "Interfața Vela" : owners.GetValueOrDefault(p.ProcessId, (Label: "Motor", Shared: false)).Label,
            Math.Round(p.WorkingSetBytes / 1048576d, 1),
            Math.Round(previous.TryGetValue(p.ProcessId, out var cpu) && elapsed > 0 ? Math.Max(0, p.CpuTicks - cpu) / (elapsed * TimeSpan.TicksPerSecond * Environment.ProcessorCount) * 100 : 0, 2),
            owners.GetValueOrDefault(p.ProcessId).Shared)).OrderByDescending(p => p.RamMB).ToArray();
    }
}
