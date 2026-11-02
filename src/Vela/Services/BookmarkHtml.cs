using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Vela.Services;

internal sealed record BookmarkImportResult(int Added, int Duplicates, int Skipped);
internal static class BookmarkHtml
{
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxEntries = 20000;
    private static readonly Regex Tokens = new("<(?<tag>a|h3)\\b(?<attrs>[^>]{0,8192})>(?<title>.*?)</(?:a|h3)\\s*>|(?<open><dl\\b[^>]*>)|(?<close></dl\\s*>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Attributes = new("(?:^|\\s)(?<name>href|add_date)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    public static (List<Bookmark> Entries, int Skipped) Parse(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > MaxFileBytes) throw new InvalidDataException("Fișierul depășește limita de 16 MB.");
        var entries = new List<Bookmark>();
        var folders = new Stack<string>();
        string? pendingFolder = null;
        var skipped = 0;
        foreach (Match token in Tokens.Matches(html))
        {
            if (token.Groups["open"].Success)
            {
                if (folders.Count > 32) throw new InvalidDataException("Ierarhia de foldere este prea adâncă.");
                folders.Push(pendingFolder ?? ""); pendingFolder = null; continue;
            }
            if (token.Groups["close"].Success) { if (folders.Count > 0) folders.Pop(); continue; }
            var title = WebUtility.HtmlDecode(Tags.Replace(token.Groups["title"].Value, "")).Trim();
            if (token.Groups["tag"].Value.Equals("h3", StringComparison.OrdinalIgnoreCase)) { pendingFolder = title; continue; }
            if (entries.Count >= MaxEntries) throw new InvalidDataException("Fișierul conține prea multe favorite (maximum 20.000).");
            string? url = null;
            var addedAt = DateTimeOffset.UtcNow;
            foreach (Match attribute in Attributes.Matches(token.Groups["attrs"].Value))
            {
                var value = WebUtility.HtmlDecode(attribute.Groups["value"].Value);
                if (attribute.Groups["name"].Value.Equals("href", StringComparison.OrdinalIgnoreCase)) url = value.Trim();
                else if (long.TryParse(value, out var timestamp))
                {
                    try { addedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
            if (url is null || url.Length > 8192 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) { skipped++; continue; }
            var folder = string.Join('/', folders.Reverse().Where(f => !string.IsNullOrWhiteSpace(f)));
            if (folder.Length > 240) { skipped++; continue; }
            entries.Add(new(uri.AbsoluteUri, string.IsNullOrWhiteSpace(title) ? uri.Host : title.Length > 500 ? title[..500] : title, addedAt) { Folder = folder });
        }
        return (entries, skipped);
    }
    private sealed class FolderNode
    {
        public readonly List<Bookmark> Bookmarks = new();
        public readonly Dictionary<string, FolderNode> Folders = new();
    }
    public static string Export(IEnumerable<Bookmark> bookmarks)
    {
        var root = new FolderNode();
        foreach (var bookmark in bookmarks.Where(b => !b.IsDeleted))
        {
            var folder = root;
            foreach (var part in bookmark.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!folder.Folders.TryGetValue(part, out var child)) folder.Folders[part] = child = new();
                folder = child;
            }
            folder.Bookmarks.Add(bookmark);
        }
        var html = new StringBuilder("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n<TITLE>Vela — Favorite</TITLE>\n<H1>Favorite</H1>\n");
        void Write(FolderNode node, int depth)
        {
            var indent = new string(' ', depth * 2);
            html.Append(indent).Append("<DL><p>\n");
            foreach (var bookmark in node.Bookmarks)
                html.Append(indent).Append("  <DT><A HREF=\"").Append(WebUtility.HtmlEncode(bookmark.Url))
                    .Append("\" ADD_DATE=\"").Append(bookmark.AddedAt.ToUnixTimeSeconds()).Append("\">")
                    .Append(WebUtility.HtmlEncode(bookmark.Title)).Append("</A>\n");
            foreach (var (name, child) in node.Folders)
            {
                html.Append(indent).Append("  <DT><H3>").Append(WebUtility.HtmlEncode(name)).Append("</H3>\n");
                Write(child, depth + 1);
            }
            html.Append(indent).Append("</DL><p>\n");
        }
        Write(root, 0);
        return html.ToString();
    }
}
