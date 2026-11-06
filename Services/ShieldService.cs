using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CrimsonBrowser.Services;

public class ShieldService : INotifyPropertyChanged
{
    private static ShieldService? _instance;
    public static ShieldService Instance => _instance ??= new ShieldService();

    private bool _isShieldsEnabled = true;
    private bool _blockAdsAndTrackers = true;
    private bool _enforceHttps = true;
    private bool _lowRamMode = true;
    private bool _blockPopups = true;
    private int _blockedCount = 0;

    public ShieldService()
    {
        var saved = SettingsService.Instance;
        _isShieldsEnabled   = saved.ShieldsEnabled;
        _blockAdsAndTrackers = saved.BlockAds;
        _enforceHttps       = saved.EnforceHttps;
        _lowRamMode         = saved.LowRamMode;
        _blockPopups        = saved.BlockPopups;
    }

    public bool IsShieldsEnabled
    {
        get => _isShieldsEnabled;
        set
        {
            if (SetField(ref _isShieldsEnabled, value))
                SettingsService.Instance.ShieldsEnabled = value;
        }
    }

    public bool BlockAdsAndTrackers
    {
        get => _blockAdsAndTrackers;
        set
        {
            if (SetField(ref _blockAdsAndTrackers, value))
                SettingsService.Instance.BlockAds = value;
        }
    }

    public bool EnforceHttps
    {
        get => _enforceHttps;
        set
        {
            if (SetField(ref _enforceHttps, value))
                SettingsService.Instance.EnforceHttps = value;
        }
    }

    public bool LowRamMode
    {
        get => _lowRamMode;
        set
        {
            if (SetField(ref _lowRamMode, value))
                SettingsService.Instance.LowRamMode = value;
        }
    }

    public bool BlockPopups
    {
        get => _blockPopups;
        set
        {
            if (SetField(ref _blockPopups, value))
                SettingsService.Instance.BlockPopups = value;
        }
    }

    public int BlockedCount
    {
        get => _blockedCount;
        set => SetField(ref _blockedCount, value);
    }

    public bool ShouldBlockUrl(string url) => IsShieldsEnabled && BlockAdsAndTrackers && ShieldRequestPolicy.ShouldBlockUrl(url);

    public void IncrementBlockedCount()
    {
        BlockedCount++;
    }

    public void ResetCount()
    {
        BlockedCount = 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
