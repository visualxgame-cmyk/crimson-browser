using System.IO;
using System.Text.Json;

namespace Vela.Services;

public sealed record SessionTab(string Url, string Title);

public sealed class SessionState
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class SessionStore
{
    private static readonly string FilePath = Path.Combine(AppPaths.Data, "session.json");

    public static SessionState? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath));
            return state?.Tabs.Count > 0 ? state : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(SessionState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { }
    }
}
