using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using CrimsonBrowser.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CrimsonBrowser.Models;

public class BrowserTab : INotifyPropertyChanged
{
    private readonly CoreWebView2Environment _environment;
    private string _title = "New Tab";
    private string _url   = "crimson://start";
    private bool   _isLoading;
    private bool   _canGoBack;
    private bool   _canGoForward;
    private bool   _isActive;
    private string? _faviconUrl;

    // ── Memory saver state ──
    private bool    _isDiscarded;
    private bool    _isDiscardNavigation;
    private bool    _hasCompletedNavigation;
    private string? _discardedUrl;
    private string? _discardedTitle;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    /// <summary>Last time this tab was visible or playing audio. Used by the discard policy.</summary>
    public DateTime LastActivityUtc => _lastActivityUtc;

    /// <summary>True when the page has been unloaded to reclaim memory; the URL is kept.</summary>
    public bool IsDiscarded => _isDiscarded;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Incognito tabs must never write to the on-disk history.</summary>
    public bool IsIncognito { get; }

    public WebView2 WebView { get; }

    /// <summary>True when this tab is showing the Crimson start page.</summary>
    public bool IsStartPage =>
        string.IsNullOrWhiteSpace(_url) ||
        _url == "crimson://start" ||
        _url.StartsWith("https://start.crimson", StringComparison.OrdinalIgnoreCase) ||
        _url.StartsWith("data:text/html",         StringComparison.OrdinalIgnoreCase);

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Url
    {
        get => _url;
        set
        {
            // Normalize start page variants so they never show in the omnibox
            string clean = (value.StartsWith("https://start.crimson", StringComparison.OrdinalIgnoreCase) ||
                            value.StartsWith("data:text/html",         StringComparison.OrdinalIgnoreCase))
                           ? "crimson://start"
                           : value;
            SetField(ref _url, clean);
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        set => SetField(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        set => SetField(ref _canGoForward, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetField(ref _isActive, value)) return;

            WebView.Visibility = value ? Visibility.Visible : Visibility.Collapsed;

            if (value)
            {
                NoteActivity();
                RestoreFromDiscard();
            }

            ApplyMemoryPolicy(value);
        }
    }

    /// <summary>Marks this tab as recently used so the discard policy gives it a fresh grace period.</summary>
    public void NoteActivity() => _lastActivityUtc = DateTime.UtcNow;

    /// <summary>
    /// Applies the Low-RAM policy: inactive tabs ask Chromium to lower their memory target and,
    /// when possible, suspend their script timers. Active tabs are always restored to Normal.
    /// </summary>
    public void ApplyMemoryPolicy(bool active)
    {
        try
        {
            var core = WebView.CoreWebView2;
            if (core is null) return;

            bool lowRam = ShieldService.Instance.LowRamMode;

            core.MemoryUsageTargetLevel = (!active && lowRam)
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;

            if (active || !lowRam)
            {
                if (core.IsSuspended) core.Resume();
            }
            else if (!_isDiscarded && _hasCompletedNavigation && !IsLoading &&
                     !core.IsDocumentPlayingAudio && !core.IsSuspended)
            {
                // Suspending pauses background timers/animations; it does not free the renderer,
                // which is why unused tabs are eventually discarded below.
                _ = core.TrySuspendAsync();
            }
        }
        catch { }
    }

    /// <summary>
    /// Chrome-style tab discard: unload the page but remember its URL and title so the session
    /// file still restores the right page. The tab reloads when the user activates it again.
    /// </summary>
    public void Discard()
    {
        if (IsActive || _isDiscarded || IsStartPage || !_hasCompletedNavigation) return;

        var core = WebView.CoreWebView2;
        if (core is null) return;

        try
        {
            if (core.IsDocumentPlayingAudio) return;

            _discardedUrl   = Url;
            _discardedTitle = Title;
            _isDiscarded    = true;
            _isDiscardNavigation = true;

            if (core.IsSuspended) core.Resume();
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;

            core.NavigateToString(
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                "<style>html,body{margin:0;height:100%;background:#0c0d10}</style>" +
                "</head><body></body></html>");
        }
        catch
        {
            _isDiscarded = false;
            _isDiscardNavigation = false;
            _discardedUrl = null;
            _discardedTitle = null;
        }
    }

    private void RestoreFromDiscard()
    {
        if (!_isDiscarded) return;

        string? target = _discardedUrl;

        _isDiscarded = false;
        _isDiscardNavigation = false;
        _discardedUrl = null;
        _discardedTitle = null;

        if (string.IsNullOrWhiteSpace(target))
            LoadStartPage();
        else
            Navigate(target);
    }

    public string? FaviconUrl
    {
        get => _faviconUrl;
        set => SetField(ref _faviconUrl, value);
    }

    public event Action<BrowserTab, string>? RequestNewTab;
    public event Action<BrowserTab>?        RequestClose;
    public event Action?                    RequestOpenSettings;
    public event Action<BrowserTab, string>? RequestShortcut;
    public event Action<bool>?              FullscreenChanged;

    /// <summary>Raised for permission requests so the host can show a Crimson-themed prompt.</summary>
    public event Action<BrowserTab, CoreWebView2PermissionRequestedEventArgs>? PermissionRequested;
    public event Action<BrowserTab, string>? InitializationFailed;

    /// <summary>Raised after the Crimson start page has finished loading.</summary>
    public event Action<BrowserTab>? StartPageReady;

    /// <summary>When true, the host should focus the omnibox once the start page is ready.</summary>
    public bool FocusOmniboxOnStartPage { get; set; }

    private string _findTerm = string.Empty;
    private string? _pendingUrl;

    public BrowserTab(CoreWebView2Environment environment, bool isIncognito = false)
    {
        _environment = environment;
        IsIncognito  = isIncognito;

        WebView = new WebView2
        {
            Visibility = Visibility.Collapsed,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(12, 13, 16)
        };

        _ = InitializeAsync(environment);
    }

    private async Task InitializeAsync(CoreWebView2Environment environment)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async(environment);

            if (WebView.CoreWebView2 != null)
            {
                // Map local resources to virtual host https://start.crimson
                string resourcesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
                if (Directory.Exists(resourcesFolder))
                {
                    WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "start.crimson",
                        resourcesFolder,
                        CoreWebView2HostResourceAccessKind.Allow
                    );
                }

                // Keep mutable user assets outside the read-only installation folder.
                WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "profile.crimson",
                    SettingsService.Instance.WallpaperFolder,
                    CoreWebView2HostResourceAccessKind.Allow);

                // Setup Crimson Shields Network Filtering (Ad/Tracker Blocker)
                WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                WebView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;

                // Configure browser settings
                WebView.CoreWebView2.Settings.IsStatusBarEnabled         = false;
                WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                WebView.CoreWebView2.Settings.AreDevToolsEnabled         = true;

                // Let Crimson handle browser shortcuts (Ctrl+F, Ctrl+P, F5, zoom...) through
                // the injected key bridge instead of WebView2's built-in accelerators.
                WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

                // Event Listeners
                WebView.CoreWebView2.NavigationStarting    += CoreWebView2_NavigationStarting;
                WebView.CoreWebView2.NavigationCompleted   += CoreWebView2_NavigationCompleted;
                WebView.CoreWebView2.SourceChanged         += CoreWebView2_SourceChanged;
                WebView.CoreWebView2.DocumentTitleChanged  += CoreWebView2_DocumentTitleChanged;
                WebView.CoreWebView2.FaviconChanged        += CoreWebView2_FaviconChanged;
                WebView.CoreWebView2.NewWindowRequested    += CoreWebView2_NewWindowRequested;
                WebView.CoreWebView2.WindowCloseRequested  += CoreWebView2_WindowCloseRequested;
                WebView.CoreWebView2.WebMessageReceived    += CoreWebView2_WebMessageReceived;
                WebView.CoreWebView2.DownloadStarting      += CoreWebView2_DownloadStarting;
                WebView.CoreWebView2.PermissionRequested   += CoreWebView2_PermissionRequested;
                WebView.CoreWebView2.FrameCreated          += CoreWebView2_FrameCreated;
                WebView.CoreWebView2.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;

                await InstallAdBlockScriptAsync();
                await InstallKeyboardBridgeAsync();

                if (!string.IsNullOrWhiteSpace(_pendingUrl))
                {
                    string pending = _pendingUrl;
                    _pendingUrl = null;
                    Navigate(pending);
                }
                else
                {
                    LoadStartPage();
                }

                // New tabs may start in the background (session restore), so apply the
                // current Low-RAM policy as soon as the WebView is ready.
                ApplyMemoryPolicy(IsActive);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize WebView2: {ex.Message}");
            InitializationFailed?.Invoke(this, ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NETWORK: Crimson Shields Ad / Tracker Blocker
    // ─────────────────────────────────────────────────────────────────────────

    private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (ShieldService.Instance.ShouldBlockUrl(e.Request.Uri))
        {
            // Block request with an empty 403 Forbidden response
            e.Response = _environment.CreateWebResourceResponse(null, 403, "Blocked by Crimson Shields", null);
            ShieldService.Instance.IncrementBlockedCount();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NAVIGATION EVENTS
    // ─────────────────────────────────────────────────────────────────────────

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The discard placeholder is not a real navigation: keep the tab's URL/title intact.
        if (_isDiscardNavigation) return;

        // Enforce HTTPS if the shield setting is on
        if (ShieldService.Instance.IsShieldsEnabled &&
            ShieldService.Instance.EnforceHttps &&
            e.Uri.StartsWith("http://",        StringComparison.OrdinalIgnoreCase) &&
            !e.Uri.StartsWith("http://localhost",  StringComparison.OrdinalIgnoreCase) &&
            !e.Uri.StartsWith("http://127.0.0.1",  StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            string secureUri = "https://" + e.Uri.Substring(7);
            WebView.CoreWebView2?.Navigate(secureUri);
            return;
        }

        IsLoading = true;
        Url       = e.Uri;
        UpdateHistoryState();
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_isDiscardNavigation)
        {
            // Placeholder finished loading. The real URL/title were never touched.
            _isDiscardNavigation = false;
            IsLoading = false;
            return;
        }

        if (_isDiscarded)
        {
            IsLoading = false;
            return;
        }

        _hasCompletedNavigation = true;
        IsLoading = false;
        UpdateHistoryState();

        if (IsActive) NoteActivity();

        if (IsStartPage)
        {
            Title = LocalizationService.Instance.Get("NewTab");
            Url   = "crimson://start";

            // Sync language and wallpaper into the start page
            try
            {
                string lang = LocalizationService.Instance.CurrentLanguage;
                _ = WebView.CoreWebView2?.ExecuteScriptAsync($"applyLanguage('{lang}'); checkWallpaper();");
            }
            catch { }

            if (FocusOmniboxOnStartPage)
            {
                FocusOmniboxOnStartPage = false;
                StartPageReady?.Invoke(this);
            }
        }
        else if (WebView.CoreWebView2 != null)
        {
            Title = string.IsNullOrWhiteSpace(WebView.CoreWebView2.DocumentTitle)
                    ? "Crimson Tab"
                    : WebView.CoreWebView2.DocumentTitle;

            // ── Record navigation in browsing history (never in Incognito) ──
            if (!IsIncognito)
                HistoryService.Instance.AddEntry(Title, _url);
        }
    }

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        // Ignore the source update caused by the discard placeholder.
        if (_isDiscarded || _isDiscardNavigation) return;

        if (WebView.Source != null)
        {
            Url = WebView.Source.ToString();
        }
        UpdateHistoryState();
    }

    private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
    {
        if (_isDiscarded || _isDiscardNavigation)
        {
            // Keep the original tab title while the placeholder is loaded.
            if (!string.IsNullOrWhiteSpace(_discardedTitle)) Title = _discardedTitle!;
            return;
        }

        if (IsStartPage)
        {
            Title = LocalizationService.Instance.Get("NewTab");
            return;
        }

        if (WebView.CoreWebView2 != null && !string.IsNullOrWhiteSpace(WebView.CoreWebView2.DocumentTitle))
        {
            Title = WebView.CoreWebView2.DocumentTitle;
        }
    }

    private void CoreWebView2_FaviconChanged(object? sender, object e)
    {
        if (WebView.CoreWebView2 != null)
        {
            FaviconUrl = WebView.CoreWebView2.FaviconUri;
        }
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // A real pop-up blocker: scripts cannot open windows without a user gesture.
        if (ShieldService.Instance.IsShieldsEnabled &&
            ShieldService.Instance.BlockPopups &&
            !e.IsUserInitiated)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        RequestNewTab?.Invoke(this, e.Uri);
    }

    private void CoreWebView2_WindowCloseRequested(object? sender, object e)
    {
        RequestClose?.Invoke(this);
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string message = e.TryGetWebMessageAsString();
            if (message == "open-settings")
            {
                RequestOpenSettings?.Invoke();
            }
            else if (message.StartsWith("crimson:", StringComparison.Ordinal))
            {
                RequestShortcut?.Invoke(this, message["crimson:".Length..]);
            }
        }
        catch { }
    }

    private void CoreWebView2_ContainsFullScreenElementChanged(object? sender, object e)
    {
        FullscreenChanged?.Invoke(WebView.CoreWebView2?.ContainsFullScreenElement == true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SITE PERMISSIONS
    // ─────────────────────────────────────────────────────────────────────────

    private void CoreWebView2_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.Handled = true;
        RaisePermissionRequested(e);
    }

    private void CoreWebView2_FrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
    {
        // Third-party frames can request permissions too; route them to the same prompt.
        e.Frame.PermissionRequested += (_, args) =>
        {
            args.Handled = true;
            RaisePermissionRequested(args);
        };
    }

    private void RaisePermissionRequested(CoreWebView2PermissionRequestedEventArgs args)
    {
        if (PermissionRequested is { } handler)
        {
            handler(this, args);
            return;
        }

        // No host is listening: fail closed instead of leaving Chromium's default UI.
        try { args.State = CoreWebView2PermissionState.Deny; } catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CRIMSON SHIELDS: injected cosmetic filtering + YouTube ad skipping
    // ─────────────────────────────────────────────────────────────────────────

    private async Task InstallAdBlockScriptAsync()
    {
        const string script = """
            (function () {
                if (window.__crimsonAdShield) return;
                window.__crimsonAdShield = true;

                function isYouTube() {
                    var host = (location.hostname || '').toLowerCase();
                    return host === 'youtube.com' ||
                           host.endsWith('.youtube.com') ||
                           host.endsWith('.youtube-nocookie.com');
                }

                // YouTube embeds its ad schedule in the initial page data and in youtubei/v1/player
                // responses. Removing that metadata before the player reads it prevents any ad
                // break from being scheduled at all, so content starts immediately. The CSS hiding
                // below stays as a fallback for ads that slip through in the future.
                if (isYouTube()) {
                    var adKeys = [
                        'adPlacements',
                        'playerAds',
                        'adSlots',
                        'adBreakHeartbeatParams',
                        'adParams',
                        'playerAdParams'
                    ];

                    function stripAds(root) {
                        if (!root || typeof root !== 'object') return;
                        var queue = [root];
                        var seen = new WeakSet();
                        var visited = 0;

                        while (queue.length && visited < 20000) {
                            visited++;
                            var node = queue.pop();
                            if (!node || typeof node !== 'object' || seen.has(node)) continue;
                            seen.add(node);

                            if (!Array.isArray(node)) {
                                for (var i = 0; i < adKeys.length; i++) {
                                    var key = adKeys[i];
                                    if (!(key in node)) continue;

                                    if (key === 'adBreakHeartbeatParams' ||
                                        key === 'adParams' ||
                                        key === 'playerAdParams') {
                                        try { delete node[key]; } catch (e) { }
                                    } else if (Array.isArray(node[key])) {
                                        node[key] = [];
                                    } else {
                                        try { node[key] = undefined; } catch (e) { }
                                    }
                                }
                            }

                            for (var k in node) {
                                if (!Object.prototype.hasOwnProperty.call(node, k)) continue;
                                var value = node[k];
                                if (value && typeof value === 'object') queue.push(value);
                            }
                        }
                    }

                    // The first player response is assigned to a global instead of being parsed
                    // by JSON.parse, so intercept the assignment as well.
                    function guardInitialData(name) {
                        var current;
                        try {
                            Object.defineProperty(window, name, {
                                configurable: true,
                                enumerable: true,
                                get: function () { return current; },
                                set: function (value) {
                                    try { stripAds(value); } catch (e) { }
                                    current = value;
                                }
                            });
                        } catch (e) { }
                    }

                    ['ytInitialPlayerResponse', 'ytInitialData', 'ytInitialGuideData',
                     'ytInitialReelWatchSequenceResponse', 'ytInitialPlayerResponsePromise']
                        .forEach(guardInitialData);

                    try {
                        var nativeJsonParse = JSON.parse;
                        JSON.parse = function (text, reviver) {
                            var result = nativeJsonParse(text, reviver);
                            if (typeof text === 'string' &&
                                (text.indexOf('adPlacements') !== -1 ||
                                 text.indexOf('playerAds') !== -1 ||
                                 text.indexOf('adSlots') !== -1 ||
                                 text.indexOf('adBreakHeartbeatParams') !== -1)) {
                                try { stripAds(result); } catch (e) { }
                            }
                            return result;
                        };
                    } catch (e) { }
                }

                // Ads share the same <video> element as real content, so hiding it only while the
                // player carries the ad-showing class keeps the ad from ever being painted while
                // letting the content frame reappear instantly when the class is removed.
                var videoHideCss =
                    '#movie_player.ad-showing video,' +
                    '.html5-video-player.ad-showing video.html5-main-video,' +
                    '#movie_player.ad-showing .video-ads' +
                    '{ visibility: hidden !important; opacity: 0 !important; }';

                var cosmeticCss = [
                    'ins.adsbygoogle',
                    '.adsbygoogle',
                    '[id^="google_ads"]',
                    '[id^="div-gpt-ad"]',
                    '[class*="banner-ad"]',
                    '[class*="ad-container"]',
                    '[class*="ad-slot"]',
                    '[class*="ad-banner"]',
                    '[class*="sponsored"]',
                    '.ad-slot',
                    '.ad-banner',
                    '#player-ads',
                    '#masthead-ad',
                    'ytd-ad-slot-renderer',
                    'ytd-in-feed-ad-layout-renderer',
                    'ytd-display-ad-renderer',
                    'ytd-promoted-sparkles-web-renderer',
                    'ytd-action-companion-ad-renderer',
                    'ytd-companion-slot-renderer',
                    'ytd-player-legacy-desktop-watch-ads-renderer',
                    '.ytp-ad-module',
                    '.ytp-ad-overlay-container',
                    '.ytp-ad-overlay-slot',
                    '.ytp-ad-player-overlay',
                    '.ytp-ad-player-overlay-layout',
                    '.ytp-ad-image-overlay',
                    '.ytp-ad-text-overlay',
                    '.ytp-ad-preview-container',
                    '.ytp-ad-badge',
                    '.ytp-ad-progress-list',
                    '.ytp-ad-survey',
                    '.video-ads'
                ].join(',') + '{ display: none !important; }';

                function injectCss() {
                    var parent = document.head || document.documentElement;
                    if (!parent) return false;
                    if (document.getElementById('crimson-ad-blocker-css')) return true;
                    var style = document.createElement('style');
                    style.id = 'crimson-ad-blocker-css';
                    style.textContent = videoHideCss + cosmeticCss;
                    parent.appendChild(style);
                    return true;
                }

                function clickFirst(selectors) {
                    for (var i = 0; i < selectors.length; i++) {
                        var button = document.querySelector(selectors[i]);
                        if (button) {
                            try { button.click(); return true; } catch (e) { }
                        }
                    }
                    return false;
                }

                function player() {
                    return document.getElementById('movie_player');
                }

                function adShowing() {
                    var current = player();
                    if (current && current.classList.contains('ad-showing')) return true;
                    return !!document.querySelector('.html5-video-player.ad-showing');
                }

                function videoElement() {
                    return document.querySelector('#movie_player video.html5-main-video') ||
                           document.querySelector('video.html5-main-video') ||
                           document.querySelector('video');
                }

                // Save the user's media state once per ad break so mute/volume/speed survive it.
                function stashMediaState(video) {
                    if (video.__crimsonAdState) return;
                    video.__crimsonAdState = {
                        muted: video.muted,
                        volume: video.volume,
                        rate: video.playbackRate
                    };
                }

                function restoreMediaState(video) {
                    var state = video.__crimsonAdState;
                    if (!state) return;
                    delete video.__crimsonAdState;
                    try { video.muted = state.muted; } catch (e) { }
                    try { video.volume = state.volume; } catch (e) { }
                    try { video.playbackRate = state.rate; } catch (e) { }
                }

                function skipAd() {
                    injectCss();

                    if (!isYouTube()) return;

                    var video = videoElement();
                    var showing = adShowing();

                    if (!showing) {
                        if (video) restoreMediaState(video);
                        return;
                    }

                    if (video) {
                        stashMediaState(video);
                        try { video.muted = true; } catch (e) { }

                        // Seekable ads end immediately. Unskippable ones cannot be seeked past,
                        // but they run muted and invisibly at 16x, so the wait is imperceptible.
                        try { if (video.playbackRate < 16) video.playbackRate = 16; } catch (e) { }
                        try {
                            var duration = video.duration;
                            if (isFinite(duration) && duration > 0) {
                                var target = Math.max(0, duration - 0.1);
                                if (video.currentTime < target) video.currentTime = target;
                            }
                        } catch (e) { }
                    }

                    // Dismiss any skip/close button that YouTube has already rendered.
                    clickFirst([
                        '.ytp-ad-skip-button',
                        '.ytp-ad-skip-button-modern',
                        '.ytp-skip-ad-button',
                        '.ytp-ad-skip-button-container button',
                        'button.ytp-ad-skip-button',
                        '.ytp-ad-survey-answer-button',
                        '.ytp-ad-overlay-close-button'
                    ]);
                }

                var rafId = 0;
                function watchAdFrames() {
                    rafId = 0;
                    skipAd();
                    if (adShowing()) rafId = window.requestAnimationFrame(watchAdFrames);
                }

                function kick() {
                    skipAd();
                    if (!rafId && adShowing()) rafId = window.requestAnimationFrame(watchAdFrames);
                }

                // The player class flips between content and ad mode; react to the flip at once.
                var observedPlayer = null;
                function observePlayer() {
                    var current = player();
                    if (!current || current === observedPlayer) return;
                    observedPlayer = current;
                    try {
                        new MutationObserver(kick).observe(current, {
                            attributes: true,
                            attributeFilter: ['class']
                        });
                    } catch (e) { }
                }

                // Media events fire before a new ad frame is painted, so handle them synchronously.
                ['loadstart', 'loadedmetadata', 'durationchange', 'play', 'playing',
                 'timeupdate', 'ratechange', 'volumechange', 'emptied'].forEach(function (name) {
                    document.addEventListener(name, function (event) {
                        var target = event.target;
                        if (target && target.tagName === 'VIDEO') kick();
                    }, true);
                });

                document.addEventListener('yt-navigate-finish', function () {
                    observePlayer();
                    kick();
                }, true);
                document.addEventListener('yt-page-data-updated', function () {
                    observePlayer();
                    kick();
                }, true);

                injectCss();
                observePlayer();
                kick();

                try {
                    new MutationObserver(function () {
                        injectCss();
                        observePlayer();
                    }).observe(document.documentElement, { childList: true, subtree: true });
                } catch (e) { }

                // Safety net for pages that create or replace the player after load.
                window.setInterval(function () {
                    injectCss();
                    observePlayer();
                    kick();
                }, 250);
            })();
            """;

        try
        {
            if (WebView.CoreWebView2 != null)
                await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch { }
    }

    /// <summary>
    /// WPF does not receive keyboard input while WebView2 has focus, so browser shortcuts are
    /// captured in the page (capture phase) and forwarded to the host over postMessage.
    /// </summary>
    private async Task InstallKeyboardBridgeAsync()
    {
        const string script = """
            (function () {
                if (window.__crimsonKeyBridge) return;
                window.__crimsonKeyBridge = true;

                function send(action) {
                    try { window.chrome.webview.postMessage('crimson:' + action); } catch (e) {}
                }

                window.addEventListener('keydown', function (e) {
                    var ctrl = e.ctrlKey || e.metaKey;
                    var key = (e.key || '').toLowerCase();
                    var action = null;

                    if (e.key === 'F11') action = 'fullscreen';
                    else if (e.key === 'F5') action = 'reload';
                    else if (e.key === 'Escape') { send('escape'); return; }
                    else if (ctrl && e.shiftKey && !e.altKey) {
                        if (e.key === 'Tab') action = 'prev-tab';
                        else if (key === 't') action = 'reopen-tab';
                        else if (key === 'n') action = 'incognito';
                        else if (key === 'b') action = 'bookmarks-bar';
                    }
                    else if (ctrl && !e.shiftKey && !e.altKey) {
                        if (e.key === 'Tab') action = 'next-tab';
                        else if (key === 't') action = 'new-tab';
                        else if (key === 'n') action = 'new-window';
                        else if (key === 'w') action = 'close-tab';
                        else if (key === 'l') action = 'focus-omnibox';
                        else if (key === 'r') action = 'reload';
                        else if (key === 'd') action = 'bookmark';
                        else if (key === 'h') action = 'history';
                        else if (key === 'f') action = 'find';
                        else if (key === '0') action = 'zoom-reset';
                        else if (key === '+' || key === '=') action = 'zoom-in';
                        else if (key === '-') action = 'zoom-out';
                        else if (key >= '1' && key <= '9') action = 'tab-' + key;
                    }

                    if (action) {
                        e.preventDefault();
                        e.stopPropagation();
                        send(action);
                    }
                }, true);
            })();
            """;

        try
        {
            if (WebView.CoreWebView2 != null)
                await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FIND IN PAGE
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<(int Index, int Count)> FindAsync(string term, bool backward = false)
    {
        if (WebView.CoreWebView2 == null) return (0, 0);

        try
        {
            var find = WebView.CoreWebView2.Find;

            if (term != _findTerm)
            {
                _findTerm = term;
                var options = _environment.CreateFindOptions();
                options.FindTerm = term;
                options.SuppressDefaultFindDialog = true;
                options.ShouldHighlightAllMatches = true;
                await find.StartAsync(options);
            }
            else if (backward)
            {
                find.FindPrevious();
            }
            else
            {
                find.FindNext();
            }

            return (find.ActiveMatchIndex, find.MatchCount);
        }
        catch
        {
            return (0, 0);
        }
    }

    public void StopFind()
    {
        _findTerm = string.Empty;
        try { WebView.CoreWebView2?.Find.Stop(); } catch { }
    }

    private void CoreWebView2_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // Let Chromium handle the file saving; track progress in our Downloads hub
        e.Handled = false;
        DownloadsService.Instance.Track(e.DownloadOperation);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUBLIC API
    // ─────────────────────────────────────────────────────────────────────────

    public void UpdateStartPageLanguage(string lang)
    {
        if (IsStartPage && WebView.CoreWebView2 != null)
        {
            _ = WebView.CoreWebView2.ExecuteScriptAsync($"applyLanguage('{lang}');");
        }
    }

    public void RefreshStartPageWallpaper()
    {
        if (IsStartPage && WebView.CoreWebView2 != null)
        {
            _ = WebView.CoreWebView2.ExecuteScriptAsync("checkWallpaper();");
        }
    }

    public void UpdateHistoryState()
    {
        CanGoBack    = WebView.CanGoBack;
        CanGoForward = WebView.CanGoForward;
    }

    public void Navigate(string target)
    {
        // An explicit navigation replaces the discarded placeholder.
        if (_isDiscarded)
        {
            _isDiscarded = false;
            _isDiscardNavigation = false;
            _discardedUrl = null;
            _discardedTitle = null;
        }

        if (string.IsNullOrWhiteSpace(target) || target == "crimson://start")
        {
            LoadStartPage();
            return;
        }

        string cleanTarget = target.Trim().Trim('"');

        if (File.Exists(cleanTarget))
            cleanTarget = new Uri(cleanTarget).AbsoluteUri;

        if (!cleanTarget.StartsWith("http://",   StringComparison.OrdinalIgnoreCase) &&
            !cleanTarget.StartsWith("https://",  StringComparison.OrdinalIgnoreCase) &&
            !cleanTarget.StartsWith("file://",   StringComparison.OrdinalIgnoreCase) &&
            !cleanTarget.StartsWith("about:",    StringComparison.OrdinalIgnoreCase) &&
            !cleanTarget.StartsWith("data:",     StringComparison.OrdinalIgnoreCase))
        {
            if (cleanTarget.Contains('.') && !cleanTarget.Contains(' '))
            {
                cleanTarget = "https://" + cleanTarget;
            }
            else
            {
                // Use whatever search engine the user has selected in Settings
                cleanTarget = SettingsService.Instance.BuildSearchUrl(cleanTarget);
            }
        }

        if (WebView.CoreWebView2 is null)
        {
            // WebView2 is still initializing; replay this navigation once it is ready.
            _pendingUrl = cleanTarget;
            return;
        }

        try
        {
            WebView.CoreWebView2.Navigate(cleanTarget);
        }
        catch { }
    }

    public void LoadStartPage()
    {
        Url   = "crimson://start";
        Title = LocalizationService.Instance.Get("NewTab");

        try
        {
            if (WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.Navigate("https://start.crimson/startpage.html");
                UpdateHistoryState();
                return;
            }
        }
        catch { }

        // Fallback: load from disk (before virtual host is ready)
        string startHtmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "startpage.html");
        if (File.Exists(startHtmlPath))
        {
            string html = File.ReadAllText(startHtmlPath);
            WebView.NavigateToString(html);
        }
        UpdateHistoryState();
    }

    public void GoBack()
    {
        if (WebView.CanGoBack) WebView.GoBack();
    }

    public void GoForward()
    {
        if (WebView.CanGoForward) WebView.GoForward();
    }

    public void Reload()
    {
        if (_isDiscarded)
        {
            RestoreFromDiscard();
            return;
        }

        if (WebView.CoreWebView2 is null)
        {
            _ = InitializeAsync(_environment);
            return;
        }

        if (IsStartPage)
            LoadStartPage();
        else
            WebView.Reload();
    }

    public void Stop()
    {
        WebView.Stop();
    }

    public void Dispose()
    {
        try
        {
            if (WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.WebResourceRequested  -= CoreWebView2_WebResourceRequested;
                WebView.CoreWebView2.NavigationStarting    -= CoreWebView2_NavigationStarting;
                WebView.CoreWebView2.NavigationCompleted   -= CoreWebView2_NavigationCompleted;
                WebView.CoreWebView2.SourceChanged         -= CoreWebView2_SourceChanged;
                WebView.CoreWebView2.DocumentTitleChanged  -= CoreWebView2_DocumentTitleChanged;
                WebView.CoreWebView2.FaviconChanged        -= CoreWebView2_FaviconChanged;
                WebView.CoreWebView2.NewWindowRequested    -= CoreWebView2_NewWindowRequested;
                WebView.CoreWebView2.WindowCloseRequested  -= CoreWebView2_WindowCloseRequested;
                WebView.CoreWebView2.WebMessageReceived    -= CoreWebView2_WebMessageReceived;
                WebView.CoreWebView2.DownloadStarting      -= CoreWebView2_DownloadStarting;
                WebView.CoreWebView2.PermissionRequested   -= CoreWebView2_PermissionRequested;
                WebView.CoreWebView2.FrameCreated          -= CoreWebView2_FrameCreated;
                WebView.CoreWebView2.ContainsFullScreenElementChanged -= CoreWebView2_ContainsFullScreenElementChanged;
            }
            WebView.Dispose();
        }
        catch { }
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
