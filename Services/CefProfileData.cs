using System.IO;

namespace CrimsonBrowser.Services;

internal static class CefProfileData
{
    private static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrimsonBrowser");
    public static string ProfileFolder => Path.Combine(DataFolder, "CefData");
    private static string ResetMarker => Path.Combine(DataFolder, "cef-clear-pending.json");

    public static void RequestReset() => AtomicFileStore.Write(ResetMarker, true);

    // CEF owns open database files while it runs. Reset only before initialization,
    // after the user's explicit Clear Data confirmation, so live databases stay intact.
    public static void ApplyPendingReset()
    {
        if (!File.Exists(ResetMarker)) return;
        string profile = Path.GetFullPath(ProfileFolder);
        string expected = Path.GetFullPath(Path.Combine(DataFolder, "CefData"));
        if (!string.Equals(profile, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unexpected CEF profile directory.");
        if (Directory.Exists(profile))
        {
            if ((File.GetAttributes(profile) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cannot clear a redirected CEF profile directory.");
            Directory.Delete(profile, recursive: true);
        }
        File.Delete(ResetMarker);
        File.Delete(ResetMarker + ".bak");
        File.Delete(Path.Combine(DataFolder, "cef-permissions.json"));
        File.Delete(Path.Combine(DataFolder, "cef-permissions.json.bak"));
    }
}
