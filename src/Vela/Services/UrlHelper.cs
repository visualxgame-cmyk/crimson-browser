namespace Vela.Services;

public static class UrlHelper
{
    /// <summary>Turns omnibox input into a URL (adding https://) or a search query.</summary>
    public static string ToNavigationUrl(string input, string searchUrl)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return AppConstants.NewTabUrl;

        if (text.Contains(' ')) return Search(searchUrl, text);

        if (HasScheme(text)) return text;

        // Bare host or host/path: "github.com", "localhost:3000/app", "192.168.1.1"
        if (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            return "https://" + text;

        return Search(searchUrl, text);
    }

    private static string Search(string searchUrl, string query) => searchUrl + Uri.EscapeDataString(query);

    private static bool HasScheme(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    public static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    }
}
