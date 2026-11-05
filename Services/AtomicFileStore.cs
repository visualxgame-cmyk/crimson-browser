using System.IO;
using System.Text;
using System.Text.Json;

namespace CrimsonBrowser.Services;

/// <summary>
/// Persists small profile documents without ever exposing a half-written JSON file.
/// A previous valid copy is retained as a recovery fallback.
/// </summary>
internal static class AtomicFileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? Read<T>(string path)
    {
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(candidate, Encoding.UTF8));
            }
            catch (JsonException)
            {
                // Try the last known-good copy below.
            }
            catch (IOException)
            {
                // The profile can be momentarily busy during shutdown; retain existing data.
            }
        }

        return default;
    }

    public static void Write<T>(string path, T value)
    {
        string? folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("A profile file must have a parent folder.", nameof(path));

        Directory.CreateDirectory(folder);
        string temporary = path + ".tmp";
        string backup = path + ".bak";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options), Encoding.UTF8);

        if (File.Exists(path))
            File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
        else
            File.Move(temporary, path);
    }
}
