namespace CrimsonBrowser.Services;

internal static class PopupNavigationPolicy
{
    public static bool IsAuthenticationTarget(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
        return ShieldRequestPolicy.IsAuthenticationPage(url) ||
            uri.IdnHost is "login.microsoftonline.com" or "login.live.com";
    }

    public static bool ShouldBlock(string targetUrl, bool userGesture, bool popupWindow)
    {
        if (ShieldRequestPolicy.ShouldBlockUrl(targetUrl)) return true;
        if (!userGesture) return true;
        if (IsAuthenticationTarget(targetUrl)) return false;
        return popupWindow || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) ||
            target.Scheme is not ("http" or "https");
    }
}
