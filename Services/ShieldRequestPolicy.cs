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
        "adcolony.com",
        // Popunder/interstitial networks commonly used by video hosts.
        "juicyads.com",
        "trafficstars.com",
        "adsterra.com",
        "hilltopads.net",
        "popcash.net",
        "popmyads.com",
        "clickadu.com",
        "adcash.com",
        "mgid.com",
        "revcontent.com",
        "bidvertiser.com",
        "adskeeper.com",
        "media.net",
        "teads.tv",
        "spotxchange.com",
        "tremorhub.com",
        "sekindo.com",
        "adpushup.com",
        "admaven.com",
        "monetag.com",
        "profitablecpmgate.com",
        "highperformanceformat.com",
        "onclickalgo.com",
        "onclickmega.com",
        "onclickmax.com",
        "pemsrv.com",
        "tsyndicate.com",
        "exdynsrv.com",
        "realsrv.com",
        "realsrvcdn.com",
        "adx18.com",
        // Anti-adblock redirect and popunder networks seen on video hosts.
        "advtpe.com",
        "stareobjectedcipher.com",
        "fhvfd.com",
        "2osb.com",
        // Video ad servers and VAST/VPAID ad exchanges.
        "2mdn.net",
        "imasdk.googleapis.com",
        "innovid.com",
        "springserve.com",
        "tremorvideo.com",
        "freewheel.tv",
        "fwmrm.net",
        "yieldlab.net",
        "smartclip.net",
        "stickyadstv.com",
        "gumgum.com",
        "lijit.com",
        "sovrn.com",
        "33across.com",
        "sharethrough.com",
        "triplelift.com",
        "indexexchange.com",
        "improvedigital.com",
        "conversantmedia.com",
        "zemanta.com",
        "nativo.com",
        "adition.com",
        "adhese.com",
        "undertone.com",
        "districtm.io",
        "adhigh.net",
        "adnium.com",
        "exponential.com",
        "tribalfusion.com",
        "vrtcal.com",
        "smaato.net",
        "mopub.com",
        "inmobi.com",
        "applovin.com",
        "unityads.unity3d.com",
        "vungle.com",
        "chartboost.com",
        "ironsrc.com",
        "fyber.com",
        "supersonicads.com",
        "doubleverify.com"
    };

    // URLs that are ad/tracking endpoints but live on domains we cannot block wholesale.
    // YouTube serves ads from youtube.com itself, so these must be filtered individually.
    private static readonly string[] BlockedUrlFragments = new[]
    {
        "youtube.com/get_midroll_info",
        "youtube.com/pagead/",
        "youtube.com/youtubei/v1/ads",
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
        // "adformat=" is intentionally not matched: media URLs carry both video
        // content and ads. Cancelling that transport can strand the player in ad mode.
        "adslot=",
        "ad_slot=",
        "ad_tag=",
        "adtag=",
        "vast=",
        "vpaid",
        "ima3.js",
        "ads-loader",
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
        // YouTube reuses its media transport for both content and ads. Dropping a
        // signed media segment breaks playback; the player script filters ad metadata.
        if (MatchesDomain(uri.IdnHost, "googlevideo.com") && uri.AbsolutePath == "/videoplayback") return false;
        if (MatchesDomain(uri.IdnHost, "youtube.com") && uri.AbsolutePath is
            "/api/stats/ads" or "/api/stats/atr" or "/ptracking" or "/youtubei/v1/log_event") return false;
        if (LooksLikePopupRedirect(uri)) return true;
        if (LooksLikeAdTagPath(uri.AbsolutePath)) return true;
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

    // VAST/VPAID ad tags commonly use /vast, /vast.xml or /vast/... paths. These
    // are specific enough that ordinary article paths are not mistaken for ads.
    private static bool LooksLikeAdTagPath(ReadOnlySpan<char> path) =>
        path.EndsWith("/vast", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/vast.xml", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/vast/", StringComparison.OrdinalIgnoreCase);

    // Popunder networks rotate throwaway hostnames but keep recognisable request
    // shapes. Matching the shape keeps them out even before a hostname is listed.
    private static bool LooksLikePopupRedirect(Uri uri)
    {
        ReadOnlySpan<char> path = uri.AbsolutePath;
        if (path.Contains("/api/submit_form_request", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains("indexindouble.php", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("indexdouble.php", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith("/smart.php", StringComparison.OrdinalIgnoreCase) &&
            HasQueryKey(uri.Query.AsSpan(), "link")) return true;
        // Redirector paths look like /cx/<long random token>.
        int cx = path.IndexOf("/cx/", StringComparison.OrdinalIgnoreCase);
        if (cx >= 0 && path.Length - cx >= 48) return true;
        // Popunder telemetry carries a base64 JSON blob beginning with {"uah".
        if (uri.Query.AsSpan().Contains("md=eyJ1YWgi", StringComparison.OrdinalIgnoreCase)) return true;
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
