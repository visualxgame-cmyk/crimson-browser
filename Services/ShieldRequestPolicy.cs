namespace CrimsonBrowser.Services;

internal static class ShieldRequestPolicy
{
    // High performance list of top tracker and advertising networks
    private static readonly string[] BlockedDomains = new[]
    {
        "doubleclick.net",
        "google-analytics.com",
        "googlesyndication.com",
        "adservice.google.com",
        "googleadservices.com",
        "adnxs.com",
        "criteo.com",
        "criteo.net",
        "outbrain.com",
        "taboola.com",
        "scorecardresearch.com",
        "moatads.com",
        "casalemedia.com",
        "rubiconproject.com",
        "pubmatic.com",
        "openx.net",
        "quantserve.com",
        "amazon-adsystem.com",
        "advertising.com",
        "zedo.com",
        "popads.net",
        "propellerads.com",
        "trafficjunky.com",
        "exoclick.com",
        "adroll.com",
        "adsrvr.org",
        "bidswitch.net",
        "serving-sys.com",
        "smartadserver.com",
        "indexww.com",
        "yieldmo.com",
        "adform.net",
        "hotjar.com",
        "clarity.ms",
        "analytics.twitter.com",
        "connect.facebook.net/signals",
        "bat.bing.com",
        "adnxs-simple.com",
        "yandex.ru/metrika",
        "adcolony.com"
    };

    // URLs that are ad/tracking endpoints but live on domains we cannot block wholesale.
    // YouTube serves ads from youtube.com itself, so these must be filtered individually.
    private static readonly string[] BlockedUrlFragments = new[]
    {
        "youtube.com/api/stats/ads",
        "youtube.com/api/stats/atr",
        "youtube.com/get_midroll_info",
        "youtube.com/pagead/",
        "youtube.com/ptracking",
        "youtube.com/youtubei/v1/ads",
        "youtube.com/youtubei/v1/log_event",
        "google.com/pagead/",
        "googletagservices.com/tag/js/gpt.js",
        "/gampad/ads",
        "/pubads/",
        "/adsbygoogle",
        "adsbygoogle.js",
        "/pagead/",
        "/adserver/",
        "/advertisement.",
        "/advertisement/",
        "ad_type=",
        // "adformat=" is intentionally not blocked: cancelling the ad's media stream leaves
        // YouTube stuck in ad mode (black player) until its timeout, which is much slower
        // than letting the hidden ad load and seeking straight to its end.
        "adslot=",
        "ad_slot=",
        "/telemetry?",
        "/tracker?"
    };


    public static bool IsAuthenticationPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
        return uri.Host is "accounts.google.com" or "accounts.youtube.com" ||
            (MatchesDomain(uri.Host, "youtube.com") && uri.AbsolutePath.StartsWith("/signin", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldBlockUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        foreach (string rule in BlockedDomains)
            if (MatchesHostAndPath(uri, rule)) return true;
        foreach (string rule in BlockedUrlFragments)
        {
            if (rule.EndsWith('='))
            {
                if (HasQueryKey(uri.Query.AsSpan(), rule.AsSpan(0, rule.Length - 1))) return true;
            }
            else if (rule[0] != '/' && rule.Contains('/'))
            {
                if (MatchesHostAndPath(uri, rule)) return true;
            }
            else
            {
                bool requiresQuery = rule.EndsWith('?');
                var pathRule = requiresQuery ? rule.AsSpan(0, rule.Length - 1) : rule.AsSpan();
                if (uri.AbsolutePath.AsSpan().Contains(pathRule, StringComparison.OrdinalIgnoreCase) &&
                    (!requiresQuery || uri.Query.Length > 0)) return true;
            }
        }
        return false;
    }

    private static bool MatchesHostAndPath(Uri uri, string rule)
    {
        int slash = rule.IndexOf('/');
        var domain = slash < 0 ? rule.AsSpan() : rule.AsSpan(0, slash);
        return MatchesDomain(uri.IdnHost, domain) &&
            (slash < 0 || uri.AbsolutePath.AsSpan().StartsWith(rule.AsSpan(slash), StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesDomain(string host, ReadOnlySpan<char> domain) =>
        host.AsSpan().Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        (host.Length > domain.Length && host[host.Length - domain.Length - 1] == '.' &&
         host.AsSpan().EndsWith(domain, StringComparison.OrdinalIgnoreCase));

    private static bool HasQueryKey(ReadOnlySpan<char> query, ReadOnlySpan<char> key)
    {
        if (!query.IsEmpty && query[0] == '?') query = query[1..];
        while (!query.IsEmpty)
        {
            int end = query.IndexOf('&');
            var parameter = end < 0 ? query : query[..end];
            int separator = parameter.IndexOf('=');
            var name = separator < 0 ? parameter : parameter[..separator];
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            if (end < 0) break;
            query = query[(end + 1)..];
        }
        return false;
    }
}
