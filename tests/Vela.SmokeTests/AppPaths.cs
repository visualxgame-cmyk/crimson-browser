using System.IO;

namespace Vela.Services;

// Only compiled by the smoke harness. Never opens or modifies the real Vela profile.
public static class AppPaths
{
    public static string BaseRoot { get; } = Environment.GetEnvironmentVariable("VELA_SMOKE_DATA") ??
        Path.Combine(Path.GetTempPath(), "Vela.SmokeTests", Guid.NewGuid().ToString("N"));
    public static string Root => ProfileContext.RootFor(BaseRoot);
    public static string Profile => Path.Combine(Root, "Profile");
    public static string Data => Path.Combine(Root, "Data");
    public static string Filters => Path.Combine(Root, "Filters");
    public static string Backgrounds => Path.Combine(Root, "Backgrounds");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string Assets { get; } = Path.Combine(AppContext.BaseDirectory, "Assets");
    public static void EnsureCreated()
    {
        foreach (var path in new[] { Root, Profile, Data, Filters, Backgrounds, Logs }) Directory.CreateDirectory(path);
    }
}
