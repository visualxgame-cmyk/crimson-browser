using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Vela.Services;

internal static class SiteDataService
{
    public static string? Origin(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host is AppConstants.InternalHost or AppConstants.BackgroundHost or ShortcutStore.IconHost || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static Task ClearOriginAsync(CoreWebView2 core, string origin)
    {
        var validOrigin = Origin(origin) ?? throw new ArgumentException("Selectează un site HTTP sau HTTPS valid.");
        // CDP scopes this operation to one origin. Never use the profile-wide ClearBrowsingData API here.
        return core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", JsonSerializer.Serialize(new { origin = validOrigin, storageTypes = "all" }));
    }
}
