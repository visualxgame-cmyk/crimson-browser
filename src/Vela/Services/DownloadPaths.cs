using System.IO;
using System.Runtime.InteropServices;

namespace Vela.Services;

internal static class DownloadPaths
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, IntPtr token, out IntPtr path);
    public static string DefaultDirectory
    {
        get
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var path) == 0)
            {
                try { return Marshal.PtrToStringUni(path)!; }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
    public static string Resolve(string? configured) => string.IsNullOrWhiteSpace(configured) ? DefaultDirectory : Path.GetFullPath(configured);
}
