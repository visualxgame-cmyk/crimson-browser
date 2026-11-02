using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using Vela.Services;

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
    private bool _hasUnsavedChanges;
    private bool _hasActiveMedia;
    public bool HasActiveMedia { get => _hasActiveMedia; set => Set(ref _hasActiveMedia, value); }
    internal bool HasActiveSignIn { get; set; }
    private bool _isPinned;
    private bool _isSecureConnection;
    private string? _connectionError;
    private string _groupName = "";
    private bool _headerVisible = true;
    private string _groupPrefix = "";
    public string GroupName { get => _groupName; set => Set(ref _groupName, value); }
    public bool HeaderVisible { get => _headerVisible; set => Set(ref _headerVisible, value); }
    public string GroupPrefix { get => _groupPrefix; set => Set(ref _groupPrefix, value); }
    internal ReaderArticle? Reader { get; set; }
    internal Guid? OpenerTabId { get; set; }
    public bool IsSecureConnection { get => _isSecureConnection; set => Set(ref _isSecureConnection, value); }
    public string? ConnectionError { get => _connectionError; set => Set(ref _connectionError, value); }
    internal MainWindow? Owner { get; set; }
    public bool IsPinned
    {
        get => _isPinned;
        set { if (Set(ref _isPinned, value)) OnPropertyChanged(nameof(DisplayWidth)); }
    }
    public double DisplayWidth => IsPinned ? 54 : 196;

    internal FormStateTracker? FormTracker { get; set; }
    internal bool HasUnloadHandler { get; set; }
    public bool HasUnsavedChanges { get => _hasUnsavedChanges; set => Set(ref _hasUnsavedChanges, value); }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Backing WebView2 control; null while the tab has never been shown or was discarded.</summary>
    public WebView2? View { get; set; }

    /// <summary>Id of the cosmetic/adblock script registered on this tab's WebView (null when off).</summary>
    public string? InjectionScriptId { get; set; }

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

    public bool IsInternalPage => Url.StartsWith("https://" + AppConstants.InternalHost + "/", StringComparison.OrdinalIgnoreCase)
                                  || string.IsNullOrEmpty(Url);
}
