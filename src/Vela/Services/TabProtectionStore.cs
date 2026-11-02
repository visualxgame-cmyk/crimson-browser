using System.IO;
using System.Text.Json;

namespace Vela.Services;

/// <summary>Explicit host exceptions, shared by every window. Subdomains remain independent.</summary>
internal static class TabProtectionStore
{
    static TabProtectionStore() { }
    private static readonly string FilePath = Path.Combine(AppPaths.Data, "protected-sites.json");
    private static readonly HashSet<string> Hosts = Load();

    public static IReadOnlyList<string> All => Hosts.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.Host != AppConstants.InternalHost ? uri.IdnHost : "";
    public static bool Contains(string url) => Hosts.Contains(Host(url));

    public static void Set(string host, bool protect)
    {
        var next = new HashSet<string>(Hosts, StringComparer.OrdinalIgnoreCase);
        if (protect) next.Add(host); else next.Remove(host);
        AtomicFile.Write(FilePath, JsonSerializer.Serialize(next));
        Hosts.Clear();
        Hosts.UnionWith(next);
    }

    private static HashSet<string> Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
                if (values is not null) return new(values.Where(h => !string.IsNullOrWhiteSpace(h)), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log.Write("Read protected sites: " + ex.Message); }
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }
}
