using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Vela.Services;

internal sealed record Shortcut(string Id, string Name, string Url);

internal static class ShortcutStore
{
    static ShortcutStore() { }
    public const int MaxItems = 24;
    public const string IconHost = "vela.icons";
    public static string IconDirectory { get; } = Path.Combine(AppPaths.Root, "ShortcutIcons");
    private static readonly string FilePath = Path.Combine(AppPaths.Data, "shortcuts.json");
    private static List<Shortcut> _items = Load();
    public static event Action? Changed;
    public static IReadOnlyList<Shortcut> All => _items;
    internal static void ValidateSnapshot(IReadOnlyList<Shortcut> items)
    {
        if (items.Count > MaxItems || items.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 100 ||
            string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 40 || s.Url.Length > 2048 || !ValidUrl(s.Url)) ||
            items.Select(s => s.Id).Distinct().Count() != items.Count) throw new InvalidDataException("Scurtăturile din backup nu sunt valide.");
    }
    internal static void Restore(IReadOnlyList<Shortcut> items) { ValidateSnapshot(items); Commit(items.ToList()); }

    private static List<Shortcut> Defaults() =>
    [
        new("youtube", "YouTube", "https://www.youtube.com"),
        new("github", "GitHub", "https://github.com"),
        new("wikipedia", "Wikipedia", "https://ro.wikipedia.org"),
        new("reddit", "Reddit", "https://www.reddit.com"),
        new("gmail", "Gmail", "https://mail.google.com"),
        new("meteoblue", "Meteo", "https://www.meteoblue.com")
    ];

    private static List<Shortcut> Load()
    {
        Directory.CreateDirectory(IconDirectory);
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var loaded = JsonSerializer.Deserialize<List<Shortcut>>(File.ReadAllText(path));
                if (loaded is null || loaded.Count > MaxItems || loaded.Any(s => s is null ||
                    string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name) || !ValidUrl(s.Url))) continue;
                if (loaded.Select(s => s.Id).Distinct().Count() != loaded.Count) continue;
                return loaded;
            }
            catch (Exception ex) { Log.Write("Read shortcuts: " + ex.Message); }
        }
        return Defaults();
    }

    private static bool ValidUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    public static void Save(string? id, string name, string url)
    {
        name = name.Trim();
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (name.Length is < 1 or > 40) throw new ArgumentException("Numele trebuie să aibă între 1 și 40 de caractere.");
        if (url.Length > 2048 || !ValidUrl(url)) throw new ArgumentException("Introdu o adresă HTTP sau HTTPS validă, fără parolă.");
        var next = new List<Shortcut>(_items);
        var index = id is null ? -1 : next.FindIndex(s => s.Id == id);
        if (id is not null && index < 0) throw new ArgumentException("Scurtătura nu mai există. Redeschide editorul.");
        if (index < 0)
        {
            if (next.Count >= MaxItems) throw new ArgumentException("Poți păstra cel mult 24 de scurtături.");
            next.Add(new(Guid.NewGuid().ToString("N"), name, url));
        }
        else next[index] = new(id!, name, url);
        Commit(next);
    }

    public static void Remove(string id) => Commit(_items.Where(s => s.Id != id).ToList());

    public static void Move(string id, int direction)
    {
        var next = new List<Shortcut>(_items);
        var index = next.FindIndex(s => s.Id == id);
        if (index < 0 || direction is not (-1 or 1)) return;
        var target = index + direction;
        if (target < 0 || target >= next.Count) return;
        (next[index], next[target]) = (next[target], next[index]);
        Commit(next);
    }

    private static void Commit(List<Shortcut> next)
    {
        AtomicFile.Write(FilePath, JsonSerializer.Serialize(next));
        _items = next;
        Changed?.Invoke();
        // Only our own hashed PNG files are eligible for cache cleanup.
        var keep = next.Select(s => IconName(s.Url)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(IconDirectory, "*.png"))
        {
            if (keep.Contains(Path.GetFileName(path))) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Log.Write("Shortcut cache cleanup: " + ex.Message); }
        }
    }

    private static string IconName(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TabProtectionStore.Host(url)))) + ".png";

    public static string? IconUrl(Shortcut shortcut)
    {
        var name = IconName(shortcut.Url);
        if (File.Exists(Path.Combine(IconDirectory, name))) return "https://" + IconHost + "/" + name;
        var bundled = TabProtectionStore.Host(shortcut.Url) switch
        {
            "youtube.com" or "www.youtube.com" => "youtube.ico",
            "github.com" or "www.github.com" => "github.ico",
            "ro.wikipedia.org" or "en.wikipedia.org" or "www.wikipedia.org" => "wikipedia.ico",
            "reddit.com" or "www.reddit.com" => "reddit.png",
            "mail.google.com" => "gmail.ico",
            "meteoblue.com" or "www.meteoblue.com" => "meteoblue.ico",
            _ => null
        };
        return bundled is null ? null : "site-icons/" + bundled;
    }

    public static void CacheIcon(string url, BitmapSource bitmap)
    {
        var host = TabProtectionStore.Host(url);
        if (host.Length == 0 || !_items.Any(s => TabProtectionStore.Host(s.Url) == host)) return;
        var path = Path.Combine(IconDirectory, IconName(url));
        if (File.Exists(path)) return; // No disk writes on every favicon update.
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path + ".tmp")) encoder.Save(stream);
            File.Move(path + ".tmp", path, overwrite: true);
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Write("Save shortcut icon: " + ex.Message); }
    }
}
