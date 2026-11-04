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

    // High performance list of top tracker and advertising networks
    private static readonly string[] BlockedDomains = new[]
    {
        "doubleclick.net",
        "google-analytics.com",
        "googlesyndication.com",
        "adservice.google.com",
        "googleadservices.com",
        "adnxs.com",
        "criteo.com",
        "criteo.net",
        "outbrain.com",
        "taboola.com",
        "scorecardresearch.com",
        "moatads.com",
        "casalemedia.com",
        "rubiconproject.com",
        "pubmatic.com",
        "openx.net",
        "quantserve.com",
        "amazon-adsystem.com",
        "advertising.com",
        "zedo.com",
        "popads.net",
        "propellerads.com",
        "trafficjunky.com",
        "exoclick.com",
        "adroll.com",
        "adsrvr.org",
        "bidswitch.net",
        "serving-sys.com",
        "smartadserver.com",
        "indexww.com",
        "yieldmo.com",
        "adform.net",
        "hotjar.com",
        "clarity.ms",
        "analytics.twitter.com",
        "connect.facebook.net/signals",
        "bat.bing.com",
        "adnxs-simple.com",
        "yandex.ru/metrika",
        "adcolony.com"
    };

    public bool ShouldBlockUrl(string url)
    {
        if (!IsShieldsEnabled || !BlockAdsAndTrackers)
            return false;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        string lowerUrl = url.ToLowerInvariant();

        for (int i = 0; i < BlockedDomains.Length; i++)
        {
            if (lowerUrl.Contains(BlockedDomains[i]))
            {
                return true;
            }
        }

        // Additional heuristic patterns for tracking and ads
        if (lowerUrl.Contains("/ads/") || 
            lowerUrl.Contains("/adserver/") || 
            lowerUrl.Contains("pagead2.") ||
            lowerUrl.Contains("/telemetry?") ||
            lowerUrl.Contains("/tracker?"))
        {
            return true;
        }

        return false;
    }

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
