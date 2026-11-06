using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace CrimsonBrowser.Services;

public class SettingsData
{
    public string Language { get; set; } = "en";
    public string SearchEngine { get; set; } = "Google"; // Google, DuckDuckGo, Brave, Bing
    public string? CustomBackgroundImagePath { get; set; }
    public double BackgroundDimming { get; set; } = 0.4;

    // Crimson Shields, persisted so the toggles survive a restart.
    public bool ShieldsEnabled { get; set; } = true;
    public bool BlockAds { get; set; } = true;
    public bool LowRamMode { get; set; } = true;
    public bool EnforceHttps { get; set; } = true;
    public bool BlockPopups { get; set; } = true;

    // Update preferences. A skipped version is remembered so "Later" does not nag
    // on every launch; it resets automatically when a newer release appears.
    public bool AutoCheckUpdates { get; set; } = true;
    public string? SkippedUpdateVersion { get; set; }
}

public class SettingsService : INotifyPropertyChanged
{
    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    private readonly string _settingsFolder;
    private readonly string _settingsFilePath;
    private readonly string _wallpaperFolder;
    private readonly string _wallpaperPath;

    private SettingsData _data = new();

    internal string WallpaperFolder => _wallpaperFolder;

    public string Language
    {
        get => _data.Language;
        set
        {
            if (_data.Language != value)
            {
                _data.Language = value;
                LocalizationService.Instance.CurrentLanguage = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    public string SearchEngine
    {
        get => _data.SearchEngine;
        set
        {
            if (_data.SearchEngine != value)
            {
                _data.SearchEngine = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    /// <summary>Builds a full search URL for the currently selected search engine.</summary>
    public string BuildSearchUrl(string query)
    {
        string encoded = Uri.EscapeDataString(query);
        return SearchEngine switch
        {
            "DuckDuckGo" => $"https://duckduckgo.com/?q={encoded}",
            "Brave"      => $"https://search.brave.com/search?q={encoded}",
            "Bing"       => $"https://www.bing.com/search?q={encoded}",
            _            => $"https://www.google.com/search?q={encoded}"
        };
    }

    public string? CustomBackgroundImagePath
    {
        get => _data.CustomBackgroundImagePath;
        set
        {
            if (_data.CustomBackgroundImagePath != value)
            {
                _data.CustomBackgroundImagePath = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    public double BackgroundDimming
    {
        get => _data.BackgroundDimming;
        set
        {
            if (Math.Abs(_data.BackgroundDimming - value) > 0.01)
            {
                _data.BackgroundDimming = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    public bool ShieldsEnabled
    {
        get => _data.ShieldsEnabled;
        set => SetShield(nameof(ShieldsEnabled), _data.ShieldsEnabled, value, v => _data.ShieldsEnabled = v);
    }

    public bool BlockAds
    {
        get => _data.BlockAds;
        set => SetShield(nameof(BlockAds), _data.BlockAds, value, v => _data.BlockAds = v);
    }

    public bool LowRamMode
    {
        get => _data.LowRamMode;
        set => SetShield(nameof(LowRamMode), _data.LowRamMode, value, v => _data.LowRamMode = v);
    }

    public bool EnforceHttps
    {
        get => _data.EnforceHttps;
        set => SetShield(nameof(EnforceHttps), _data.EnforceHttps, value, v => _data.EnforceHttps = v);
    }

    public bool BlockPopups
    {
        get => _data.BlockPopups;
        set => SetShield(nameof(BlockPopups), _data.BlockPopups, value, v => _data.BlockPopups = v);
    }

    public bool AutoCheckUpdates
    {
        get => _data.AutoCheckUpdates;
        set => SetShield(nameof(AutoCheckUpdates), _data.AutoCheckUpdates, value, v => _data.AutoCheckUpdates = v);
    }

    public string? SkippedUpdateVersion
    {
        get => _data.SkippedUpdateVersion;
        set
        {
            if (_data.SkippedUpdateVersion == value) return;
            _data.SkippedUpdateVersion = value;
            OnPropertyChanged();
            Save();
        }
    }


    private void SetShield(string propertyName, bool current, bool value, Action<bool> apply)
    {
        if (current == value) return;
        apply(value);
        OnPropertyChanged(propertyName);
        Save();
    }

    public event Action? SettingsUpdated;

    public SettingsService()
    {
        _settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrimsonBrowser"
        );
        _settingsFilePath = Path.Combine(_settingsFolder, "settings.json");
        // User data must never be written below the installation directory.  Installed
        // builds normally live in Program Files, where a standard user has no write access.
        _wallpaperFolder = Path.Combine(_settingsFolder, "Wallpaper");
        _wallpaperPath = Path.Combine(_wallpaperFolder, "custom_wallpaper.jpg");

        // CEF's folder scheme handler requires this directory before registration.
        // Creating this empty profile folder is harmless and prevents first-run startup failures.
        Directory.CreateDirectory(_wallpaperFolder);

        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var loaded = AtomicFileStore.Read<SettingsData>(_settingsFilePath);
                if (loaded != null)
                {
                    _data = loaded;
                }
            }
        }
        catch { }

        // Sync Localization with saved language
        LocalizationService.Instance.CurrentLanguage = _data.Language;
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(_settingsFolder))
            {
                Directory.CreateDirectory(_wallpaperFolder);
            }

            AtomicFileStore.Write(_settingsFilePath, _data);
            SettingsUpdated?.Invoke();
        }
        catch { }
    }

    public void SetCustomBackgroundImage(string sourceFilePath)
    {
        try
        {
            if (File.Exists(sourceFilePath))
            {
                Directory.CreateDirectory(_wallpaperFolder);
                File.Copy(sourceFilePath, _wallpaperPath, true);

                // The start page loads the file via the profile-local virtual host.
                CustomBackgroundImagePath = _wallpaperPath;
                SettingsUpdated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to set wallpaper: {ex.Message}");
        }
    }

    public void ResetBackgroundImage()
    {
        try
        {
            if (File.Exists(_wallpaperPath))
            {
                File.Delete(_wallpaperPath);
            }
            CustomBackgroundImagePath = null;
            SettingsUpdated?.Invoke();
        }
        catch { }
    }

    public bool HasCustomWallpaper => File.Exists(_wallpaperPath);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
