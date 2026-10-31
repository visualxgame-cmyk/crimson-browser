using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class MainWindow : Window
{
    private const string IconGlobe = "\uE774";
    private const string IconStarOutline = "\uE734";
    private const string IconStarFilled = "\uE735";
    private const string IconReload = "\uE72C";
    private const string IconStop = "\uE711";
    private const string IconLock = "\uE72E";
    private const string IconUnlock = "\uE785";

    private readonly ObservableCollection<BrowserTab> _tabs = new();
    private readonly List<(string Url, string Title, int Index)> _closedTabs = new();

    private Settings _settings;
    private readonly AdBlocker _blocker = new();
    private readonly HistoryStore _history = new();
    private readonly BookmarksStore _bookmarks = new();

    private readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private CoreWebView2Environment? _env;
    private BrowserTab? _active;
    private IntPtr _hwnd;
    private string? _transientStatus;
    private MemorySnapshot _lastMemory;
    private bool _shuttingDown;
    private bool _isFullscreen;
    private bool _appliedBlockAds;
    private SettingsWindow? _settingsWindow;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    public MainWindow() : this(null) { }

    public MainWindow(CoreWebView2Environment? sharedEnvironment)
    {
        AppPaths.EnsureCreated();

        _env = sharedEnvironment;
        _settings = SettingsStore.Load();
        _appliedBlockAds = _settings.BlockAds;
        _blocker.Enabled = _settings.BlockAds;
        _blocker.Load();

        InitializeComponent();

        TabItems.ItemsSource = _tabs;
        _tabs.CollectionChanged += (_, _) => UpdateStatusBar();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;

        _sleepTimer.Tick += OnSleepTick;
        _memoryTimer.Tick += OnMemoryTick;
        _statusTimer.Tick += OnStatusTick;

        PreviewKeyDown += OnWindowPreviewKeyDown;

        // WebView2 keeps the page in a child window: without this, shortcuts only worked
        // while the chrome itself had focus.
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            KeyboardShortcuts.Register(_hwnd, HandleShortcut);
        };
        Closed += (_, _) => KeyboardShortcuts.Unregister(_hwnd);
    }

    // =====================================================================
    //  Startup / shutdown
    // =====================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_env is null)
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--autoplay-policy=user-gesture-required --no-default-browser-check",
                    Language = "ro-RO",
                };

                _env = await CoreWebView2Environment.CreateAsync(null, AppPaths.Profile, options);
            }
            catch (Exception ex)
            {
                Log.Write("WebView2 init failed: " + ex);
                MessageBox.Show(this,
                    "WebView2 Runtime nu poate porni.\n\n" + ex.Message +
                    "\n\nInstalează „Microsoft Edge WebView2 Runtime” și încearcă din nou.",
                    "Vela", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
        }

        RestoreSession();

        _sleepTimer.Start();
        _memoryTimer.Start();
        UpdateStatusBar();

        // First sample right away so the status bar is not empty.
        OnMemoryTick(this, EventArgs.Empty);
    }

    private void RestoreSession()
    {
        var state = _settings.RestoreSession ? SessionStore.Load() : null;

        if (state is { Tabs.Count: > 0 })
        {
            foreach (var saved in state.Tabs)
            {
                if (string.IsNullOrWhiteSpace(saved.Url)) continue;

                var tab = CreateTabModel(saved.Url);
                if (!string.IsNullOrWhiteSpace(saved.Title) && saved.Title != saved.Url)
                    tab.Title = saved.Title;
            }

            if (_tabs.Count > 0)
            {
                var index = Math.Clamp(state.ActiveIndex, 0, _tabs.Count - 1);
                _ = ActivateTabAsync(_tabs[index]);
                return;
            }
        }

        _ = AddTabAsync(AppConstants.NewTabUrl);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _shuttingDown = true;
        _sleepTimer.Stop();
        _memoryTimer.Stop();
        _statusTimer.Stop();

        SaveSession();

        SettingsStore.Save(_settings);
        _history.Flush();
        _bookmarks.Flush();

        foreach (var tab in _tabs.ToList())
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
            try { tab.View?.Dispose(); }
            catch (Exception ex) { Log.Write("Dispose tab: " + ex.Message); }
        }

        _tabs.Clear();
    }

    private void SaveSession()
    {
        if (!_settings.RestoreSession)
        {
            SessionStore.Clear();
            return;
        }

        var state = new SessionState { ActiveIndex = Math.Max(0, _active is null ? 0 : _tabs.IndexOf(_active)) };

        foreach (var tab in _tabs)
        {
            var url = !string.IsNullOrWhiteSpace(tab.Url) ? tab.Url : tab.PendingUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;
            state.Tabs.Add(new SessionTab(url, tab.Title));
        }

        if (state.Tabs.Count > 0) SessionStore.Save(state);
    }

    // =====================================================================
    //  Tabs
    // =====================================================================

    private BrowserTab CreateTabModel(string url)
    {
        var tab = new BrowserTab { Url = url, PendingUrl = url };
        tab.PropertyChanged += OnTabPropertyChanged;
        _tabs.Add(tab);
        return tab;
    }

    private async Task<BrowserTab> AddTabAsync(string url, bool select = true, int? insertAt = null)
    {
        var tab = CreateTabModel(url);

        if (insertAt is int index && index >= 0 && index < _tabs.Count - 1)
            _tabs.Move(_tabs.Count - 1, index);

        if (select) await ActivateTabAsync(tab);

        return tab;
    }

    private async Task ActivateTabAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab) || _shuttingDown) return;

        if (_active is not null && !ReferenceEquals(_active, tab))
            _active.IsSelected = false;

        _active = tab;
        tab.IsSelected = true;
        tab.LastActiveUtc = DateTime.UtcNow;

        var view = tab.View;
        if (view is null) view = await EnsureViewAsync(tab);
        if (view is null) return;

        foreach (var other in _tabs)
        {
            if (other.View is null) continue;
            other.View.Visibility = ReferenceEquals(other, tab) ? Visibility.Visible : Visibility.Collapsed;
        }

        var core = view.CoreWebView2;
        if (core is not null)
        {
            if (tab.IsSuspended)
            {
                try { core.Resume(); } catch { /* already resumed */ }
                tab.IsSuspended = false;
            }

            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }

        SetOmnibox(tab.Url);
        UpdateChrome();

        try { view.Focus(); } catch { }
    }

    private async Task<WebView2?> EnsureViewAsync(BrowserTab tab)
    {
        if (_env is null) return null;
        if (tab.View is not null) return tab.View;

        var target = ResolveUrl(string.IsNullOrWhiteSpace(tab.PendingUrl) ? _settings.HomePage : tab.PendingUrl);
        tab.PendingUrl = "";

        var view = new WebView2
        {
            Visibility = ReferenceEquals(tab, _active) ? Visibility.Visible : Visibility.Collapsed,
            DefaultBackgroundColor = ToDrawingColor(
                IsNewTabUrl(target)
                    ? ThemeCatalog.ResolveNewTabBackground(ThemeCatalog.Resolve(_settings), _settings)
                    : ThemeCatalog.Resolve(_settings).ContentBg),
        };

        tab.View = view;
        ContentHost.Children.Add(view);

        // Let WPF create the child HWND before WebView2 tries to attach to it.
        await Dispatcher.Yield(DispatcherPriority.Loaded);

        try
        {
            await view.EnsureCoreWebView2Async(_env);
        }
        catch (Exception ex)
        {
            Log.Write("EnsureCoreWebView2Async: " + ex);
            Status("Pagina nu a putut fi deschisă: " + ex.Message);
            ContentHost.Children.Remove(view);
            tab.View = null;
            return null;
        }

        var core = view.CoreWebView2;
        await ConfigureCoreAsync(tab, core);

        core.Navigate(target);
        return view;
    }

    private async Task ConfigureCoreAsync(BrowserTab tab, CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreDevToolsEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // shortcuts are handled by Vela itself
        settings.IsSwipeNavigationEnabled = true;
        settings.IsPasswordAutosaveEnabled = true;
        settings.IsGeneralAutofillEnabled = true;
        settings.IsZoomControlEnabled = true;
        settings.IsWebMessageEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.IsBuiltInErrorPageEnabled = true;

        try
        {
            core.SetVirtualHostNameToFolderMapping(
                AppConstants.InternalHost, AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);

            // New tab background images picked in settings.
            core.SetVirtualHostNameToFolderMapping(
                AppConstants.BackgroundHost, AppPaths.Backgrounds, CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch (Exception ex)
        {
            Log.Write("Virtual host mapping: " + ex.Message);
        }

        core.NavigationStarting += (_, e) =>
        {
            tab.IsLoading = true;
            if (!string.IsNullOrEmpty(e.Uri)) tab.Url = e.Uri;
            if (ReferenceEquals(tab, _active) && !Omnibox.IsKeyboardFocusWithin)
                SetOmnibox(tab.Url);
        };

        core.SourceChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(core.Source)) tab.Url = core.Source;
            if (ReferenceEquals(tab, _active) && !Omnibox.IsKeyboardFocusWithin)
                SetOmnibox(tab.Url);
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            var title = core.DocumentTitle;
            tab.Title = string.IsNullOrWhiteSpace(title) ? UrlHelper.HostOf(tab.Url) : title;
        };

        core.FaviconChanged += async (_, _) => await UpdateFaviconAsync(tab, core);

        core.NavigationCompleted += (_, e) =>
        {
            tab.IsLoading = false;
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;

            if (ReferenceEquals(tab, _active)) UpdateChrome();

            if (e.IsSuccess && !tab.IsInternalPage)
                _history.Add(core.Source ?? tab.Url, tab.Title);
        };

        core.HistoryChanged += (_, _) =>
        {
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
        };

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var target = string.IsNullOrWhiteSpace(e.Uri) ? AppConstants.NewTabUrl : e.Uri;
            Dispatcher.InvokeAsync(() => _ = AddTabAsync(target));
        };

        core.WebMessageReceived += (_, e) => OnWebMessage(tab, e);
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ProcessFailed += (_, e) => OnProcessFailed(tab, e);

        _blocker.Attach(
            core,
            () => tab.ShieldEnabled && _settings.BlockAds,
            () => tab.Url,
            () =>
            {
                tab.BlockedCount++;
                if (ReferenceEquals(tab, _active) && ShieldPopup.IsOpen) UpdateShieldPopup(tab);
            });

        if (tab.ShieldEnabled && _settings.BlockAds)
            await AddInjectionAsync(tab, core);
    }

    private async Task AddInjectionAsync(BrowserTab tab, CoreWebView2 core)
    {
        if (tab.InjectionScriptId is not null || string.IsNullOrEmpty(_blocker.InjectionScript)) return;

        try
        {
            tab.InjectionScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(_blocker.InjectionScript);
        }
        catch (Exception ex)
        {
            Log.Write("Cosmetic injection: " + ex.Message);
        }
    }

    private Task RemoveInjectionAsync(BrowserTab tab, CoreWebView2 core)
    {
        var id = tab.InjectionScriptId;
        if (id is null) return Task.CompletedTask;

        tab.InjectionScriptId = null;
        try
        {
            core.RemoveScriptToExecuteOnDocumentCreated(id);
        }
        catch (Exception ex)
        {
            Log.Write("Cosmetic removal: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    private static async Task UpdateFaviconAsync(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            await using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null) return;

            // The native favicon stream is not seekable (and cannot be frozen), so copy it
            // into memory first: WPF needs a seekable stream for BitmapImage.
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = buffer;
            bitmap.EndInit();
            bitmap.Freeze();

            tab.Favicon = bitmap;
        }
        catch (Exception ex)
        {
            Log.Write("Favicon: " + ex.Message);
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BrowserTab tab) return;

        if (ReferenceEquals(tab, _active))
        {
            switch (e.PropertyName)
            {
                case nameof(BrowserTab.CanGoBack):
                case nameof(BrowserTab.CanGoForward):
                case nameof(BrowserTab.IsLoading):
                case nameof(BrowserTab.BlockedCount):
                case nameof(BrowserTab.ShieldEnabled):
                    UpdateChrome();
                    break;

                case nameof(BrowserTab.Url):
                    if (!Omnibox.IsKeyboardFocusWithin) SetOmnibox(tab.Url);
                    break;
            }
        }

        UpdateStatusBar();
    }

    private void SelectTab(BrowserTab tab) => _ = ActivateTabAsync(tab);

    private void CloseTab(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;

        var index = _tabs.IndexOf(tab);
        var url = !string.IsNullOrWhiteSpace(tab.Url) ? tab.Url : tab.PendingUrl;
        if (!string.IsNullOrWhiteSpace(url)) _closedTabs.Add((url, tab.Title, index));
        if (_closedTabs.Count > 25) _closedTabs.RemoveAt(0);

        tab.PropertyChanged -= OnTabPropertyChanged;

        if (tab.View is not null)
        {
            ContentHost.Children.Remove(tab.View);
            try { tab.View.Dispose(); }
            catch (Exception ex) { Log.Write("Close tab dispose: " + ex.Message); }
            tab.View = null;
        }

        var wasActive = ReferenceEquals(tab, _active);
        _tabs.Remove(tab);

        if (!wasActive)
        {
            UpdateStatusBar();
            return;
        }

        _active = null;

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        var next = _tabs[Math.Clamp(index, 0, _tabs.Count - 1)];
        _ = ActivateTabAsync(next);
    }

    private void CloseActiveTab()
    {
        if (_active is not null) CloseTab(_active);
    }

    private void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0)
        {
            Status("Nu există tab-uri închise recent.");
            return;
        }

        var last = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        _ = AddTabAsync(last.Url, true, last.Index);
    }

    /// <summary>Drops the tab's WebView2 entirely; the page is reloaded on next activation.</summary>
    private void DiscardTab(BrowserTab tab, bool force = false)
    {
        if (!force && ReferenceEquals(tab, _active)) return;

        var view = tab.View;
        if (view is null) return;

        var url = !string.IsNullOrWhiteSpace(tab.Url) ? tab.Url : tab.PendingUrl;

        tab.View = null;
        tab.InjectionScriptId = null;
        ContentHost.Children.Remove(view);
        try { view.Dispose(); }
        catch (Exception ex) { Log.Write("Discard tab: " + ex.Message); }

        tab.IsSuspended = false;
        tab.PendingUrl = url;
        UpdateStatusBar();
    }

    // =====================================================================
    //  Sleeping / memory policy
    // =====================================================================

    private async void OnSleepTick(object? sender, EventArgs e)
    {
        if (_shuttingDown) return;

        var suspendAfter = _settings.SuspendAfterMinutes;
        var discardAfter = _settings.DiscardAfterMinutes;
        var now = DateTime.UtcNow;

        foreach (var tab in _tabs.ToArray())
        {
            if (ReferenceEquals(tab, _active)) continue;

            var core = tab.View?.CoreWebView2;
            if (core is null) continue;

            var idleMinutes = (now - tab.LastActiveUtc).TotalMinutes;

            if (discardAfter > 0 && idleMinutes >= discardAfter)
            {
                DiscardTab(tab);
                continue;
            }

            if (suspendAfter > 0 && idleMinutes >= suspendAfter && !tab.IsSuspended)
            {
                try
                {
                    if (await core.TrySuspendAsync()) tab.IsSuspended = true;
                }
                catch (Exception ex)
                {
                    Log.Write("Suspend: " + ex.Message);
                }
            }
        }

        EnforceLiveTabLimit();
        UpdateStatusBar();
    }

    private void EnforceLiveTabLimit()
    {
        var limit = Math.Max(3, _settings.MaxLiveTabs);

        var live = _tabs
            .Where(t => t.View is not null && !ReferenceEquals(t, _active))
            .OrderBy(t => t.LastActiveUtc)
            .ToList();

        while (live.Count >= limit)
        {
            var victim = live[0];
            live.RemoveAt(0);
            DiscardTab(victim);
        }
    }

    private async void OnMemoryTick(object? sender, EventArgs e)
    {
        if (_shuttingDown) return;

        try
        {
            _lastMemory = await Task.Run(() => MemoryProbe.Measure(Environment.ProcessId));
            RenderMemory();
            BroadcastStats();
        }
        catch (Exception ex)
        {
            Log.Write("Memory probe: " + ex.Message);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

        // While minimised nobody sees the pages: let Chromium shed memory.
        if (_env is null) return;

        if (WindowState == WindowState.Minimized)
        {
            foreach (var tab in _tabs)
            {
                if (tab.View?.CoreWebView2 is { } core)
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            }
        }
        else if (_active?.View?.CoreWebView2 is { } activeCore)
        {
            activeCore.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }
    }

    // =====================================================================
    //  Chrome (toolbar / status) rendering
    // =====================================================================

    private void UpdateChrome()
    {
        var tab = _active;

        BackButton.IsEnabled = tab?.CanGoBack == true;
        ForwardButton.IsEnabled = tab?.CanGoForward == true;

        if (tab is null)
        {
            ReloadButton.Content = IconReload;
            BookmarkButton.Content = IconStarOutline;
            ShieldButton.Foreground = (Brush)FindResource("TextSecondary");
            return;
        }

        ReloadButton.Content = tab.IsLoading ? IconStop : IconReload;
        ReloadButton.ToolTip = tab.IsLoading ? "Oprește încărcarea" : "Reîncarcă (F5)";

        var isBookmarked = _bookmarks.Contains(tab.Url);
        BookmarkButton.Content = isBookmarked ? IconStarFilled : IconStarOutline;
        BookmarkButton.Foreground = (Brush)FindResource(isBookmarked ? "Accent" : "TextPrimary");

        ShieldButton.Foreground = tab.ShieldEnabled
            ? (Brush)FindResource("Accent")
            : (Brush)FindResource("TextSecondary");

        if (tab.IsInternalPage)
        {
            SecurityGlyph.Text = "\uE721"; // search glyph, like Brave's new tab
            SecurityGlyph.Foreground = (Brush)FindResource("TextMuted");
            SecurityGlyph.ToolTip = "Pagină internă Vela";
        }
        else if (tab.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            SecurityGlyph.Text = IconLock;
            SecurityGlyph.Foreground = (Brush)FindResource("TextMuted");
            SecurityGlyph.ToolTip = "Conexiune criptată";
        }
        else
        {
            SecurityGlyph.Text = IconUnlock;
            SecurityGlyph.Foreground = (Brush)FindResource("TextMuted");
            SecurityGlyph.ToolTip = "Fără criptare";
        }

        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        if (_shuttingDown) return;

        if (_transientStatus is not null)
        {
            StatusText.Text = _transientStatus;
        }
        else
        {
            var parts = new List<string> { $"{_tabs.Count} tab{(_tabs.Count == 1 ? "" : "uri")}" };

            var sleeping = _tabs.Count(t => t.IsSuspended);
            if (sleeping > 0) parts.Add($"{sleeping} în repaus");

            var discarded = _tabs.Count(t => t.IsDiscarded);
            if (discarded > 0) parts.Add($"{discarded} eliberate din memorie");

            if (_blocker.BlockedTotal > 0) parts.Add($"{_blocker.BlockedTotal} cereri blocate");

            StatusText.Text = string.Join("  ·  ", parts);
        }

        RenderMemory();
    }

    private void RenderMemory()
    {
        MemoryText.Text = _lastMemory.WorkingSetBytes > 0
            ? $"RAM {_lastMemory.Megabytes:0} MB  ·  {_lastMemory.ProcessCount} {(_lastMemory.ProcessCount == 1 ? "proces" : "procese")}"
            : "";
    }

    private void Status(string message, int seconds = 6)
    {
        _transientStatus = message;
        StatusText.Text = message;
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(seconds);
        _statusTimer.Start();
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _transientStatus = null;
        UpdateStatusBar();
    }

    private void SetOmnibox(string url)
    {
        // A fresh tab looks empty, the way Brave does it: no vela.internal URL in the omnibox.
        var display = IsInternalUrl(url) ? "" : FormatForDisplay(url);
        Omnibox.Text = display;
        OmniboxPlaceholder.Visibility = display.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsInternalUrl(string? url) =>
        !string.IsNullOrEmpty(url) &&
        url.StartsWith("https://" + AppConstants.InternalHost + "/", StringComparison.OrdinalIgnoreCase);

    private static string FormatForDisplay(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url[8..];
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return url[7..];
        return url;
    }

    private void BroadcastStats()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "stats",
            ramMB = (int)Math.Round(_lastMemory.Megabytes),
            processes = _lastMemory.ProcessCount,
            tabs = _tabs.Count,
            sleeping = _tabs.Count(t => t.IsSuspended),
            blocked = _blocker.BlockedTotal,
        });

        foreach (var tab in _tabs)
        {
            if (!tab.IsInternalPage) continue;
            try { tab.View?.CoreWebView2?.PostWebMessageAsJson(json); }
            catch { /* page may be gone */ }
        }
    }

    // =====================================================================
    //  Navigation
    // =====================================================================

    private async Task NavigateActiveAsync(string url)
    {
        url = ResolveUrl(url);

        if (_active is null)
        {
            await AddTabAsync(url);
            return;
        }

        var tab = _active;

        // A discarded tab has no view: let EnsureViewAsync load the new URL directly.
        if (tab.View is null)
        {
            tab.PendingUrl = url;
            var created = await EnsureViewAsync(tab);
            if (created is not null) created.Visibility = Visibility.Visible;
            return;
        }

        var core = tab.View.CoreWebView2;
        if (core is null) return;

        tab.IsSuspended = false;
        tab.View.Visibility = Visibility.Visible;
        core.Navigate(url);
    }

    private void NavigateFromOmnibox()
    {
        var url = UrlHelper.ToNavigationUrl(Omnibox.Text, _settings.SearchUrl);
        Keyboard.ClearFocus();
        _ = NavigateActiveAsync(url);
    }

    private void Back()
    {
        if (_active?.View?.CoreWebView2 is { CanGoBack: true } core) core.GoBack();
    }

    private void Forward()
    {
        if (_active?.View?.CoreWebView2 is { CanGoForward: true } core) core.GoForward();
    }

    private void ReloadActive()
    {
        var tab = _active;
        if (tab?.View?.CoreWebView2 is not { } core) return;

        if (IsNewTabUrl(tab.Url))
        {
            _ = NavigateActiveAsync(BuildNewTabUrl());
            return;
        }

        if (tab.IsLoading) core.Stop();
        else core.Reload();
    }

    private void ToggleBookmark()
    {
        var tab = _active;
        if (tab is null) return;

        if (tab.IsInternalPage)
        {
            Status("Paginile interne nu pot fi adăugate la favorite.");
            return;
        }

        var added = _bookmarks.Toggle(tab.Url, tab.Title);
        SetOmnibox(tab.Url);
        UpdateChrome();
        Status(added ? "Adăugat la favorite." : "Eliminat din favorite.");
    }

    private void UpdateShieldPopup(BrowserTab tab)
    {
        ShieldHostText.Text = UrlHelper.HostOf(tab.Url);
        ShieldToggle.IsChecked = tab.ShieldEnabled;
        ShieldBlockedText.Text = $"{tab.BlockedCount} cereri blocate";

        ShieldSavedText.Text = tab.IsInternalPage
            ? "Pagină internă Vela — fără trafic extern."
            : _settings.BlockAds
                ? "Reclamele sunt blocate în rețea și ascunse în pagină, inclusiv în playerul YouTube."
                : "Blocarea globală este oprită din meniu.";
    }

    private void FocusOmnibox()
    {
        Omnibox.Focus();
        Omnibox.SelectAll();
    }

    // =====================================================================
    //  WebView2 events
    // =====================================================================

    private void OnWebMessage(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only our own internal pages are allowed to drive the browser.
        if (string.IsNullOrEmpty(e.Source) ||
            !e.Source.StartsWith("https://" + AppConstants.InternalHost + "/", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            // The new tab page posts JSON objects, so read the JSON form: TryGetWebMessageAsString
            // throws for object messages (it only accepts strings).
            var raw = e.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(raw)) return;

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

            switch (type)
            {
                case "search":
                {
                    var query = root.TryGetProperty("q", out var q) ? q.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(query))
                        _ = NavigateActiveAsync(UrlHelper.ToNavigationUrl(query, _settings.SearchUrl));
                    break;
                }
                case "navigate":
                {
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url)) _ = NavigateActiveAsync(url);
                    break;
                }
                case "newtab":
                    _ = AddTabAsync(AppConstants.NewTabUrl);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("WebMessage: " + ex.Message);
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);

            var fileName = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "descarcare";

            var target = Path.Combine(downloads, fileName);
            var counter = 1;
            while (File.Exists(target))
            {
                var name = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                target = Path.Combine(downloads, $"{name} ({counter++}){ext}");
            }

            e.ResultFilePath = target;
            e.Handled = true;

            Status($"Se descarcă: {Path.GetFileName(target)}");

            var operation = e.DownloadOperation;
            operation.StateChanged += (_, _) =>
            {
                switch (operation.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        Dispatcher.InvokeAsync(() => Status($"Descărcat: {Path.GetFileName(target)}", 8));
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        Dispatcher.InvokeAsync(() => Status($"Descărcarea a eșuat: {Path.GetFileName(target)}", 8));
                        break;
                }
            };
        }
        catch (Exception ex)
        {
            Log.Write("Download: " + ex.Message);
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        try
        {
            // Notifications are noisy and cheap to refuse; everything else asks the user.
            if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
            {
                e.State = CoreWebView2PermissionState.Deny;
                return;
            }

            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
            {
                e.State = CoreWebView2PermissionState.Allow;
                return;
            }

            var host = UrlHelper.HostOf(e.Uri);
            var answer = MessageBox.Show(this,
                $"{host} cere permisiunea: {Describe(e.PermissionKind)}.\n\nPermiți?",
                "Vela", MessageBoxButton.YesNo, MessageBoxImage.Question);

            e.State = answer == MessageBoxResult.Yes
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        }
        catch (Exception ex)
        {
            Log.Write("Permission: " + ex.Message);
            e.State = CoreWebView2PermissionState.Deny;
        }
    }

    private static string Describe(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Camera => "camera video",
        CoreWebView2PermissionKind.Microphone => "microfon",
        CoreWebView2PermissionKind.Geolocation => "locația",
        CoreWebView2PermissionKind.ClipboardRead => "clipboard",
        CoreWebView2PermissionKind.Notifications => "notificări",
        CoreWebView2PermissionKind.OtherSensors => "senzori",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "descărcări multiple",
        _ => kind.ToString(),
    };

    private void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Write($"Process failed ({e.ProcessFailedKind}) on {tab.Url}");

        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            Status("Procesul browserului s-a oprit. Repornește Vela.");
            return;
        }

        var wasActive = ReferenceEquals(tab, _active);

        Dispatcher.InvokeAsync(async () =>
        {
            DiscardTab(tab, force: wasActive);
            if (wasActive)
            {
                Status("Pagina a fost reîncărcată după o eroare.");
                await ActivateTabAsync(tab);
            }
        });
    }

    // =====================================================================
    //  Keyboard
    // =====================================================================

    /// <summary>
    /// Keyboard shortcuts. WebView2 forwards accelerators it does not consume (they are
    /// disabled in its settings) up to the WPF window, so page focus still reaches here.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (HandleShortcut(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers))
            e.Handled = true;
    }

    private bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var alt = modifiers.HasFlag(ModifierKeys.Alt);

        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.T when shift:
                    ReopenClosedTab();
                    return true;
                case Key.T:
                    _ = AddTabAsync(AppConstants.NewTabUrl);
                    return true;
                case Key.W:
                    CloseActiveTab();
                    return true;
                case Key.N:
                    OpenNewWindow();
                    return true;
                case Key.L:
                    FocusOmnibox();
                    return true;
                case Key.D:
                    ToggleBookmark();
                    return true;
                case Key.R:
                    ReloadActive();
                    return true;
                case Key.Tab:
                    CycleTab(shift ? -1 : 1);
                    return true;
                case Key.PageDown:
                    CycleTab(1);
                    return true;
                case Key.PageUp:
                    CycleTab(-1);
                    return true;
                case Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6 or Key.D7 or Key.D8:
                    SelectTabByIndex(key - Key.D1);
                    return true;
                case Key.D9:
                    SelectTabByIndex(_tabs.Count - 1);
                    return true;
                case Key.OemPlus or Key.Add:
                    ZoomActive(+0.1);
                    return true;
                case Key.OemMinus or Key.Subtract:
                    ZoomActive(-0.1);
                    return true;
                case Key.D0 or Key.NumPad0:
                    ZoomActive(0, reset: true);
                    return true;
                case Key.OemComma:
                    OpenSettings();
                    return true;
            }
        }

        if (alt)
        {
            switch (key)
            {
                case Key.Left:
                    Back();
                    return true;
                case Key.Right:
                    Forward();
                    return true;
                case Key.D:
                    FocusOmnibox();
                    return true;
            }
        }

        switch (key)
        {
            case Key.F5:
                ReloadActive();
                return true;
            case Key.F11:
                ToggleFullscreen();
                return true;
            case Key.F12:
                ToggleDevTools();
                return true;
            case Key.Escape when _isFullscreen:
                ToggleFullscreen();
                return true;
        }

        return false;
    }

    private void CycleTab(int direction)
    {
        if (_tabs.Count < 2 || _active is null) return;

        var index = _tabs.IndexOf(_active);
        var next = (index + direction + _tabs.Count) % _tabs.Count;
        SelectTab(_tabs[next]);
    }

    private void SelectTabByIndex(int index)
    {
        if (_tabs.Count == 0) return;
        SelectTab(_tabs[Math.Clamp(index, 0, _tabs.Count - 1)]);
    }

    private void ZoomActive(double delta, bool reset = false)
    {
        var view = _active?.View;
        if (view is null) return;

        var zoom = reset ? 1.0 : Math.Clamp(view.ZoomFactor + delta, 0.25, 5.0);
        view.ZoomFactor = zoom;
        Status($"Zoom: {zoom * 100:0}%", 3);
    }

    private void ToggleDevTools()
    {
        try { _active?.View?.CoreWebView2?.OpenDevToolsWindow(); }
        catch (Exception ex) { Log.Write("DevTools: " + ex.Message); }
    }

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;

        if (_isFullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.Normal;

            RootGrid.RowDefinitions[0].Height = new GridLength(0);
            RootGrid.RowDefinitions[1].Height = new GridLength(0);
            RootGrid.RowDefinitions[3].Height = new GridLength(0);

            WindowState = WindowState.Maximized;
            Status("F11 pentru a ieși din ecran complet.", 4);
        }
        else
        {
            RootGrid.RowDefinitions[0].Height = new GridLength(38);
            RootGrid.RowDefinitions[1].Height = GridLength.Auto;
            RootGrid.RowDefinitions[3].Height = new GridLength(24);
            WindowState = _stateBeforeFullscreen;
        }
    }

    // =====================================================================
    //  Window chrome handlers
    // =====================================================================

    private void TabStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (IsInteractiveSource(e.OriginalSource)) return;

        try { DragMove(); }
        catch { /* not dragging */ }
    }

    private static bool IsInteractiveSource(object? source)
    {
        var current = source as DependencyObject;

        while (current is not null)
        {
            if (current is ButtonBase or TextBoxBase or ScrollBar or Thumb) return true;
            if (current is Grid) return false;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
        {
            SelectTab(tab);
            e.Handled = true;
        }
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e) => _ = AddTabAsync(AppConstants.NewTabUrl);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Back_Click(object sender, RoutedEventArgs e) => Back();

    private void Forward_Click(object sender, RoutedEventArgs e) => Forward();

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadActive();

    private void Home_Click(object sender, RoutedEventArgs e) => _ = NavigateActiveAsync(_settings.HomePage);

    private void Omnibox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                NavigateFromOmnibox();
                break;
            case Key.Escape:
                e.Handled = true;
                SetOmnibox(_active?.Url ?? "");
                Keyboard.ClearFocus();
                _active?.View?.Focus();
                break;
        }
    }

    private void Omnibox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        OmniboxBorder.BorderBrush = (Brush)FindResource("BorderStrong");
        Omnibox.SelectAll();
    }

    private void Omnibox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        OmniboxBorder.BorderBrush = Brushes.Transparent;
        SetOmnibox(_active?.Url ?? Omnibox.Text);
    }

    private void Omnibox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OmniboxPlaceholder.Visibility = Omnibox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e) => ToggleBookmark();

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        UpdateShieldPopup(_active);
        ShieldPopup.IsOpen = true;
    }

    private async void ShieldToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;

        var enabled = ShieldToggle.IsChecked == true;
        _active.ShieldEnabled = enabled;

        var core = _active.View?.CoreWebView2;
        if (core is not null)
        {
            // Cosmetic filters live in a script registered per document, so they have to be
            // added/removed as well when the shield changes. Reload to apply immediately.
            if (enabled) await AddInjectionAsync(_active, core);
            else await RemoveInjectionAsync(_active, core);

            try { core.Reload(); } catch { }
        }

        UpdateChrome();
        UpdateShieldPopup(_active);

        Status(enabled ? "Scutul este activ pentru acest site." : "Scutul este oprit pentru acest site.", 4);
    }

    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        try { Process.Start(new ProcessStartInfo("explorer.exe", downloads) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Open downloads: " + ex.Message); }
    }

    // =====================================================================
    //  Settings, themes and new tab background
    // =====================================================================

    private static bool IsNewTabUrl(string? url) =>
        !string.IsNullOrEmpty(url) &&
        url.StartsWith(AppConstants.NewTabUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns the stored new tab address into the themed address actually loaded.</summary>
    private string ResolveUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return BuildNewTabUrl();
        return IsNewTabUrl(url) ? BuildNewTabUrl() : url;
    }

    /// <summary>
    /// The new tab page is themed through query parameters so the first paint is already
    /// correct. Live updates use <c>window.velaApplyTheme</c> instead of reloading the page.
    /// </summary>
    private string BuildNewTabUrl()
    {
        var theme = ThemeCatalog.Resolve(_settings);

        var url = new StringBuilder(AppConstants.NewTabUrl);
        url.Append("?accent=").Append(Uri.EscapeDataString(theme.Accent));
        url.Append("&surface=").Append(Uri.EscapeDataString(theme.NewTabSurface));
        url.Append("&surface2=").Append(Uri.EscapeDataString(theme.NewTabSurface2));
        url.Append("&border=").Append(Uri.EscapeDataString(theme.BorderSoft));
        url.Append("&text=").Append(Uri.EscapeDataString(theme.TextPrimary));
        url.Append("&muted=").Append(Uri.EscapeDataString(theme.TextSecondary));
        url.Append("&dim=").Append(Uri.EscapeDataString(theme.TextMuted));
        url.Append("&bg=").Append(Uri.EscapeDataString(ThemeCatalog.ResolveNewTabBackground(theme, _settings)));

        if (HasBackgroundImage(_settings))
            url.Append("&img=").Append(Uri.EscapeDataString(BackgroundImageUrl(_settings)));

        return url.ToString();
    }

    private static bool HasBackgroundImage(Settings settings) =>
        settings.NewTabBackgroundMode == "image" &&
        !string.IsNullOrWhiteSpace(settings.NewTabBackgroundImage);

    private static string BackgroundImageUrl(Settings settings) =>
        "https://" + AppConstants.BackgroundHost + "/" + settings.NewTabBackgroundImage;

    private static string ThemeConfigJson(Settings settings)
    {
        var theme = ThemeCatalog.Resolve(settings);

        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["accent"] = theme.Accent,
            ["surface"] = theme.NewTabSurface,
            ["surface2"] = theme.NewTabSurface2,
            ["border"] = theme.BorderSoft,
            ["text"] = theme.TextPrimary,
            ["muted"] = theme.TextSecondary,
            ["dim"] = theme.TextMuted,
            ["bg"] = ThemeCatalog.ResolveNewTabBackground(theme, settings),
            ["img"] = HasBackgroundImage(settings) ? BackgroundImageUrl(settings) : "",
        });
    }

    private async Task ApplyNewTabThemeAsync(Settings settings)
    {
        var script = "window.velaApplyTheme && window.velaApplyTheme(" + ThemeConfigJson(settings) + ");";

        foreach (var tab in _tabs.ToList())
        {
            var core = tab.View?.CoreWebView2;
            if (core is null || !IsNewTabUrl(tab.Url)) continue;

            try { await core.ExecuteScriptAsync(script); }
            catch (Exception ex) { Log.Write("New tab theme: " + ex.Message); }
        }
    }

    /// <summary>Called by the settings window on every change, before the user saves.</summary>
    private void PreviewSettings(Settings draft)
    {
        ThemeCatalog.Apply(draft);
        _ = ApplyNewTabThemeAsync(draft);
    }

    /// <summary>Called by the settings window when the user presses Salvează.</summary>
    private void CommitSettings(Settings draft)
    {
        var adBlockChanged = _appliedBlockAds != draft.BlockAds;

        _settings = draft;
        SettingsStore.Save(_settings);

        ThemeCatalog.Apply(_settings);
        _ = ApplyNewTabThemeAsync(_settings);
        _ = ApplyAdBlockSettingAsync(adBlockChanged);

        PopulateMenu();
        UpdateStatusBar();
        Status("Setările au fost salvate.", 4);
    }

    private async Task ApplyAdBlockSettingAsync(bool reload)
    {
        _blocker.Enabled = _settings.BlockAds;
        var previous = _appliedBlockAds;
        _appliedBlockAds = _settings.BlockAds;

        if (previous == _settings.BlockAds) return;

        foreach (var tab in _tabs.ToList())
        {
            var core = tab.View?.CoreWebView2;
            if (core is null) continue;

            if (_settings.BlockAds && tab.ShieldEnabled) await AddInjectionAsync(tab, core);
            else await RemoveInjectionAsync(tab, core);

            if (!reload || IsNewTabUrl(tab.Url)) continue;

            try { core.Reload(); } catch { /* tab may be closing */ }
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings, PreviewSettings, CommitSettings)
        {
            Owner = this,
        };

        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;
        window.ShowDialog();
    }

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenSettings();
    }

    private static System.Drawing.Color ToDrawingColor(string hex)
    {
        var color = ThemeCatalog.Parse(hex);
        return System.Drawing.Color.FromArgb(255, color.R, color.G, color.B);
    }

    // =====================================================================
    //  Menu
    // =====================================================================

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        PopulateMenu();
        MenuPopup.IsOpen = true;
    }

    private void PopulateMenu()
    {
        var marks = _bookmarks.All.Take(12).ToList();
        BookmarksList.ItemsSource = marks;
        NoBookmarksText.Visibility = marks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        HistoryList.ItemsSource = _history.Recent(8);

        SleepPolicyText.Text = _settings.SuspendAfterMinutes <= 0
            ? "Adormirea tab-urilor este oprită: Vela ține toate paginile încărcate."
            : $"Tab-urile din fundal se îngheață după {_settings.SuspendAfterMinutes} min și eliberează memoria după {_settings.DiscardAfterMinutes} min.";
    }

    private void MenuNewTab_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        _ = AddTabAsync(AppConstants.NewTabUrl);
    }

    private void MenuNewWindow_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenNewWindow();
    }

    private void OpenNewWindow()
    {
        // Same CoreWebView2Environment: one browser process, no second profile on disk.
        var window = new MainWindow(_env) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.Show();
    }

    private void BookmarkItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            MenuPopup.IsOpen = false;
            _ = NavigateActiveAsync(url);
        }
    }

    private void HistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            MenuPopup.IsOpen = false;
            _ = NavigateActiveAsync(url);
        }
    }

    private void SleepPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string raw } || !int.TryParse(raw, out var minutes)) return;

        _settings.SuspendAfterMinutes = minutes;
        SettingsStore.Save(_settings);

        if (minutes <= 0) ResumeAllSuspended();
        MenuPopup.IsOpen = false;
        PopulateMenu();
        UpdateStatusBar();

        Status(minutes <= 0 ? "Adormirea tab-urilor: oprită." : $"Tab-urile din fundal adorm după {minutes} min.", 5);
    }

    private void ResumeAllSuspended()
    {
        foreach (var tab in _tabs)
        {
            if (!tab.IsSuspended) continue;
            try { tab.View?.CoreWebView2?.Resume(); }
            catch { /* ignore */ }
            tab.IsSuspended = false;
        }
    }

    private void MenuClearData_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;

        var answer = MessageBox.Show(this,
            "Ștergi tot istoricul de navigare?\n\nFavoritele și sesiunea rămân intacte.",
            "Vela", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _history.Clear();
        PopulateMenu();
        Status("Istoricul a fost șters.");
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;

        MessageBox.Show(this,
            $"Vela {AppConstants.Version}\n\n" +
            $"Motor: WebView2 (Chromium) — aceeași versiune cu Edge instalat pe sistem.\n" +
            $"Reguli de blocare: {_blocker.RuleCount}\n" +
            $"Cereri blocate de la pornire: {_blocker.BlockedTotal}\n" +
            $"Profil: {AppPaths.Profile}\n\n" +
            "Construit pentru consum redus: tab-urile din fundal sunt înghețate și eliberate din memorie.",
            "Despre Vela", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
