using System.IO;
using CefSharp;
using CefSharp.SchemeHandler;

namespace CrimsonBrowser.Services;

internal static class CefRuntime
{
    public static void Initialize()
    {
        CefProfileData.ApplyPendingReset();
        var root = CefProfileData.ProfileFolder;
        Directory.CreateDirectory(root);
        var settings = new CefSharp.Wpf.CefSettings
        {
            BrowserSubprocessPath = Path.Combine(AppContext.BaseDirectory, "CrimsonBrowser.exe"),
            RootCachePath = root,
            CachePath = Path.Combine(root, "Default"),
            PersistSessionCookies = true,
            LogFile = Path.Combine(root, "cef.log"),
            LogSeverity = LogSeverity.Warning,
        };
        Cef.EnableWaitForBrowsersToClose();
        // CEF requires this check to be disabled for its supported SelfHost entry point.
        if (!Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null))
            throw new InvalidOperationException("CEF could not initialize. See " + Path.Combine(root, "cef.log"));
        using var context = Cef.GetGlobalRequestContext();
        RegisterResources(context);
    }

    public static void RegisterResources(IRequestContext context)
    {
        context.RegisterSchemeHandlerFactory("https", "start.crimson",
            new FolderSchemeHandlerFactory(Path.Combine(AppContext.BaseDirectory, "Resources"), schemeName: "https", hostName: "start.crimson", defaultPage: "startpage.html"));
        context.RegisterSchemeHandlerFactory("https", "profile.crimson",
            new FolderSchemeHandlerFactory(SettingsService.Instance.WallpaperFolder, schemeName: "https", hostName: "profile.crimson"));
    }
}
