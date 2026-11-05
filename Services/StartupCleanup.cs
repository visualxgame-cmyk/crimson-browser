using System.IO;

namespace CrimsonBrowser.Services;

/// <summary>Removes only stale, per-run Incognito profiles left behind after a forced shutdown.</summary>
internal static class StartupCleanup
{
    private const string IncognitoPrefix = "CrimsonIncognito_";

    public static void RemoveStaleIncognitoProfiles()
    {
        try
        {
            string temporaryRoot = Path.GetTempPath();
            DateTime cutoff = DateTime.UtcNow.AddDays(-1);

            foreach (string folder in Directory.EnumerateDirectories(temporaryRoot, IncognitoPrefix + "*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(folder) < cutoff)
                        Directory.Delete(folder, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
