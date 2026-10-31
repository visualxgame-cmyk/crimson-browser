using System.IO;

namespace Vela.Services;

/// <summary>Where Vela keeps its profile and data (all under %LOCALAPPDATA%\Vela).</summary>
public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vela");

    /// <summary>WebView2 user data folder (cookies, cache, localStorage...).</summary>
    public static string Profile { get; } = Path.Combine(Root, "Profile");

    public static string Data { get; } = Path.Combine(Root, "Data");

    /// <summary>Extra blocklists dropped here by the user (hosts or simple ABP syntax).</summary>
    public static string Filters { get; } = Path.Combine(Root, "Filters");

    /// <summary>Images picked in settings for the new tab background.</summary>
    public static string Backgrounds { get; } = Path.Combine(Root, "Backgrounds");

    public static string Logs { get; } = Path.Combine(Root, "Logs");

    public static string Assets { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets");

    public static void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Profile, Data, Filters, Backgrounds, Logs })
            Directory.CreateDirectory(dir);
    }
}
