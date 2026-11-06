using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CrimsonBrowser.Models;
using CrimsonBrowser.Services;
using CefSharp;
using DownloadItem = CrimsonBrowser.Services.DownloadItem;

namespace CrimsonBrowser;

public partial class MainWindow : Window
{
    private IRequestContext? _webViewEnvironment;
    private readonly Queue<string> _pendingExternalUrls = new();
    private bool _browserInitialized;
    public ObservableCollection<BrowserTab> Tabs { get; } = new();
    private BrowserTab? _activeTab;
    private bool _isOmniboxFocused;

    private readonly bool _restoreSession;
    private bool _isFullscreen;
    private WindowState _preFullscreenState = WindowState.Normal;
    private Rect _preFullscreenBounds = Rect.Empty;
    private bool _bookmarksBarWasVisible;
    private readonly PermissionBroker _permissionBroker;

    // Stack for Ctrl+Shift+T — reopen last closed tab
    private readonly Stack<(string url, string title)> _closedTabStack = new();

    // Tab drag-to-reorder state.
    private Point _tabDragStart;
    private BrowserTab? _tabDragSource;
    private bool _isTabDragging;
    private readonly List<(FrameworkElement Container, TranslateTransform Transform, double Left)> _tabDragSlots = new();
    private int _tabDragFrom;
    private int _tabDragTo;

    // ── Memory saver ──
    private readonly DispatcherTimer _memorySaverTimer;
    private static readonly TimeSpan TabDiscardAfter = TimeSpan.FromMinutes(10);

    // ── Commands ──
    public ICommand NewTabCommand            { get; }
    public ICommand CloseCurrentTabCommand   { get; }
    public ICommand RefreshCommand           { get; }
    public ICommand FocusOmniboxCommand      { get; }
    public ICommand OpenDevToolsCommand      { get; }
    public ICommand OpenHistoryCommand       { get; }
    public ICommand OpenIncognitoCommand     { get; }
    public ICommand ReopenClosedTabCommand   { get; }
    public ICommand ToggleBookmarksBarCommand { get; }
    public ICommand ToggleBookmarkCommand    { get; }
    public ICommand NewWindowCommand         { get; }
    public ICommand ToggleFullscreenCommand  { get; }
    public ICommand NextTabCommand           { get; }
    public ICommand PrevTabCommand           { get; }
    public ICommand FindCommand              { get; }
    public ICommand ResetZoomCommand         { get; }
    public ICommand ZoomInCommand            { get; }
    public ICommand ZoomOutCommand           { get; }

    public MainWindow(bool restoreSession = false)
    {
        _restoreSession = restoreSession;

        InitializeComponent();

        _permissionBroker = new PermissionBroker(this, persistChoices: true);

        NewTabCommand            = new RelayCommand(_ => AddNewTab());
        CloseCurrentTabCommand   = new RelayCommand(_ => { if (_activeTab != null) CloseTab(_activeTab); });
        RefreshCommand           = new RelayCommand(_ => _activeTab?.Reload());
        FocusOmniboxCommand      = new RelayCommand(_ => FocusOmnibox());
        OpenDevToolsCommand      = new RelayCommand(_ => _activeTab?.ShowDevTools());
        OpenHistoryCommand       = new RelayCommand(_ => OpenHistory());
        OpenIncognitoCommand     = new RelayCommand(_ => OpenIncognito());
        ReopenClosedTabCommand   = new RelayCommand(_ => ReopenLastTab());
        ToggleBookmarksBarCommand = new RelayCommand(_ => ToggleBookmarksBar());
        ToggleBookmarkCommand    = new RelayCommand(_ => ToggleCurrentBookmark());
        NewWindowCommand         = new RelayCommand(_ => { var w = new MainWindow(); w.Show(); });
        ToggleFullscreenCommand  = new RelayCommand(_ => ToggleFullscreen());
        NextTabCommand           = new RelayCommand(_ => SelectNextTab(1));
        PrevTabCommand           = new RelayCommand(_ => SelectNextTab(-1));
        FindCommand              = new RelayCommand(_ => OpenFind());
        ResetZoomCommand         = new RelayCommand(_ => SetZoom(1.0));
        ZoomInCommand            = new RelayCommand(_ => SetZoom((_activeTab?.WebView.ZoomFactor ?? 1.0) + 0.1));
        ZoomOutCommand           = new RelayCommand(_ => SetZoom((_activeTab?.WebView.ZoomFactor ?? 1.0) - 0.1));

        TabsItemsControl.ItemsSource        = Tabs;
        BookmarksBarItemsControl.ItemsSource = BookmarksService.Instance.Bookmarks;
        DownloadsList.ItemsSource            = DownloadsService.Instance.Downloads;

        // Listen for downloads changes to update badge
        DownloadsService.Instance.ActiveDownloadsChanged += UpdateDownloadsBadge;
        DownloadsService.Instance.Downloads.CollectionChanged += (_, __) => UpdateDownloadsBadge();

        // Listen for bookmark changes to update star icon
        BookmarksService.Instance.BookmarksChanged += () => Dispatcher.Invoke(UpdateBookmarkStar);

        InitializeShieldsUi();
        ApplyLocalizationToAllUi();

        _memorySaverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _memorySaverTimer.Tick += (_, _) => ApplyMemorySaverPolicy();

        Loaded       += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closing      += MainWindow_Closing;
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeBrowserEnvironment();
        _memorySaverTimer.Start();
        ApplyMemorySaverPolicy();
        _ = CheckForUpdatesAutomaticallyAsync();
    }

    private void InitializeBrowserEnvironment()
    {
        try
        {
            _webViewEnvironment = Cef.GetGlobalRequestContext();

            // A URL passed on the command line (or forwarded from a second launch) wins over
            // the previous session. Secondary windows never restore or save the session.
            string? startupUrl = _restoreSession ? App.TakePendingStartupUrl() : null;

            SessionState? session = _restoreSession && startupUrl is null
                ? SessionService.Load()
                : null;

            if (session is not null)
                ApplySessionBounds(session);

            if (!string.IsNullOrWhiteSpace(startupUrl))
            {
                AddNewTab(startupUrl);
            }
            else if (session is not null && session.Urls.Count > 0)
            {
                foreach (string url in session.Urls)
                    AddNewTab(url);

                int activeIndex = Math.Clamp(session.ActiveIndex, 0, Tabs.Count - 1);
                SetActiveTab(Tabs[activeIndex]);
            }
            else
            {
                AddNewTab();
            }

            _browserInitialized = true;
            while (_pendingExternalUrls.TryDequeue(out string? externalUrl))
                AddNewTab(externalUrl);
        }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            MessageBox.Show(
                loc.Format("EngineErrorMessage", ex.Message),
                loc.Get("EngineErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TAB MANAGEMENT
    // ─────────────────────────────────────────────────────────────────────────

    public void AddNewTab(string? initialUrl = null)
    {
        if (_webViewEnvironment == null) return;

        bool isBlankNewTab = string.IsNullOrWhiteSpace(initialUrl);
        var tab = new BrowserTab(_webViewEnvironment)
        {
            FocusOmniboxOnStartPage = isBlankNewTab
        };

        tab.StartPageReady += readyTab => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (readyTab == _activeTab) FocusOmnibox();
        }), DispatcherPriority.Input);

        tab.RequestNewTab += (senderTab, targetUrl) =>
            Dispatcher.Invoke(() => AddNewTab(targetUrl));

        tab.RequestClose += (senderTab) =>
            Dispatcher.Invoke(() => CloseTab(senderTab));

        tab.RequestOpenSettings += () =>
            Dispatcher.Invoke(OpenSettings);

        tab.RequestShortcut += (senderTab, action) =>
            Dispatcher.Invoke(() => HandleShortcut(senderTab, action));

        tab.FullscreenChanged += isFullscreen =>
            Dispatcher.Invoke(() => SetFullscreen(isFullscreen));

        tab.PermissionRequested += (senderTab, args) =>
            _permissionBroker.Handle(senderTab, args);

        tab.InitializationFailed += (failedTab, message) => Dispatcher.Invoke(() =>
        {
            if (!Tabs.Contains(failedTab)) return;
            CrimsonDialog.Info(
                this,
                LocalizationService.Instance.Get("EngineErrorTitle"),
                LocalizationService.Instance.Format("EngineErrorMessage", message));
        });

        tab.PropertyChanged += Tab_PropertyChanged;

        Tabs.Add(tab);
        WebContainer.Children.Add(tab.WebView);
        SetActiveTab(tab);

        if (!string.IsNullOrWhiteSpace(initialUrl))
            tab.Navigate(initialUrl);
        else
            FocusOmnibox();
    }

    /// <summary>Focus the address bar so a new tab can be typed into immediately.</summary>
    private void FocusOmnibox()
    {
        // The web engine often claims focus shortly after a document finishes navigating.
        // Try immediately, at idle, and once more shortly afterwards so the omnibox wins.
        Dispatcher.BeginInvoke(new Action(() => ApplyOmniboxFocus(selectAll: true)),
            DispatcherPriority.Input);
        Dispatcher.BeginInvoke(new Action(() => ApplyOmniboxFocus(selectAll: false)),
            DispatcherPriority.ApplicationIdle);

        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            ApplyOmniboxFocus(selectAll: false);
        };
        retry.Start();
    }

    private void ApplyOmniboxFocus(bool selectAll)
    {
        if (!IsVisible) return;

        if (!OmniboxTextBox.IsKeyboardFocusWithin)
            OmniboxTextBox.Focus();

        Keyboard.Focus(OmniboxTextBox);

        if (selectAll)
            OmniboxTextBox.SelectAll();
    }

    public void SetActiveTab(BrowserTab tab)
    {
        if (_activeTab != null) _activeTab.IsActive = false;

        if (FindBar.Visibility == Visibility.Visible)
            CloseFind();

        _activeTab = tab;
        _activeTab.IsActive = true;
        _activeTab.SetWindowMinimized(WindowState == WindowState.Minimized);
        UpdateUiForActiveTab();
    }

    public void CloseTab(BrowserTab tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0) return;

        // Push to closed-tab stack before removing (unless it was the start page)
        if (!tab.IsStartPage)
            _closedTabStack.Push((tab.Url, tab.Title));

        if (Tabs.Count == 1)
        {
            AddNewTab();
        }

        bool wasActive = tab.IsActive;
        tab.PropertyChanged -= Tab_PropertyChanged;
        WebContainer.Children.Remove(tab.WebView);
        Tabs.Remove(tab);
        tab.Dispose();

        if (wasActive && Tabs.Count > 0)
            SetActiveTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    public void ReopenLastTab()
    {
        if (_closedTabStack.Count == 0) return;
        var (url, _) = _closedTabStack.Pop();
        AddNewTab(url);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SESSION RESTORE / SAVE
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplySessionBounds(SessionState session)
    {
        if (session.Width > 0 && session.Height > 0)
        {
            Width  = Math.Max(MinWidth, session.Width);
            Height = Math.Max(MinHeight, session.Height);
        }

        if (!double.IsNaN(session.Left) && !double.IsNaN(session.Top))
        {
            double minLeft = SystemParameters.VirtualScreenLeft;
            double minTop  = SystemParameters.VirtualScreenTop;
            double maxLeft = minLeft + SystemParameters.VirtualScreenWidth  - Width;
            double maxTop  = minTop  + SystemParameters.VirtualScreenHeight - Height;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(session.Left, minLeft, Math.Max(minLeft, maxLeft));
            Top  = Math.Clamp(session.Top,  minTop,  Math.Max(minTop,  maxTop));
        }

        if (session.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _memorySaverTimer.Stop();
        SaveSession();
    }

    public void DisposeBrowsers()
    {
        _memorySaverTimer.Stop();
        foreach (var tab in Tabs.ToArray())
        {
            tab.PropertyChanged -= Tab_PropertyChanged;
            tab.Dispose();
        }
        WebContainer.Children.Clear();
        Tabs.Clear();
        _webViewEnvironment?.Dispose();
        _webViewEnvironment = null;
        DownloadsService.Instance.ActiveDownloadsChanged -= UpdateDownloadsBadge;
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeBrowsers();
        base.OnClosed(e);
    }

    private void SaveSession()
    {
        // Only the primary window owns the shared session file.
        if (Application.Current?.MainWindow != this) return;

        var urls = Tabs.Where(t => !t.IsStartPage && !string.IsNullOrWhiteSpace(t.Url))
                       .Select(t => t.Url)
                       .ToList();

        int activeIndex = 0;
        if (_activeTab is not null && !_activeTab.IsStartPage)
        {
            int index = urls.IndexOf(_activeTab.Url);
            if (index >= 0) activeIndex = index;
        }

        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        SessionService.Save(new SessionState
        {
            Urls        = urls,
            ActiveIndex = activeIndex,
            Left        = bounds.Left,
            Top         = bounds.Top,
            Width       = bounds.Width,
            Height      = bounds.Height,
            Maximized   = WindowState == WindowState.Maximized
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SHORTCUTS (forwarded from the page because the web engine consumes key input)
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleShortcut(BrowserTab tab, string action)
    {
        switch (action)
        {
            case "fullscreen": ToggleFullscreen(); break;
            case "escape":
                if (FindBar.Visibility == Visibility.Visible) CloseFind();
                else if (_isFullscreen) SetFullscreen(false);
                break;
            case "reload":
                if (tab.IsLoading) tab.Stop();
                else tab.Reload();
                break;
            case "new-tab":       AddNewTab(); break;
            case "new-window":    new MainWindow(restoreSession: false).Show(); break;
            case "close-tab":     CloseTab(tab); break;
            case "reopen-tab":    ReopenLastTab(); break;
            case "incognito":     OpenIncognito(); break;
            case "bookmarks-bar": ToggleBookmarksBar(); break;
            case "focus-omnibox":
                FocusOmnibox();
                break;
            case "bookmark": ToggleCurrentBookmark(); break;
            case "history":  OpenHistory(); break;
            case "find":     OpenFind(); break;
            case "zoom-in":  SetZoom(tab.WebView.ZoomFactor + 0.1); break;
            case "zoom-out": SetZoom(tab.WebView.ZoomFactor - 0.1); break;
            case "zoom-reset": SetZoom(1.0); break;
            case "next-tab": SelectNextTab(1); break;
            case "prev-tab": SelectNextTab(-1); break;
            default:
                if (action.StartsWith("tab-", StringComparison.Ordinal) &&
                    int.TryParse(action[4..], out int number))
                {
                    int index = number - 1;
                    if (index >= 0 && index < Tabs.Count) SetActiveTab(Tabs[index]);
                }
                break;
        }
    }

    private void SelectNextTab(int delta)
    {
        if (Tabs.Count <= 1) return;
        int index = _activeTab is null ? 0 : Tabs.IndexOf(_activeTab);
        int next  = (index + delta + Tabs.Count) % Tabs.Count;
        SetActiveTab(Tabs[next]);
    }

    private void SetZoom(double zoom)
    {
        if (_activeTab is null) return;
        double clamped = Math.Clamp(zoom, 0.25, 3.0);
        _activeTab.WebView.ZoomFactor = clamped;
        UpdateMenuZoomDisplay();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FULLSCREEN
    // ─────────────────────────────────────────────────────────────────────────

    private void ToggleFullscreen() => SetFullscreen(!_isFullscreen);

    private void SetFullscreen(bool enable)
    {
        if (enable == _isFullscreen) return;

        if (enable)
        {
            _preFullscreenState = WindowState;
            _preFullscreenBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            _bookmarksBarWasVisible = BookmarksBarBorder.Visibility == Visibility.Visible;

            TopBarRow.Height = new GridLength(0);
            NavBarRow.Height = new GridLength(0);
            BookmarksBarBorder.Visibility = Visibility.Collapsed;

            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;

            _isFullscreen = true;
            FullscreenHelper.Enter(this);
        }
        else
        {
            _isFullscreen = false;
            _activeTab?.ExitDocumentFullscreen();
            FullscreenHelper.Exit(this);

            TopBarRow.Height = new GridLength(44);
            NavBarRow.Height = new GridLength(50);
            if (_bookmarksBarWasVisible)
                BookmarksBarBorder.Visibility = Visibility.Visible;

            if (_preFullscreenBounds != Rect.Empty)
            {
                Left   = _preFullscreenBounds.Left;
                Top    = _preFullscreenBounds.Top;
                Width  = _preFullscreenBounds.Width;
                Height = _preFullscreenBounds.Height;
            }

            if (_preFullscreenState == WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // WPF routes preview keys through the window before CEF. Handle each
        // application gesture here so the native handler cannot execute it twice.
        foreach (InputBinding binding in InputBindings)
        {
            if (binding.Gesture?.Matches(this, e) == true && binding.Command?.CanExecute(binding.CommandParameter) == true)
            {
                binding.Command.Execute(binding.CommandParameter);
                e.Handled = true;
                return;
            }
        }
        if (e.Key != Key.Escape) return;

        if (FindBar.Visibility == Visibility.Visible)
        {
            CloseFind();
            e.Handled = true;
        }
        else if (_isFullscreen)
        {
            SetFullscreen(false);
            e.Handled = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FIND IN PAGE
    // ─────────────────────────────────────────────────────────────────────────

    private void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FindTextBox.Focus();
            FindTextBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindMatchText.Text = "0/0";
        _activeTab?.StopFind();
        _activeTab?.WebView.Focus();
    }

    private async void RunFind(bool backward)
    {
        if (_activeTab is null) return;

        string term = FindTextBox.Text;
        if (string.IsNullOrEmpty(term))
        {
            _activeTab.StopFind();
            FindMatchText.Text = "0/0";
            return;
        }

        var (index, count) = await _activeTab.FindAsync(term, backward);
        FindMatchText.Text = count > 0 && index > 0 ? $"{index}/{count}" : "0/0";
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        RunFind(false);
    }

    private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunFind(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)  => RunFind(false);
    private void FindPrev_Click(object sender, RoutedEventArgs e)  => RunFind(true);
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is BrowserTab tab && tab == _activeTab)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.PropertyName == nameof(BrowserTab.Url))
                {
                    if (!_isOmniboxFocused) UpdateOmniboxText(tab.Url);
                    UpdateSecurityBadge(tab.Url);
                    UpdateBookmarkStar();
                }
                else if (e.PropertyName == nameof(BrowserTab.CanGoBack) || e.PropertyName == nameof(BrowserTab.CanGoForward))
                {
                    BackBtn.IsEnabled    = tab.CanGoBack;
                    ForwardBtn.IsEnabled = tab.CanGoForward;
                }
                else if (e.PropertyName == nameof(BrowserTab.IsLoading))
                {
                    UpdateReloadIcon(tab.IsLoading);
                }
            });
        }
    }

    private void UpdateUiForActiveTab()
    {
        if (_activeTab == null) return;
        UpdateOmniboxText(_activeTab.Url);
        UpdateSecurityBadge(_activeTab.Url);
        BackBtn.IsEnabled    = _activeTab.CanGoBack;
        ForwardBtn.IsEnabled = _activeTab.CanGoForward;
        UpdateReloadIcon(_activeTab.IsLoading);
        UpdateBookmarkStar();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NAVIGATION / UI HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateOmniboxText(string url)
    {
        if (_activeTab == null || _activeTab.IsStartPage || string.IsNullOrWhiteSpace(url))
        {
            OmniboxTextBox.Text = string.Empty;
            OmniboxPlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            OmniboxTextBox.Text = url;
            OmniboxPlaceholder.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSecurityBadge(string url)
    {
        if (_activeTab != null && _activeTab.IsStartPage)
        {
            SecurityIcon.Fill = (Brush)FindResource("CrimsonRedBrush");
            return;
        }

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            SecurityIcon.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 80));
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            SecurityIcon.Fill = new SolidColorBrush(Color.FromRgb(255, 170, 0));
        else
            SecurityIcon.Fill = new SolidColorBrush(Color.FromRgb(230, 25, 55));
    }

    private void UpdateReloadIcon(bool isLoading)
    {
        var loc = LocalizationService.Instance;

        if (isLoading)
        {
            ReloadIcon.Data   = Geometry.Parse("M3 3 L11 11 M11 3 L3 11");
            ReloadBtn.ToolTip = loc.Get("StopTooltip");
        }
        else
        {
            ReloadIcon.Data   = Geometry.Parse("M7 1 A6 6 0 1 1 2 7 M7 1 L4 4 M7 1 L10 4");
            ReloadBtn.ToolTip = loc.Get("ReloadTooltip");
        }
    }

    private void UpdateBookmarkStar()
    {
        if (_activeTab == null || _activeTab.IsStartPage)
        {
            BookmarkStarText.Text       = "☆";
            BookmarkStarText.Foreground = new SolidColorBrush(Color.FromRgb(104, 108, 126));
            return;
        }

        bool isBookmarked = BookmarksService.Instance.Contains(_activeTab.Url);
        BookmarkStarText.Text       = isBookmarked ? "★" : "☆";
        BookmarkStarText.Foreground = isBookmarked
            ? new SolidColorBrush(Color.FromRgb(255, 200, 0))   // Gold star
            : new SolidColorBrush(Color.FromRgb(104, 108, 126)); // Grey outline
    }

    private void UpdateDownloadsBadge()
    {
        Dispatcher.Invoke(() =>
        {
            int active = DownloadsService.Instance.ActiveCount;
            int total  = DownloadsService.Instance.Downloads.Count;

            if (active > 0)
            {
                DownloadsBadge.Visibility = Visibility.Visible;
                DownloadsBadgeText.Text   = active.ToString();
            }
            else if (total > 0)
            {
                DownloadsBadge.Visibility = Visibility.Visible;
                DownloadsBadgeText.Text   = "✓";
            }
            else
            {
                DownloadsBadge.Visibility = Visibility.Collapsed;
            }

            NoDownloadsText.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TAB CLICK HANDLERS
    // ─────────────────────────────────────────────────────────────────────────

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The close button owns its click; it must not initiate a drag.
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null && node != sender;
             node = VisualTreeHelper.GetParent(node))
            if (node is Button) return;

        if (sender is FrameworkElement el && el.DataContext is BrowserTab tab)
        {
            SetActiveTab(tab);

            if (e.ChangedButton == MouseButton.Left)
            {
                _tabDragStart  = e.GetPosition(TabsItemsControl);
                _tabDragSource = tab;
                _isTabDragging = false;
            }
        }
    }

    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragSource is null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishTabDrag(commit: false);
            return;
        }

        if (!_isTabDragging)
        {
            Point position = e.GetPosition(TabsItemsControl);
            if (Math.Abs(position.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _tabDragFrom = Tabs.IndexOf(_tabDragSource);
            _tabDragTo = _tabDragFrom;
            for (int index = 0; index < Tabs.Count; index++)
            {
                if (TabsItemsControl.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                {
                    FinishTabDrag(commit: false);
                    return;
                }
                double left = container.TranslatePoint(new Point(), TabsItemsControl).X;
                var transform = new TranslateTransform();
                container.RenderTransform = transform;
                _tabDragSlots.Add((container, transform, left));
            }
            if (_tabDragFrom < 0 || _tabDragSlots.Count == 0) return;
            Panel.SetZIndex(_tabDragSlots[_tabDragFrom].Container, 10);
            _isTabDragging = true;
            if (sender is not UIElement dragElement || !dragElement.CaptureMouse())
            {
                FinishTabDrag(commit: false);
                return;
            }
        }

        if (Tabs.Count != _tabDragSlots.Count || Tabs.IndexOf(_tabDragSource) != _tabDragFrom)
        {
            FinishTabDrag(commit: false);
            return;
        }

        var source = _tabDragSlots[_tabDragFrom];
        double leftPosition = Math.Clamp(source.Left + e.GetPosition(TabsItemsControl).X - _tabDragStart.X,
            _tabDragSlots[0].Left, _tabDragSlots[^1].Left);
        source.Transform.X = leftPosition - source.Left;
        int targetIndex = 0;
        for (int i = 1; i < _tabDragSlots.Count; i++)
            if (leftPosition >= (_tabDragSlots[i - 1].Left + _tabDragSlots[i].Left) / 2) targetIndex = i;

        if (targetIndex != _tabDragTo)
        {
            _tabDragTo = targetIndex;
            for (int i = 0; i < _tabDragSlots.Count; i++)
            {
                if (i == _tabDragFrom) continue;
                int destination = i;
                if (i > _tabDragFrom && i <= _tabDragTo) destination--;
                else if (i < _tabDragFrom && i >= _tabDragTo) destination++;
                var slot = _tabDragSlots[i];
                slot.Transform.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(_tabDragSlots[destination].Left - slot.Left, TimeSpan.FromMilliseconds(120))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        e.Handled = true;
    }

    private void Tab_LostMouseCapture(object sender, MouseEventArgs e) => FinishTabDrag(commit: false);

    private void FinishTabDrag(bool commit)
    {
        BrowserTab? source = _tabDragSource;
        int target = _tabDragTo;
        bool move = commit && _isTabDragging && source is not null && Tabs.Count == _tabDragSlots.Count;
        _tabDragSource = null;
        _isTabDragging = false;
        foreach (var slot in _tabDragSlots)
        {
            slot.Transform.BeginAnimation(TranslateTransform.XProperty, null);
            slot.Container.RenderTransform = Transform.Identity;
            Panel.SetZIndex(slot.Container, 0);
        }
        _tabDragSlots.Clear();
        if (Mouse.Captured is DependencyObject captured && FindTabBorder(captured) is { } border)
            border.ReleaseMouseCapture();
        if (move && Tabs.IndexOf(source!) is int from && from >= 0 && from != target)
            Tabs.Move(from, target);
    }

    private static Border? FindTabBorder(DependencyObject? hit)
    {
        while (hit is not null)
        {
            if (hit is Border border && border.DataContext is BrowserTab)
                return border;

            hit = VisualTreeHelper.GetParent(hit);
        }

        return null;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BrowserTab tab) CloseTab(tab);
    }

    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            FinishTabDrag(commit: true);
        }

        if (e.ChangedButton == MouseButton.Middle &&
            sender is FrameworkElement el &&
            el.DataContext is BrowserTab tab)
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    private static BrowserTab? ContextTab(object sender) =>
        (sender as FrameworkElement)?.DataContext as BrowserTab;

    private void TabContext_Close(object sender, RoutedEventArgs e)
    {
        if (ContextTab(sender) is { } tab) CloseTab(tab);
    }

    private void TabContext_CloseOthers(object sender, RoutedEventArgs e)
    {
        if (ContextTab(sender) is not { } target) return;

        foreach (var tab in Tabs.ToList())
        {
            if (!ReferenceEquals(tab, target)) CloseTab(tab);
        }

        SetActiveTab(target);
    }

    private void TabContext_Duplicate(object sender, RoutedEventArgs e)
    {
        if (ContextTab(sender) is { IsStartPage: false } tab) AddNewTab(tab.Url);
    }

    private void TabContext_CopyUrl(object sender, RoutedEventArgs e)
    {
        if (ContextTab(sender) is { IsStartPage: false } tab)
        {
            try { Clipboard.SetText(tab.Url); } catch { }
        }
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddNewTab();

    private void BrandLogo_Click(object sender, MouseButtonEventArgs e) => AddNewTab();

    // ─────────────────────────────────────────────────────────────────────────
    // NAVIGATION TOOLBAR HANDLERS
    // ─────────────────────────────────────────────────────────────────────────

    private void BackBtn_Click(object sender, RoutedEventArgs e)    => _activeTab?.GoBack();
    private void ForwardBtn_Click(object sender, RoutedEventArgs e) => _activeTab?.GoForward();

    private void ReloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        if (_activeTab.IsLoading) _activeTab.Stop();
        else _activeTab.Reload();
    }

    private void HomeBtn_Click(object sender, RoutedEventArgs e) => _activeTab?.LoadStartPage();

    private void GoBtn_Click(object sender, RoutedEventArgs e) => NavigateActiveTab();

    private void OmniboxTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { NavigateActiveTab(); Keyboard.ClearFocus(); }
    }

    private void NavigateActiveTab()
    {
        string query = OmniboxTextBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;
        _activeTab?.Navigate(query);
    }

    private void OmniboxTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _isOmniboxFocused = true;
        OmniboxPlaceholder.Visibility = Visibility.Collapsed;
        OmniboxBorder.BorderBrush = (Brush)FindResource("CrimsonRedBrush");
    }

    private void OmniboxTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _isOmniboxFocused = false;
        OmniboxBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(44, 47, 62));
        if (string.IsNullOrWhiteSpace(OmniboxTextBox.Text))
            OmniboxPlaceholder.Visibility = Visibility.Visible;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BOOKMARKS
    // ─────────────────────────────────────────────────────────────────────────

    private void BookmarkStar_Click(object sender, RoutedEventArgs e) => ToggleCurrentBookmark();

    private void ToggleCurrentBookmark()
    {
        if (_activeTab == null || _activeTab.IsStartPage) return;
        BookmarksService.Instance.Toggle(_activeTab.Title, _activeTab.Url);
        UpdateBookmarkStar();
    }

    public void ToggleBookmarksBar()
    {
        BookmarksBarBorder.Visibility = BookmarksBarBorder.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BookmarkBarItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Bookmark bm)
        {
            _activeTab?.Navigate(bm.Url);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DOWNLOADS
    // ─────────────────────────────────────────────────────────────────────────

    private void DownloadsBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateDownloadsBadge();
        DownloadsPopup.PlacementTarget = DownloadsBtn;
        DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
    }

    private void ClearCompletedDownloads_Click(object sender, RoutedEventArgs e)
    {
        DownloadsService.Instance.ClearCompleted();
        UpdateDownloadsBadge();
    }

    private void OpenDownloadedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DownloadItem item } && item.IsCompleted)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.SavePath) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DownloadItem item }) return;
        string? folder = Path.GetDirectoryName(item.SavePath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + item.SavePath + "\""); }
        catch { }
    }

    private void MenuDownloads_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        DownloadsPopup.PlacementTarget = DownloadsBtn;
        DownloadsPopup.IsOpen = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SHIELDS & MENU ACTIONS
    // ─────────────────────────────────────────────────────────────────────────

    private void InitializeShieldsUi()
    {
        MasterShieldToggle.IsChecked = ShieldService.Instance.IsShieldsEnabled;
        ToggleAds.IsChecked    = ShieldService.Instance.BlockAdsAndTrackers;
        ToggleLowRam.IsChecked = ShieldService.Instance.LowRamMode;
        ToggleHttps.IsChecked  = ShieldService.Instance.EnforceHttps;
        TogglePopups.IsChecked = ShieldService.Instance.BlockPopups;

        UpdateBlockedStats();
        UpdateShieldsUi();

        ShieldService.Instance.PropertyChanged += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (e.PropertyName == nameof(ShieldService.BlockedCount))
                    UpdateBlockedStats();
                else if (e.PropertyName == nameof(ShieldService.IsShieldsEnabled))
                    UpdateShieldsUi();
                else if (e.PropertyName == nameof(ShieldService.LowRamMode))
                    ApplyMemorySaverPolicy();
            });
        };
    }

    /// <summary>
    /// Low-RAM policy: keep active tabs at normal priority, lower the memory target of every
    /// background tab, and unload tabs that have been idle for a while. Audio tabs are exempt.
    /// </summary>
    private void ApplyMemorySaverPolicy()
    {
        bool enabled = ShieldService.Instance.LowRamMode;

        foreach (var tab in Tabs.ToList())
        {
            if (tab == _activeTab)
            {
                tab.NoteActivity();
                continue;
            }

            if (tab.IsDocumentPlayingAudio)
            {
                // Never discard a tab that is still making sound; restart its idle timer.
                tab.NoteActivity();
                continue;
            }

            if (tab.IsDiscarded) continue;

            if (enabled && !tab.IsLoading && !tab.IsStartPage &&
                DateTime.UtcNow - tab.LastActivityUtc >= TabDiscardAfter)
            {
                tab.Discard();
                continue;
            }

            tab.ApplyMemoryPolicy(false);
        }

        _activeTab?.ApplyMemoryPolicy(true);
        _activeTab?.SetWindowMinimized(WindowState == WindowState.Minimized);
    }

    private void UpdateBlockedStats()
    {
        int count = ShieldService.Instance.BlockedCount;
        string key = count == 1 ? "ShieldItemBlocked" : "ShieldItemsBlocked";
        BlockedStatsText.Text = LocalizationService.Instance.Format(key, count);
    }

    private void UpdateShieldsUi()
    {
        bool enabled = ShieldService.Instance.IsShieldsEnabled;
        var loc = LocalizationService.Instance;

        if (enabled)
        {
            ShieldBtnText.Text       = loc.Get("ShieldsButton");
            ShieldBtnText.Foreground = (Brush)FindResource("CrimsonRedGlowBrush");
            ShieldBtnIcon.Fill       = (Brush)FindResource("CrimsonRedGlowBrush");
            ShieldsBtnBorder.Background   = new SolidColorBrush(Color.FromRgb(37, 18, 25));
            ShieldsBtnBorder.BorderBrush  = (Brush)FindResource("CrimsonDarkRedBrush");
            ShieldsStatusSubtext.Text     = loc.Get("ShieldsActive");
            ShieldsStatusSubtext.Foreground = (Brush)FindResource("CrimsonRedGlowBrush");
            PopupShieldIcon.Fill      = (Brush)FindResource("CrimsonRedGlowBrush");
            ShieldOptionsPanel.IsEnabled = true;
            ShieldOptionsPanel.Opacity   = 1.0;
        }
        else
        {
            ShieldBtnText.Text       = loc.Get("ShieldsButtonOff");
            ShieldBtnText.Foreground = new SolidColorBrush(Color.FromRgb(140, 145, 160));
            ShieldBtnIcon.Fill       = new SolidColorBrush(Color.FromRgb(140, 145, 160));
            ShieldsBtnBorder.Background   = new SolidColorBrush(Color.FromRgb(24, 25, 33));
            ShieldsBtnBorder.BorderBrush  = new SolidColorBrush(Color.FromRgb(45, 48, 62));
            ShieldsStatusSubtext.Text     = loc.Get("ShieldsDisabled");
            ShieldsStatusSubtext.Foreground = new SolidColorBrush(Color.FromRgb(140, 145, 160));
            PopupShieldIcon.Fill      = new SolidColorBrush(Color.FromRgb(140, 145, 160));
            ShieldOptionsPanel.IsEnabled = false;
            ShieldOptionsPanel.Opacity   = 0.45;
        }
    }

    private void ShieldsBtn_Click(object sender, MouseButtonEventArgs e)
    {
        ShieldsPopup.PlacementTarget = sender as UIElement;
        ShieldsPopup.IsOpen = !ShieldsPopup.IsOpen;
    }

    private void MasterShieldToggle_Click(object sender, RoutedEventArgs e)
    {
        ShieldService.Instance.IsShieldsEnabled = MasterShieldToggle.IsChecked ?? true;
        UpdateShieldsUi();
    }

    private void ToggleAds_Click(object sender, RoutedEventArgs e)    => ShieldService.Instance.BlockAdsAndTrackers = ToggleAds.IsChecked ?? true;
    private void ToggleLowRam_Click(object sender, RoutedEventArgs e)  => ShieldService.Instance.LowRamMode         = ToggleLowRam.IsChecked ?? true;
    private void ToggleHttps_Click(object sender, RoutedEventArgs e)   => ShieldService.Instance.EnforceHttps        = ToggleHttps.IsChecked ?? true;
    private void TogglePopups_Click(object sender, RoutedEventArgs e)  => ShieldService.Instance.BlockPopups         = TogglePopups.IsChecked ?? true;
    private void ResetBlockedStats_Click(object sender, RoutedEventArgs e) => ShieldService.Instance.ResetCount();
    private void OpenDevTools_Click(object sender, RoutedEventArgs e)  => _activeTab?.ShowDevTools();

    // ─────────────────────────────────────────────────────────────────────────
    // MENU (3-DOTS)
    // ─────────────────────────────────────────────────────────────────────────

    private void MenuBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateMenuZoomDisplay();
        UpdateDefaultBrowserMenu();
        MenuPopup.IsOpen = !MenuPopup.IsOpen;
    }

    private void UpdateDefaultBrowserMenu()
    {
        bool isDefault = WindowsIntegration.IsDefaultBrowser();
        var loc = LocalizationService.Instance;

        MenuDefaultBrowserText.Text = loc.Get(isDefault
            ? "MenuDefaultBrowserAlready"
            : "MenuDefaultBrowser");
        MenuDefaultBrowserHint.Text = loc.Get(isDefault
            ? "MenuDefaultBrowserDone"
            : "MenuDefaultBrowserHint");
    }

    private void UpdateMenuZoomDisplay()
    {
        int zoomPct = _activeTab != null
            ? (int)Math.Round(_activeTab.WebView.ZoomFactor * 100)
            : 100;
        MenuZoomText.Text = $"{zoomPct}%";
    }

    private void MenuNewTab_Click(object sender, RoutedEventArgs e)    { MenuPopup.IsOpen = false; AddNewTab(); }
    private void MenuNewWindow_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; var w = new MainWindow(); w.Show(); }
    private void MenuIncognito_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenIncognito();
    }

    private void MenuReopenTab_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        ReopenLastTab();
    }

    private void MenuHistory_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenHistory();
    }

    private void MenuFind_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenFind();
    }

    private void MenuFullscreen_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        ToggleFullscreen();
    }

    private void MenuToggleBookmarksBar_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        ToggleBookmarksBar();
    }

    private void MenuZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab != null && _activeTab.WebView.ZoomFactor < 3.0)
        {
            _activeTab.WebView.ZoomFactor += 0.1;
            UpdateMenuZoomDisplay();
        }
    }

    private void MenuZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab != null && _activeTab.WebView.ZoomFactor > 0.3)
        {
            _activeTab.WebView.ZoomFactor -= 0.1;
            UpdateMenuZoomDisplay();
        }
    }

    private void MenuDevTools_Click(object sender, RoutedEventArgs e)  { MenuPopup.IsOpen = false; _activeTab?.ShowDevTools(); }

    private async void MenuClearData_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        var loc = LocalizationService.Instance;

        bool confirmed = CrimsonDialog.Show(
            this,
            loc.Get("ClearDataTitle"),
            loc.Get("ClearDataMessage"),
            loc.Get("ClearDataAction"),
            loc.Get("DialogCancel"),
            danger: true);

        if (!confirmed) return;

        if (_activeTab != null)
        {
            await _activeTab.ClearBrowsingDataAsync();
            CefProfileData.RequestReset();
            HistoryService.Instance.Clear();

            CrimsonDialog.Info(
                this,
                loc.Get("ClearDataDoneTitle"),
                loc.Get("ClearDataDoneMessage"));
        }
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        var loc = LocalizationService.Instance;
        CrimsonDialog.Info(
            this,
            loc.Get("AboutTitle"),
            loc.Format("AboutBody", UpdateService.CurrentVersion));
    }

    private void MenuSettings_Click(object sender, RoutedEventArgs e)  { MenuPopup.IsOpen = false; OpenSettings(); }

    private void MenuDefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        var loc = LocalizationService.Instance;

        if (WindowsIntegration.IsDefaultBrowser())
        {
            CrimsonDialog.Info(
                this,
                loc.Get("DefaultBrowserAlreadyTitle"),
                loc.Get("DefaultBrowserAlreadyMessage"));
            return;
        }

        bool proceed = CrimsonDialog.Show(
            this,
            loc.Get("DefaultBrowserTitle"),
            loc.Get("DefaultBrowserMessage"),
            loc.Get("DefaultBrowserOpen"),
            loc.Get("DialogCancel"));

        if (!proceed) return;

        try
        {
            WindowsIntegration.Register();
        }
        catch (Exception ex)
        {
            CrimsonDialog.Info(this, loc.Get("DefaultBrowserErrorTitle"), ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SETTINGS / HISTORY / INCOGNITO
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // GITHUB AUTO-UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    private async Task CheckForUpdatesAutomaticallyAsync()
    {
        if (!_restoreSession || !SettingsService.Instance.AutoCheckUpdates)
            return;

        try
        {
            // Give the first page time to load; the check itself never blocks the UI.
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (!IsVisible || !IsEnabled) return;

            UpdateCheckResult result = await UpdateService.CheckAsync();
            if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Update is null)
                return;

            string version = result.Update.Version.ToString();
            if (string.Equals(SettingsService.Instance.SkippedUpdateVersion, version,
                    StringComparison.OrdinalIgnoreCase))
                return;

            await Dispatcher.InvokeAsync(() => PromptForUpdate(result.Update));
        }
        catch
        {
            // Update checks are best-effort; offline or rate-limited clients stay silent.
        }
    }

    private async void PromptForUpdate(UpdateInfo update)
    {
        var loc = LocalizationService.Instance;
        string message =
            loc.Format("UpdateAvailableMessage", update.ReleaseName, update.Version) +
            Environment.NewLine + Environment.NewLine +
            loc.Get("UpdateRestartNotice");

        bool install = CrimsonDialog.Show(
            this,
            loc.Get("UpdateAvailableTitle"),
            message,
            loc.Get("DownloadAndInstall"),
            loc.Get("UpdateLater"));

        if (!install)
        {
            SettingsService.Instance.SkippedUpdateVersion = update.Version.ToString();
            return;
        }

        SettingsService.Instance.SkippedUpdateVersion = null;
        var progressDialog = new UpdateProgressDialog(this, update.Version);
        progressDialog.Show();

        try
        {
            var progress = new Progress<double>(progressDialog.SetProgress);
            await UpdateService.DownloadAndInstallAsync(update, progress);
        }
        catch (OperationCanceledException)
        {
            progressDialog.Close();
        }
        catch (Exception ex)
        {
            progressDialog.Close();
            CrimsonDialog.Info(
                this,
                loc.Get("UpdateInstallFailedTitle"),
                loc.Format("UpdateInstallFailedMessage", ex.Message));
        }
    }

    public void OpenSettings()
    {
        var settingsWin = new SettingsWindow(this);
        settingsWin.ShowDialog();
    }

    public void OpenHistory()
    {
        var histWin = new HistoryWindow(this);
        histWin.Show();
    }

    public void OpenIncognito()
    {
        var incog = new IncognitoWindow();
        incog.Show();
    }

    /// <summary>Navigate the active tab, or add a new tab if the browser isn't loaded yet.</summary>
    public void NavigateOrAddTab(string url)
    {
        if (_activeTab != null)
            _activeTab.Navigate(url);
        else
            AddNewTab(url);
    }

    /// <summary>Entry point for URLs forwarded by a second Crimson process.</summary>
    public void OpenExternalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        string target = url.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target)) return;

        // External launches must preserve the page the user is currently working on.
        // Environment creation is asynchronous, so retain early requests until startup completes.
        if (!_browserInitialized)
        {
            _pendingExternalUrls.Enqueue(target);
            return;
        }

        AddNewTab(target);
    }

    public void RefreshAllStartPages()
    {
        foreach (var tab in Tabs)
            tab.RefreshStartPageWallpaper();
    }

    public void ApplyLocalizationToAllUi()
    {
        if (BlockedStatsText == null || MenuDefaultBrowserText == null) return;
        var loc = LocalizationService.Instance;

        // Static XAML labels refresh themselves through the {loc:Loc ...} bindings.
        // Only values computed in code need an explicit refresh.
        UpdateBlockedStats();
        UpdateShieldsUi();
        UpdateDefaultBrowserMenu();
        UpdateReloadIcon(_activeTab?.IsLoading ?? false);
        MaximizeBtn.ToolTip = loc.Get(WindowState == WindowState.Maximized
            ? "RestoreTooltip"
            : "MaximizeTooltip");

        foreach (var tab in Tabs)
        {
            if (tab.IsStartPage)
            {
                tab.Title = loc.Get("NewTab");
                tab.UpdateStartPageLanguage(loc.CurrentLanguage);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WINDOW CHROME CONTROLS
    // ─────────────────────────────────────────────────────────────────────────

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        _activeTab?.SetWindowMinimized(WindowState == WindowState.Minimized);
        MaximizeIcon.Data = WindowState == WindowState.Maximized
            ? Geometry.Parse("M2 1 L9 1 L9 8 M1 3 L8 3 L8 10 L1 10 Z")
            : Geometry.Parse("M1 1 L9 1 L9 9 L1 9 Z");
        MaximizeBtn.ToolTip = LocalizationService.Instance.Get(
            WindowState == WindowState.Maximized ? "RestoreTooltip" : "MaximizeTooltip");
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}

// ─────────────────────────────────────────────────────────────────────────────
// RelayCommand
// ─────────────────────────────────────────────────────────────────────────────

public class RelayCommand : ICommand
{
    private readonly Action<object?>   _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter)    => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
