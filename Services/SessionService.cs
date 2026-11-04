using System.IO;
using System.Text.Json;

namespace CrimsonBrowser.Services;

/// <summary>Window geometry and open tabs persisted between runs.</summary>
public sealed class SessionState
{
    public List<string> Urls { get; set; } = new();
    public int ActiveIndex { get; set; }
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public static class SessionService
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrimsonBrowser");

    private static readonly string FilePath = Path.Combine(Folder, "session.json");

    public static SessionState? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath));
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
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Session restore is a convenience; failing to write it must never block shutdown.
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch { }
    }
}
