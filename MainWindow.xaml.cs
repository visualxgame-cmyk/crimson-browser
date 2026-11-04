using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CrimsonBrowser.Models;
using CrimsonBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace CrimsonBrowser;

public partial class MainWindow : Window
{
    private CoreWebView2Environment? _webViewEnvironment;
    public ObservableCollection<BrowserTab> Tabs { get; } = new();
    private BrowserTab? _activeTab;
    private bool _isOmniboxFocused;

    // Stack for Ctrl+Shift+T — reopen last closed tab
    private readonly Stack<(string url, string title)> _closedTabStack = new();

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

    public MainWindow()
    {
        InitializeComponent();

        NewTabCommand            = new RelayCommand(_ => AddNewTab());
        CloseCurrentTabCommand   = new RelayCommand(_ => { if (_activeTab != null) CloseTab(_activeTab); });
        RefreshCommand           = new RelayCommand(_ => _activeTab?.Reload());
        FocusOmniboxCommand      = new RelayCommand(_ => { OmniboxTextBox.Focus(); OmniboxTextBox.SelectAll(); });
        OpenDevToolsCommand      = new RelayCommand(_ => _activeTab?.WebView.CoreWebView2?.OpenDevToolsWindow());
        OpenHistoryCommand       = new RelayCommand(_ => OpenHistory());
        OpenIncognitoCommand     = new RelayCommand(_ => OpenIncognito());
        ReopenClosedTabCommand   = new RelayCommand(_ => ReopenLastTab());
        ToggleBookmarksBarCommand = new RelayCommand(_ => ToggleBookmarksBar());
        ToggleBookmarkCommand    = new RelayCommand(_ => ToggleCurrentBookmark());
        NewWindowCommand         = new RelayCommand(_ => { var w = new MainWindow(); w.Show(); });

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

        Loaded       += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeBrowserEnvironmentAsync();
    }

    private async Task InitializeBrowserEnvironmentAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrimsonBrowser",
                "UserData"
            );

            var options = new CoreWebView2EnvironmentOptions(
                "--disable-features=Translate --enable-features=OverlayScrollbar"
            );

            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);

            AddNewTab();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error starting Chromium engine:\n{ex.Message}\n\nMake sure Microsoft Edge WebView2 Runtime is installed.",
                "Crimson Browser Error",
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

        var tab = new BrowserTab(_webViewEnvironment);

        tab.RequestNewTab += (senderTab, targetUrl) =>
            Dispatcher.Invoke(() => AddNewTab(targetUrl));

        tab.RequestClose += (senderTab) =>
            Dispatcher.Invoke(() => CloseTab(senderTab));

        tab.RequestOpenSettings += () =>
            Dispatcher.Invoke(OpenSettings);

        tab.PropertyChanged += Tab_PropertyChanged;

        Tabs.Add(tab);
        WebContainer.Children.Add(tab.WebView);
        SetActiveTab(tab);

        if (!string.IsNullOrWhiteSpace(initialUrl))
            tab.Navigate(initialUrl);
    }

    public void SetActiveTab(BrowserTab tab)
    {
        if (_activeTab != null) _activeTab.IsActive = false;
        _activeTab = tab;
        _activeTab.IsActive = true;
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
        if (isLoading)
        {
            ReloadIcon.Data   = Geometry.Parse("M3 3 L11 11 M11 3 L3 11");
            ReloadBtn.ToolTip = "Stop loading";
        }
        else
        {
            ReloadIcon.Data   = Geometry.Parse("M7 1 A6 6 0 1 1 2 7 M7 1 L4 4 M7 1 L10 4");
            ReloadBtn.ToolTip = "Reload (Ctrl+R / F5)";
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
        if (sender is FrameworkElement el && el.DataContext is BrowserTab tab) SetActiveTab(tab);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BrowserTab tab) CloseTab(tab);
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

        BlockedStatsText.Text = $"{ShieldService.Instance.BlockedCount} items blocked";
        UpdateShieldsUi();

        ShieldService.Instance.PropertyChanged += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (e.PropertyName == nameof(ShieldService.BlockedCount))
                    BlockedStatsText.Text = $"{ShieldService.Instance.BlockedCount} items blocked";
                else if (e.PropertyName == nameof(ShieldService.IsShieldsEnabled))
                    UpdateShieldsUi();
            });
        };
    }

    private void UpdateShieldsUi()
    {
        bool enabled = ShieldService.Instance.IsShieldsEnabled;

        if (enabled)
        {
            ShieldBtnText.Text       = "SHIELDS";
            ShieldBtnText.Foreground = (Brush)FindResource("CrimsonRedGlowBrush");
            ShieldBtnIcon.Fill       = (Brush)FindResource("CrimsonRedGlowBrush");
            ShieldsBtnBorder.Background   = new SolidColorBrush(Color.FromRgb(37, 18, 25));
            ShieldsBtnBorder.BorderBrush  = (Brush)FindResource("CrimsonDarkRedBrush");
            ShieldsStatusSubtext.Text     = "Protection active on this site";
            ShieldsStatusSubtext.Foreground = (Brush)FindResource("CrimsonRedGlowBrush");
            PopupShieldIcon.Fill      = (Brush)FindResource("CrimsonRedGlowBrush");
            ShieldOptionsPanel.IsEnabled = true;
            ShieldOptionsPanel.Opacity   = 1.0;
        }
        else
        {
            ShieldBtnText.Text       = "SHIELDS OFF";
            ShieldBtnText.Foreground = new SolidColorBrush(Color.FromRgb(140, 145, 160));
            ShieldBtnIcon.Fill       = new SolidColorBrush(Color.FromRgb(140, 145, 160));
            ShieldsBtnBorder.Background   = new SolidColorBrush(Color.FromRgb(24, 25, 33));
            ShieldsBtnBorder.BorderBrush  = new SolidColorBrush(Color.FromRgb(45, 48, 62));
            ShieldsStatusSubtext.Text     = "Protection disabled";
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
    private void OpenDevTools_Click(object sender, RoutedEventArgs e)  => _activeTab?.WebView.CoreWebView2?.OpenDevToolsWindow();

    // ─────────────────────────────────────────────────────────────────────────
    // MENU (3-DOTS)
    // ─────────────────────────────────────────────────────────────────────────

    private void MenuBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateMenuZoomDisplay();
        MenuPopup.IsOpen = !MenuPopup.IsOpen;
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

    private void MenuDevTools_Click(object sender, RoutedEventArgs e)  { MenuPopup.IsOpen = false; _activeTab?.WebView.CoreWebView2?.OpenDevToolsWindow(); }

    private async void MenuClearData_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (_activeTab?.WebView.CoreWebView2 != null)
        {
            await _activeTab.WebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            HistoryService.Instance.Clear();
            MessageBox.Show(
                "Cache, browsing data, and history cleared successfully!",
                "Crimson Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        MessageBox.Show(
            "🔴 Crimson Browser v1.0.0\n\n" +
            "• Engine: Chromium (Blink & V8)\n" +
            "• Platform: .NET 10 & Microsoft WebView2 Evergreen\n" +
            "• Theme: Crimson Stealth (Red & Black)\n" +
            "• Resource Footprint: Ultra-low (Low-RAM Optimizer Active)\n" +
            "• Protection: Crimson Shields Engine\n" +
            "• Features: History, Bookmarks Bar, Incognito, Downloads Hub",
            "About Crimson Browser",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }

    private void MenuSettings_Click(object sender, RoutedEventArgs e)  { MenuPopup.IsOpen = false; OpenSettings(); }

    // ─────────────────────────────────────────────────────────────────────────
    // SETTINGS / HISTORY / INCOGNITO
    // ─────────────────────────────────────────────────────────────────────────

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

    public void RefreshAllStartPages()
    {
        foreach (var tab in Tabs)
            tab.RefreshStartPageWallpaper();
    }

    public void ApplyLocalizationToAllUi()
    {
        if (OmniboxPlaceholder == null || BackBtn == null || MenuClearDataText == null) return;
        var loc = LocalizationService.Instance;

        OmniboxPlaceholder.Text = loc.Get("OmniboxPlaceholder");
        BackBtn.ToolTip    = loc.Get("BackTooltip");
        ForwardBtn.ToolTip = loc.Get("ForwardTooltip");
        ReloadBtn.ToolTip  = loc.Get("ReloadTooltip");

        MenuClearDataText.Text = loc.Get("MenuClearData");
        MenuSettingsText.Text  = loc.Get("MenuSettings");
        MenuAboutText.Text     = loc.Get("MenuAbout");

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
        MaximizeIcon.Data = WindowState == WindowState.Maximized
            ? Geometry.Parse("M2 1 L9 1 L9 8 M1 3 L8 3 L8 10 L1 10 Z")
            : Geometry.Parse("M1 1 L9 1 L9 9 L1 9 Z");
        MaximizeBtn.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
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
