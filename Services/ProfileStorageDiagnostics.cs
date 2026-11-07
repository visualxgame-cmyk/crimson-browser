using System.IO;

namespace CrimsonBrowser.Services;

internal static class ProfileStorageDiagnostics
{
    public static event Action<string, Exception>? Failed;
    public static void Report(string file, Exception error) => Failed?.Invoke(Path.GetFileName(file), error);
}
