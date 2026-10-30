using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vela.Services;

public sealed class Settings
{
    public string HomePage { get; set; } = AppConstants.NewTabUrl;
    public string SearchUrl { get; set; } = AppConstants.DefaultSearchUrl;

    /// <summary>Block ads and trackers at the network layer (no extension needed).</summary>
    public bool BlockAds { get; set; } = true;

    /// <summary>Freeze a background tab (JS stops, memory reclaimed) after this many minutes. 0 = off.</summary>
    public int SuspendAfterMinutes { get; set; } = 2;

    /// <summary>Throw away a sleeping tab's process entirely after this many minutes. 0 = off.</summary>
    public int DiscardAfterMinutes { get; set; } = 30;

    /// <summary>Hard cap of simultaneously "live" (loaded) tabs.</summary>
    public int MaxLiveTabs { get; set; } = 10;

    public bool RestoreSession { get; set; } = true;

    public bool ShowDevTools { get; set; }

    [JsonIgnore]
    public bool SuspendEnabled => SuspendAfterMinutes > 0;
}

public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(AppPaths.Data, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt settings: fall back to defaults rather than refusing to start.
        }

        return new Settings();
    }

    public static void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Never let persistence break browsing.
        }
    }
}
