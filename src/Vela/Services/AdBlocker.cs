using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Vela.Services;

/// <summary>
/// Network-level ad/tracker blocker. Works without extensions: every request made by a
/// tab is checked against the bundled list plus any user lists in %LOCALAPPDATA%\Vela\Filters.
/// First-party requests are never blocked, so sites keep working.
/// </summary>
public sealed class AdBlocker
{
    private static readonly byte[] EmptyBody = [];

    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pathFragments = new();

    public bool Enabled { get; set; } = true;

    public int RuleCount => _hosts.Count + _pathFragments.Count;

    public long BlockedTotal { get; private set; }

    /// <summary>Cosmetic + YouTube scriptlet injected into every document while the shield is on.</summary>
    public string InjectionScript { get; private set; } = string.Empty;

    public void Load()
    {
        _hosts.Clear();
        _allowHosts.Clear();
        _pathFragments.Clear();

        TryLoadFile(Path.Combine(AppPaths.Assets, "blocklist.txt"));
        TryLoadFile(Path.Combine(AppPaths.Assets, "blocklist-big.txt"));

        try
        {
            var scriptPath = Path.Combine(AppPaths.Assets, "adblock.js");
            if (File.Exists(scriptPath)) InjectionScript = File.ReadAllText(scriptPath);
        }
        catch
        {
            // Cosmetic filtering is optional; network blocking still works.
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.Filters, "*.txt"))
            {
                if (string.Equals(Path.GetFileName(file), "allowlist.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                TryLoadFile(file);
            }
        }
        catch
        {
            // Filters folder is optional.
        }

        var allowList = Path.Combine(AppPaths.Filters, "allowlist.txt");
        if (File.Exists(allowList))
        {
            foreach (var line in SafeReadLines(allowList))
            {
                var host = NormalizeHost(line);
                if (host is not null) _allowHosts.Add(host);
            }
        }
    }

    private void TryLoadFile(string path)
    {
        if (!File.Exists(path)) return;

        foreach (var raw in SafeReadLines(path))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] is '#' or '!' or '[') continue;

            // ABP allow rule: "@@||example.com^"
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var allowHost = NormalizeHost(line[2..].TrimStart('|').TrimEnd('^').Split('$')[0]);
                if (allowHost is not null) _allowHosts.Add(allowHost);
                continue;
            }

            // Hosts-file format: "0.0.0.0 ads.example.com"
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && (parts[0] == "0.0.0.0" || parts[0] == "127.0.0.1" || parts[0] == "::1" || parts[0] == "::"))
            {
                var hostsHost = NormalizeHost(parts[1]);
                if (hostsHost is not null) _hosts.Add(hostsHost);
                continue;
            }

            // ABP domain anchor: "||ads.example.com^" or "||ads.example.com^$third-party"
            if (line.StartsWith("||", StringComparison.Ordinal))
            {
                var body = line[2..];
                var caret = body.IndexOf('^');
                if (caret >= 0) body = body[..caret];
                var dollar = body.IndexOf('$');
                if (dollar >= 0) body = body[..dollar];
                var anchorHost = NormalizeHost(body);
                if (anchorHost is not null) _hosts.Add(anchorHost);
                continue;
            }

            // ABP path/regex rule: "/banner/*/ad" — keep only plain fragments, skip complex regex.
            if (line.StartsWith('/') && line.EndsWith('/') && line.Length > 3)
            {
                _pathFragments.Add(line[1..^1]);
                continue;
            }

            if (line.Contains('*') || line.Contains('|') || line.Contains('^') || line.Contains('$'))
                continue;

            var host = NormalizeHost(line);
            if (host is not null) _hosts.Add(host);
        }
    }

    private static string? NormalizeHost(string value)
    {
        var host = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];

        // Ignore junk and single-label entries: blocking "co" or "com" would break the web.
        if (host.Length < 4 || !host.Contains('.')) return null;
        if (host.Contains('/') || host.Contains(':') || host.Contains(' ')) return null;
        return host;
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch { return []; }
    }

    public bool IsBlocked(Uri uri, string? documentUrl)
    {
        if (!Enabled) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        string? documentHost = null;
        if (!string.IsNullOrEmpty(documentUrl) &&
            Uri.TryCreate(documentUrl, UriKind.Absolute, out var docUri))
        {
            documentHost = docUri.Host;
        }

        // First party: the site itself. Never block it.
        if (documentHost is not null &&
            string.Equals(uri.Host, documentHost, StringComparison.OrdinalIgnoreCase))
            return false;

        if (MatchesSuffix(_allowHosts, uri.Host)) return false;
        if (MatchesSuffix(_hosts, uri.Host)) return true;

        var path = uri.PathAndQuery;
        foreach (var fragment in _pathFragments)
        {
            if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool MatchesSuffix(HashSet<string> set, string host)
    {
        if (set.Count == 0) return false;

        var candidate = host.TrimEnd('.').ToLowerInvariant();
        while (true)
        {
            if (set.Contains(candidate)) return true;
            var dot = candidate.IndexOf('.');
            if (dot < 0) return false;
            candidate = candidate[(dot + 1)..];
        }
    }

    /// <summary>Hooks a tab's WebView into the blocker. Call before navigating.</summary>
    public void Attach(CoreWebView2 core, Func<bool> shieldEnabled, Func<string?> documentUrl, Action onBlocked)
    {
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                if (!Enabled || !shieldEnabled()) return;

                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;

                // Iframes load as Document context; block those too, but never the top page.
                if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
                {
                    var doc = documentUrl();
                    if (string.IsNullOrEmpty(doc)) return;
                    if (Uri.TryCreate(doc, UriKind.Absolute, out var docUri) &&
                        string.Equals(docUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                if (!IsBlocked(uri, documentUrl())) return;

                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(EmptyBody), 403, "Blocked by Vela", "Content-Type: text/plain");

                BlockedTotal++;
                onBlocked();
            }
            catch
            {
                // A throwing handler would break the request entirely; ignore.
            }
        };
    }
}
