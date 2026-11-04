using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
}

public class SettingsService : INotifyPropertyChanged
{
    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    private readonly string _settingsFolder;
    private readonly string _settingsFilePath;
    private readonly string _resourcesWallpaperPath;

    private SettingsData _data = new();

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
        _resourcesWallpaperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "custom_wallpaper.jpg");

        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<SettingsData>(json);
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
                Directory.CreateDirectory(_settingsFolder);
            }

            string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
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
                // Copy image into app Resources folder for the start page virtual host
                string targetDir = Path.GetDirectoryName(_resourcesWallpaperPath)!;
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                File.Copy(sourceFilePath, _resourcesWallpaperPath, true);

                // Also copy to bin/Debug output directory if different
                string binResourcesPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Resources", "custom_wallpaper.jpg");
                if (!string.Equals(binResourcesPath, _resourcesWallpaperPath, StringComparison.OrdinalIgnoreCase))
                {
                    string binDir = Path.GetDirectoryName(binResourcesPath)!;
                    if (!Directory.Exists(binDir)) Directory.CreateDirectory(binDir);
                    File.Copy(sourceFilePath, binResourcesPath, true);
                }

                CustomBackgroundImagePath = sourceFilePath;
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
            if (File.Exists(_resourcesWallpaperPath))
            {
                File.Delete(_resourcesWallpaperPath);
            }
            CustomBackgroundImagePath = null;
            SettingsUpdated?.Invoke();
        }
        catch { }
    }

    public bool HasCustomWallpaper => File.Exists(_resourcesWallpaperPath);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
