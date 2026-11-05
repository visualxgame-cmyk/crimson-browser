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
            if (SetField(ref _isActive, value))
            {
                WebView.Visibility = value ? Visibility.Visible : Visibility.Collapsed;

                // Low-RAM Optimizer: suspend background tabs to cut memory usage
                try
                {
                    if (ShieldService.Instance.LowRamMode && WebView.CoreWebView2 != null)
                    {
                        if (value)
                        {
                            WebView.CoreWebView2.Resume();
                        }
                        else if (!WebView.CoreWebView2.IsDocumentPlayingAudio)
                        {
                            // Never suspend a background tab that is playing music or video.
                            _ = WebView.CoreWebView2.TrySuspendAsync();
                        }
                    }
                }
                catch { }
            }
        }
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

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        IsLoading = false;
        UpdateHistoryState();

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
        }
        else if (WebView.CoreWebView2 != null)
        {
            Title = string.IsNullOrWhiteSpace(WebView.CoreWebView2.DocumentTitle)
                    ? "Crimson Tab"
                    : WebView.CoreWebView2.DocumentTitle;

            // ── Record navigation in browsing history (never in Incognito) ──
            if (!IsIncognito)
                HistoryService.Instance.AddEntry(Title, _url);

            // ── Cosmetic Ad Blocker: hide empty ad slots ──
            if (ShieldService.Instance.IsShieldsEnabled && ShieldService.Instance.BlockAdsAndTrackers)
            {
                try
                {
                    await WebView.CoreWebView2.ExecuteScriptAsync(
                        "(function() {" +
                        "  var el = document.getElementById('crimson-ad-blocker-css');" +
                        "  if (!el) {" +
                        "    el = document.createElement('style');" +
                        "    el.id = 'crimson-ad-blocker-css';" +
                        "    el.innerHTML = 'ins.adsbygoogle,[id*=\"google_ads\"],[class*=\"banner-ad\"]," +
                        "[class*=\"ad-container\"],.ad-slot,.ad-banner,.adsbygoogle," +
                        "#player-ads,.ytp-ad-module,.ytp-ad-overlay-container," +
                        ".ytp-ad-player-overlay,.video-ads { display:none!important; }';" +
                        "    (document.head||document.documentElement).appendChild(el);" +
                        "  }" +
                        "})();"
                    );
                }
                catch { }

            }
        }
    }

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (WebView.Source != null)
        {
            Url = WebView.Source.ToString();
        }
        UpdateHistoryState();
    }

    private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
    {
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
