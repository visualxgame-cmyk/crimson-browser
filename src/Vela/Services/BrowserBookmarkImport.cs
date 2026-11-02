using System.IO;
using System.Text.Json;

namespace Vela.Services;

internal sealed record ImportSource(string Name, string Path);
internal static class BrowserBookmarkImport
{
    internal static IReadOnlyList<ImportSource> Discover()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var result = new List<ImportSource>();
        foreach (var (name, relative) in new[] { ("Chrome", @"Google\Chrome\User Data"), ("Edge", @"Microsoft\Edge\User Data"), ("Brave", @"BraveSoftware\Brave-Browser\User Data") })
        {
            var root = System.IO.Path.Combine(local, relative);
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root).Take(64))
                {
                    var profile = System.IO.Path.GetFileName(directory);
                    if (profile != "Default" && !profile.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                    var path = System.IO.Path.Combine(directory, "Bookmarks");
                    if (File.Exists(path)) result.Add(new(name + " — " + profile, path));
                }
            }
            catch (IOException ex) { Log.Write("Discover bookmark sources: " + ex.Message); }
            catch (UnauthorizedAccessException) { /* A source owned by another Windows account is unavailable. */ }
        }
        return result;
    }
    internal static (List<Bookmark> Entries, int Skipped) ParseChromium(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > BookmarkHtml.MaxFileBytes) throw new InvalidDataException("Fișierul depășește 16 MB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 100 });
        if (!document.RootElement.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Fișierul nu este o bibliotecă Chromium Bookmarks.");
        var items = new List<Bookmark>(); int skipped = 0, visited = 0;
        void Walk(JsonElement node, string folder, int depth)
        {
            if (++visited > 50000 || depth > 32) throw new InvalidDataException("Biblioteca are prea multe elemente sau foldere imbricate.");
            var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
            var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (type == "folder")
            {
                var path = string.IsNullOrWhiteSpace(name) ? folder : string.IsNullOrWhiteSpace(folder) ? name : folder + "/" + name;
                if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                    foreach (var child in children.EnumerateArray()) Walk(child, path, depth + 1);
            }
            else if (type == "url")
            {
                var url = node.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (url is null || url.Length > 8192 || name.Length > 500 || folder.Length > 240 ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
                    uri.Host is AppConstants.InternalHost or AppConstants.BackgroundHost or ShortcutStore.IconHost) { skipped++; return; }
                if (items.Count >= BookmarkHtml.MaxEntries) throw new InvalidDataException("Maximum 20.000 de favorite la un import.");
                var added = DateTimeOffset.UtcNow;
                if (node.TryGetProperty("date_added", out var date) && long.TryParse(date.GetString(), out var microseconds))
                    try { added = DateTimeOffset.FromFileTime(checked(microseconds * 10)); } catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException) { }
                items.Add(new(uri.AbsoluteUri, string.IsNullOrWhiteSpace(name) ? uri.Host : name, added) { Folder = folder });
            }
        }
        foreach (var root in roots.EnumerateObject()) if (root.Value.ValueKind == JsonValueKind.Object) Walk(root.Value, "", 0);
        return (items, skipped);
    }
    internal static (List<Bookmark> Entries, int Skipped) Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > BookmarkHtml.MaxFileBytes) throw new InvalidDataException("Fișierul depășește 16 MB.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var content = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        return content.TrimStart().StartsWith('{') ? ParseChromium(content) : BookmarkHtml.Parse(content);
    }
}
