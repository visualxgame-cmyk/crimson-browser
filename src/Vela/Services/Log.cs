using System.IO;
using System.Text.RegularExpressions;

namespace Vela.Services;

public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();
    private static readonly Regex Addresses = new(@"(?:https?|file)://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                message = Addresses.Replace(message, "[address]");
                var userDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userDirectory)) message = message.Replace(userDirectory, "[user]", StringComparison.OrdinalIgnoreCase);
                if (message.Length > 32000) message = message[..32000];
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
