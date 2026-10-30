using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace Vela.Models;

/// <summary>
/// One browser tab. The <see cref="View"/> is created lazily and can be thrown away
/// (discarded) to release memory while the tab stays in the strip; activating a
/// discarded tab recreates the view and reloads <see cref="Url"/>.
/// </summary>
public sealed class BrowserTab : ObservableObject
{
    private string _title = "Tab nou";
    private string _url = "";
    private ImageSource? _favicon;
    private bool _isSelected;
    private bool _isLoading;
    private bool _isSuspended;
    private bool _isAudible;
    private bool _isMuted;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _shieldEnabled = true;
    private int _blockedCount;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Backing WebView2 control; null while the tab has never been shown or was discarded.</summary>
    public WebView2? View { get; set; }

    public bool HasView => View is not null;

    /// <summary>URL to load the next time a view is created for this tab.</summary>
    public string PendingUrl { get; set; } = "";

    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public ImageSource? Favicon { get => _favicon; set => Set(ref _favicon, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public bool IsSuspended { get => _isSuspended; set => Set(ref _isSuspended, value); }
    public bool IsAudible { get => _isAudible; set => Set(ref _isAudible, value); }
    public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }
    public bool CanGoBack { get => _canGoBack; set => Set(ref _canGoBack, value); }
    public bool CanGoForward { get => _canGoForward; set => Set(ref _canGoForward, value); }
    public bool ShieldEnabled { get => _shieldEnabled; set => Set(ref _shieldEnabled, value); }

    /// <summary>Requests blocked by the shield on this tab since it was opened.</summary>
    public int BlockedCount { get => _blockedCount; set => Set(ref _blockedCount, value); }

    /// <summary>True when the tab has no live view and must be rebuilt on activation.</summary>
    public bool IsDiscarded => View is null;

    public bool IsInternalPage => Url.StartsWith(AppConstants.NewTabUrl, StringComparison.OrdinalIgnoreCase)
                                  || string.IsNullOrEmpty(Url);
}
