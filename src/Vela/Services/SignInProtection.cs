namespace Vela.Services;

internal static class SignInProtection
{
    private static readonly string[] Hosts = ["accounts.google.com", "accounts.youtube.com", "consent.google.com", "consent.youtube.com"];
    private static readonly string HostJson = System.Text.Json.JsonSerializer.Serialize(Hosts);
    internal static string WrapScript(string script) => "(function(velaSignInHosts){" + script + "})(" + HostJson + ");";
    // Phone confirmations can outlive the normal discard deadline without containing
    // an editable field. Keep only these exact HTTPS account/consent hosts alive.
    private static bool IsTrusted(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        Hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
    internal static bool IsActive(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && IsTrusted(uri);
    // Google also embeds persistent cookie/session-check frames in ordinary pages.
    // Those must not pin every signed-in tab; only interactive sign-in routes do.
    internal static bool IsInteractiveFrame(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && IsTrusted(uri) &&
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "signin" or "ServiceLogin" or "AccountChooser");
}
