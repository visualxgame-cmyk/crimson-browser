using System.IO;
using System.Text.Json;

namespace Vela.Services;

public sealed record SessionTab(string Url, string Title)
{
    public bool IsPinned { get; init; }
    public bool IsMuted { get; init; }
    public string GroupName { get; init; } = "";
    public bool GroupCollapsed { get; init; }
}

public sealed class SessionState
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class SessionStore
{
    static SessionStore() { }
    private static readonly string FilePath = Path.Combine(AppPaths.Data, "session.json");
    // UI-thread owned: each window contributes instead of overwriting other windows.
    private static readonly Dictionary<Guid, SessionState> Windows = new();
    private static bool _restorationClaimed;

    public static bool ClaimRestoration()
    {
        if (_restorationClaimed) return false;
        _restorationClaimed = true;
        return true;
    }

    public static SessionState? Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path));
                if (state?.Tabs is not { Count: > 0 }) continue;
                state.Tabs.RemoveAll(t => t is null || string.IsNullOrWhiteSpace(t.Url));
                if (state.Tabs.Count > 0) return state;
            }
            catch (Exception ex) { Log.Write("Read session: " + ex.Message); }
        }
        return null;
    }

    public static void UpdateWindow(Guid id, SessionState state)
    {
        if (state.Tabs.Count == 0) return;
        Windows[id] = state;
        SaveWindows(id);
    }

    public static void CloseWindow(Guid id)
    {
        if (Windows.Count > 1)
        {
            Windows.Remove(id);
            SaveWindows(Windows.Keys.First());
        }
        else Windows.Remove(id); // Preserve the last window on disk for the next launch.
    }

    private static void SaveWindows(Guid activeWindow)
    {
        var combined = new SessionState();
        foreach (var (id, state) in Windows)
        {
            if (id == activeWindow) combined.ActiveIndex = combined.Tabs.Count + Math.Clamp(state.ActiveIndex, 0, state.Tabs.Count - 1);
            combined.Tabs.AddRange(state.Tabs);
        }
        try { AtomicFile.Write(FilePath, JsonSerializer.Serialize(combined)); }
        catch (Exception ex) { Log.Write("Save session: " + ex.Message); }
    }

    public static void Clear()
    {
        Windows.Clear();
        try
        {
            foreach (var path in new[] { FilePath, FilePath + ".bak", FilePath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { Log.Write("Clear session: " + ex.Message); }
    }
}
