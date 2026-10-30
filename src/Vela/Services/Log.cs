using System.IO;

namespace Vela.Services;

public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(AppPaths.Logs, "vela.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) info.Delete();

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
