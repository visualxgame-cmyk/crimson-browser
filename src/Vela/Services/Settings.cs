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
    public int SuspendAfterMinutes { get; set; } = 1;

    /// <summary>Throw away a sleeping tab's process entirely after this many minutes. 0 = off.</summary>
    public int DiscardAfterMinutes { get; set; } = 15;

    /// <summary>Target cap of loaded tabs; pages protected against eviction can exceed it.</summary>
    public int MaxLiveTabs { get; set; } = 6;
    public bool EconomyMode { get; set; }
    [JsonIgnore] public int EffectiveMaxLiveTabs => EconomyMode ? 3 : MaxLiveTabs;
    [JsonIgnore] public int EffectiveSuspendMinutes => EconomyMode ? 1 : SuspendAfterMinutes;
    [JsonIgnore] public int EffectiveDiscardMinutes => EconomyMode ? 5 : DiscardAfterMinutes;

    /// <summary>
    /// Settings format version. Older files are migrated once by <see cref="SettingsStore"/>
    /// so that improved memory defaults reach existing installs too.
    /// </summary>
    public int Version { get; set; }

    public bool RestoreSession { get; set; } = true;

    public bool ShowDevTools { get; set; }

    /// <summary>Id of the colour theme (see <see cref="ThemeCatalog"/>).</summary>
    public string Theme { get; set; } = "vela";

    /// <summary>Optional accent that overrides the theme (#RRGGBB). Null = the theme's own accent.</summary>
    public string? CustomAccent { get; set; }

    /// <summary>New tab background: "theme", "color" or "image".</summary>
    public string NewTabBackgroundMode { get; set; } = "theme";

    /// <summary>Custom new tab background colour (#RRGGBB), also used behind a background image.</summary>
    public string NewTabBackgroundColor { get; set; } = "#101114";

    /// <summary>File name (inside %LOCALAPPDATA%\Vela\Backgrounds) used as the new tab background.</summary>
    public string? NewTabBackgroundImage { get; set; }

    /// <summary>Public GitHub owner/repository checked only when the user requests an update check.</summary>
    public string UpdateRepository { get; set; } = AppConstants.DefaultUpdateRepository;
    public string StartupMode { get; set; } = "";
    public string DownloadDirectory { get; set; } = "";
    public bool AskDownloadLocation { get; set; }
    public bool SavePasswords { get; set; } = true;
    public bool AutofillEnabled { get; set; } = true;
    public bool ExtensionsEnabled { get; set; }
    public string SyncDirectory { get; set; } = "";
    public string TranslationLanguage { get; set; } = "ro";
    public DateTimeOffset PreferencesUpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string EffectiveStartupMode => StartupMode is "restore" or "home" or "newtab" ? StartupMode : RestoreSession ? "restore" : "newtab";

    [JsonIgnore]
    public bool SuspendEnabled => EffectiveSuspendMinutes > 0;

    /// <summary>Snapshot used by the settings window so cancelling can restore the old values.</summary>
    public Settings Clone() => (Settings)MemberwiseClone();
}

public static class SettingsStore
{
    static SettingsStore() { }
    /// <summary>Increment whenever defaults change in a way existing installs should inherit.</summary>
    private const int CurrentVersion = 3;

    private static readonly string FilePath = Path.Combine(AppPaths.Data, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static Settings Load()
    {
        Settings settings = new();

        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                if (loaded is not null) { settings = loaded; break; }
            }
            catch (Exception ex) { Log.Write("Read settings: " + ex.Message); }
        }

        if (Migrate(settings)) Save(settings);
        return settings;
    }

    /// <summary>
    /// Applies better defaults to files written by older builds. Values the user changed
    /// away from the old defaults are kept untouched.
    /// </summary>
    private static bool Migrate(Settings settings)
    {
        if (settings.Version >= CurrentVersion) return false;

        if (settings.Version < 2)
        {
            if (settings.SuspendAfterMinutes == 2) settings.SuspendAfterMinutes = 1;
            if (settings.DiscardAfterMinutes == 30) settings.DiscardAfterMinutes = 15;
            if (settings.MaxLiveTabs == 10) settings.MaxLiveTabs = 6;
        }
        if (string.IsNullOrWhiteSpace(settings.UpdateRepository)) settings.UpdateRepository = AppConstants.DefaultUpdateRepository;

        settings.Version = CurrentVersion;
        return true;
    }

    public static bool Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            AtomicFile.Write(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Save settings: " + ex.Message);
            return false;
        }
    }
}
