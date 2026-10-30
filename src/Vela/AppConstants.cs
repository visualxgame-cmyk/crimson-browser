namespace Vela;

public static class AppConstants
{
    public const string ProductName = "Vela";
    public const string Version = "0.1.0";

    /// <summary>Virtual host used to serve the local new-tab page and other internal pages.</summary>
    public const string InternalHost = "vela.internal";
    public const string NewTabUrl = "https://vela.internal/newtab.html";
    public const string ErrorPageUrl = "https://vela.internal/error.html";

    public const string DefaultSearchUrl = "https://duckduckgo.com/?q=";
}
