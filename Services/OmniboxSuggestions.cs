namespace CrimsonBrowser.Services;

public sealed record OmniboxSuggestion(string Title, string Url, string Address, string? Completion)
{
    public bool HasDistinctTitle => !string.Equals(Title, Address, StringComparison.OrdinalIgnoreCase);
}

public static class OmniboxSuggestions
{
    public static IReadOnlyList<OmniboxSuggestion> Search(IEnumerable<HistoryEntry> history, string text, int limit = 6)
    {
        string query = text.Trim();
        if (query.Length == 0 || limit <= 0) return Array.Empty<OmniboxSuggestion>();
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sites = new List<OmniboxSuggestion>(limit);
        var pages = new List<OmniboxSuggestion>(limit);
        foreach (var entry in history)
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0) continue;
            string root = uri.GetLeftPart(UriPartial.Authority);
            string address = Display(root);
            string completion = query.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? root : address;
            bool prefix = completion.StartsWith(query, StringComparison.OrdinalIgnoreCase);
            if (prefix && domains.Add(address))
            {
                urls.Add(root + "/");
                sites.Add(new OmniboxSuggestion(address, root + "/", address, completion));
            }
            if (pages.Count < limit && (entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                if (urls.Add(uri.AbsoluteUri))
                    pages.Add(new OmniboxSuggestion(entry.Title, uri.AbsoluteUri, Display(uri.AbsoluteUri), null));
            }
            if (sites.Count == limit) break;
        }
        // Allocate only the visible candidates; typing never starts network requests or polling.
        return sites.Concat(pages).Take(limit).ToArray();
    }

    private static string Display(string url)
    {
        string address = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url[8..] : url[7..];
        if (address.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) address = address[4..];
        return address.TrimEnd('/');
    }
}
