namespace Vela;

public static class AppConstants
{
    public const string ProductName = "Vela";
    public const string Version = "0.1.5";
    public const string DefaultUpdateRepository = "visualxstudio/vela-browser";

    /// <summary>Virtual host used to serve the local new-tab page and other internal pages.</summary>
    public const string InternalHost = "vela.internal";
    public const string NewTabUrl = "https://vela.internal/newtab.html";
    public const string ReaderUrl = "https://vela.internal/reader.html";
    public const string ErrorPageUrl = "https://vela.internal/error.html";

    /// <summary>Virtual host that serves user images (new tab background) from %LOCALAPPDATA%\Vela\Backgrounds.</summary>
    public const string BackgroundHost = "vela.bg";

    public const string DefaultSearchUrl = "https://duckduckgo.com/?q=";
}
