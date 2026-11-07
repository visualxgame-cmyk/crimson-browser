using System.IO;
using System.Runtime.CompilerServices;
using CefSharp;
using CefSharp.SchemeHandler;

namespace CrimsonBrowser.Services;

internal static class CefRuntime
{
    public static bool IsInitialized { get; private set; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Initialize(string? profileFolder = null, string? subprocessPath = null)
    {
        // Tests supply an isolated profile and never consume a pending user-data reset.
        if (profileFolder == null) CefProfileData.ApplyPendingReset();
        var root = profileFolder == null ? CefProfileData.ProfileFolder : Path.GetFullPath(profileFolder);
        Directory.CreateDirectory(root);
        var settings = new CefSharp.Wpf.CefSettings
        {
            BrowserSubprocessPath = subprocessPath ?? Path.Combine(AppContext.BaseDirectory, "CrimsonBrowser.exe"),
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
        IsInitialized = true;
        using var context = Cef.GetGlobalRequestContext();
        RegisterResources(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Shutdown()
    {
        if (!IsInitialized) return;
        if (Cef.IsInitialized == true && !Cef.IsShutdown)
        {
            Cef.WaitForBrowsersToClose();
            Cef.Shutdown();
        }
        IsInitialized = false;
    }

    public static void RegisterResources(IRequestContext context)
    {
        context.RegisterSchemeHandlerFactory("https", "start.crimson",
            new FolderSchemeHandlerFactory(Path.Combine(AppContext.BaseDirectory, "Resources"), schemeName: "https", hostName: "start.crimson", defaultPage: "startpage.html"));
        context.RegisterSchemeHandlerFactory("https", "profile.crimson",
            new FolderSchemeHandlerFactory(SettingsService.Instance.WallpaperFolder, schemeName: "https", hostName: "profile.crimson"));
    }
}
