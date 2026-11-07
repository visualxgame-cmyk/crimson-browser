namespace CrimsonBrowser.Services;

internal static class BookmarkAddress
{
    public static bool Equals(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a) || !Uri.TryCreate(right, UriKind.Absolute, out var b))
            return string.Equals(left, right, StringComparison.Ordinal);
        // Hosts and schemes are insensitive; paths, query values and fragments need not be.
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port &&
               string.Equals(a.GetComponents(UriComponents.UserInfo | UriComponents.PathAndQuery | UriComponents.Fragment,
                   UriFormat.UriEscaped), b.GetComponents(UriComponents.UserInfo | UriComponents.PathAndQuery | UriComponents.Fragment,
                   UriFormat.UriEscaped), StringComparison.Ordinal);
    }
}
