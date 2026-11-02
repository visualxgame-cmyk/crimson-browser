using System.IO;
using System.Text.Json;

namespace Vela.Services;

internal static class ManagedExtensions
{
    private static string IndexPath => Path.Combine(AppPaths.Root, "Extensions", "installed.json");
    internal static Dictionary<string, string> Read()
    {
        foreach (var path in new[] { IndexPath, IndexPath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException) { Log.Write("Read installed extensions: " + ex.Message); }
        }
        return new();
    }
    internal static void Add(string id, string folder)
    { var index = Read(); index[id] = Path.GetFileName(folder); AtomicFile.Write(IndexPath, JsonSerializer.Serialize(index)); }
    internal static void Remove(string id)
    {
        var index = Read();
        if (!index.Remove(id, out var name)) return;
        AtomicFile.Write(IndexPath, JsonSerializer.Serialize(index));
        if (!name.StartsWith("ext-", StringComparison.Ordinal) || !Guid.TryParseExact(name[4..], "N", out _)) return;
        var root = Path.GetFullPath(Path.Combine(AppPaths.Root, "Extensions"));
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (Path.GetDirectoryName(path) != root) throw new InvalidDataException("Calea extensiei nu este validă.");
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException ex) { Log.Write("Extension is removed; cache files remain locked: " + ex.Message); }
    }
    internal static bool TryGetFolder(string id, out string folder)
    {
        folder = "";
        if (!Read().TryGetValue(id, out var name) || !name.StartsWith("ext-", StringComparison.Ordinal) || !Guid.TryParseExact(name[4..], "N", out _)) return false;
        var root = Path.GetFullPath(Path.Combine(AppPaths.Root, "Extensions"));
        var candidate = Path.GetFullPath(Path.Combine(root, name));
        if (Path.GetDirectoryName(candidate) != root || !Directory.Exists(candidate)) return false;
        folder = candidate;
        return true;
    }
}
