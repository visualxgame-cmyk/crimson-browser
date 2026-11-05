using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CrimsonBrowser.Models;
using CrimsonBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace CrimsonBrowser;

/// <summary>A fully isolated Incognito (InPrivate) browser window with a separate WebView2 profile.</summary>
public partial class IncognitoWindow : Window
{
    private CoreWebView2Environment? _webViewEnvironment;
    public ObservableCollection<BrowserTab> Tabs { get; } = new();
    private BrowserTab? _activeTab;
    private bool _isOmniboxFocused;
    private readonly PermissionBroker _permissionBroker;

    private bool _isFullscreen;
    private WindowState _preFullscreenState = WindowState.Normal;
    private Rect _preFullscreenBounds = Rect.Empty;

    public ICommand NewTabCommand          { get; }
    public ICommand CloseCurrentTabCommand { get; }
    public ICommand RefreshCommand         { get; }
    public ICommand FocusOmniboxCommand    { get; }
    public ICommand OpenDevToolsCommand    { get; }
    public ICommand ToggleFullscreenCommand { get; }
    public ICommand NextTabCommand         { get; }
    public ICommand PrevTabCommand         { get; }
    public ICommand FindCommand            { get; }

    public IncognitoWindow()
    {
        InitializeComponent();

        // Incognito choices are kept in memory only for this window.
        _permissionBroker = new PermissionBroker(this, persistChoices: false);

        NewTabCommand          = new RelayCommand(_ => AddNewTab());
        CloseCurrentTabCommand = new RelayCommand(_ => { if (_activeTab != null) CloseTab(_activeTab); });
        RefreshCommand         = new RelayCommand(_ => _activeTab?.Reload());
        FocusOmniboxCommand    = new RelayCommand(_ => { OmniboxTextBox.Focus(); OmniboxTextBox.SelectAll(); });
        OpenDevToolsCommand    = new RelayCommand(_ => _activeTab?.WebView.CoreWebView2?.OpenDevToolsWindow());
        ToggleFullscreenCommand = new RelayCommand(_ => ToggleFullscreen());
        NextTabCommand         = new RelayCommand(_ => SelectNextTab(1));
        PrevTabCommand         = new RelayCommand(_ => SelectNextTab(-1));
        FindCommand            = new RelayCommand(_ => OpenFind());

        TabsItemsControl.ItemsSource = Tabs;

        Loaded       += IncognitoWindow_Loaded;
        StateChanged += IncognitoWindow_StateChanged;
        PreviewKeyDown += Window_PreviewKeyDown;
        LocalizationService.Instance.LanguageChanged += IncognitoWindow_LanguageChanged;
    }

    private void IncognitoWindow_LanguageChanged()
    {
        Dispatcher.Invoke(() =>
        {
            if (_activeTab != null)
                UpdateReloadIcon(_activeTab.IsLoading);

            MaximizeBtn.ToolTip = LocalizationService.Instance.Get(
                WindowState == WindowState.Maximized ? "RestoreTooltip" : "MaximizeTooltip");
        });
    }

    private async void IncognitoWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeBrowserEnvironmentAsync();
    }

    private async Task InitializeBrowserEnvironmentAsync()
    {
        try
        {
            // Use a temporary folder in %TEMP% — deleted on window close
            string tempFolder = Path.Combine(Path.GetTempPath(), "CrimsonIncognito_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempFolder);

            // Incognito isolation comes from the throw-away user data folder; there is no
            // "--inprivate" switch in the WebView2 environment options API.
            var options = new CoreWebView2EnvironmentOptions(
                "--disable-features=Translate --enable-features=OverlayScrollbar");
            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, tempFolder, options);

            // Store temp folder path for cleanup
            _tempFolder = tempFolder;

            AddNewTab();
        }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            MessageBox.Show(
                loc.Format("IncognitoStartErrorMessage", ex.Message),
                loc.Get("IncognitoStartErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    private string? _tempFolder;

    public void AddNewTab(string? initialUrl = null)
    {
        if (_webViewEnvironment == null) return;

        var tab = new BrowserTab(_webViewEnvironment, isIncognito: true);

        tab.RequestNewTab += (senderTab, targetUrl) => Dispatcher.Invoke(() => AddNewTab(targetUrl));
        tab.RequestClose  += (senderTab) => Dispatcher.Invoke(() => CloseTab(senderTab));
        tab.PermissionRequested += (senderTab, args) => _permissionBroker.Handle(senderTab, args);
        tab.RequestShortcut += (senderTab, action) => Dispatcher.Invoke(() => HandleShortcut(senderTab, action));
        tab.FullscreenChanged += isFullscreen => Dispatcher.Invoke(() => SetFullscreen(isFullscreen));
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

        if (FindBar.Visibility == Visibility.Visible)
            CloseFind();

        _activeTab = tab;
        _activeTab.IsActive = true;
        UpdateUiForActiveTab();
    }

    public void CloseTab(BrowserTab tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0) return;

        if (Tabs.Count == 1) { Close(); return; }

        bool wasActive = tab.IsActive;
        tab.PropertyChanged -= Tab_PropertyChanged;
        WebContainer.Children.Remove(tab.WebView);
        Tabs.Remove(tab);
        tab.Dispose();

        if (wasActive && Tabs.Count > 0)
            SetActiveTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is BrowserTab tab && tab == _activeTab)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.PropertyName == nameof(BrowserTab.Url))
                {
                    if (!_isOmniboxFocused) UpdateOmniboxText(tab.Url);
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
        BackBtn.IsEnabled    = _activeTab.CanGoBack;
        ForwardBtn.IsEnabled = _activeTab.CanGoForward;
        UpdateReloadIcon(_activeTab.IsLoading);
    }

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

    private void UpdateReloadIcon(bool isLoading)
    {
        var loc = LocalizationService.Instance;

        if (isLoading)
        {
            ReloadIcon.Data = Geometry.Parse("M3 3 L11 11 M11 3 L3 11");
            ReloadBtn.ToolTip = loc.Get("StopTooltip");
        }
        else
        {
            ReloadIcon.Data = Geometry.Parse("M7 1 A6 6 0 1 1 2 7 M7 1 L4 4 M7 1 L10 4");
            ReloadBtn.ToolTip = loc.Get("ReloadTooltip");
        }
    }

    // ── Tabs ──
    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is BrowserTab tab) SetActiveTab(tab);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BrowserTab tab) CloseTab(tab);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddNewTab();

    // ── Navigation ──
    private void BackBtn_Click(object sender, RoutedEventArgs e)    => _activeTab?.GoBack();
    private void ForwardBtn_Click(object sender, RoutedEventArgs e) => _activeTab?.GoForward();
    private void GoBtn_Click(object sender, RoutedEventArgs e)      => NavigateActiveTab();

    private void ReloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        if (_activeTab.IsLoading) _activeTab.Stop();
        else _activeTab.Reload();
    }

    private void OmniboxTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { NavigateActiveTab(); Keyboard.ClearFocus(); }
    }

    private void OmniboxTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _isOmniboxFocused = true;
        OmniboxPlaceholder.Visibility = Visibility.Collapsed;
        OmniboxBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(139, 0, 255));
    }

    private void OmniboxTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _isOmniboxFocused = false;
        OmniboxBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(61, 26, 102));
        if (string.IsNullOrWhiteSpace(OmniboxTextBox.Text))
            OmniboxPlaceholder.Visibility = Visibility.Visible;
    }

    private void NavigateActiveTab()
    {
        string query = OmniboxTextBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;
        _activeTab?.Navigate(query);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SHORTCUTS (forwarded from the page because WebView2 swallows key input)
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
            case "new-tab": AddNewTab(); break;
            case "new-window": new IncognitoWindow().Show(); break;
            case "close-tab": CloseTab(tab); break;
            case "focus-omnibox":
                OmniboxTextBox.Focus();
                OmniboxTextBox.SelectAll();
                break;
            case "find": OpenFind(); break;
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
        _activeTab.WebView.ZoomFactor = Math.Clamp(zoom, 0.25, 3.0);
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

            TopBarRow.Height = new GridLength(0);
            NavBarRow.Height = new GridLength(0);

            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;

            _isFullscreen = true;
            FullscreenHelper.Enter(this);
        }
        else
        {
            _isFullscreen = false;
            FullscreenHelper.Exit(this);

            TopBarRow.Height = new GridLength(44);
            NavBarRow.Height = new GridLength(50);

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

    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle &&
            sender is FrameworkElement el &&
            el.DataContext is BrowserTab tab)
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    // ── Window chrome ──
    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
    }

    private void IncognitoWindow_StateChanged(object? sender, EventArgs e)
    {
        MaximizeIcon.Data = WindowState == WindowState.Maximized
            ? Geometry.Parse("M2 1 L9 1 L9 8 M1 3 L8 3 L8 10 L1 10 Z")
            : Geometry.Parse("M1 1 L9 1 L9 9 L1 9 Z");

        MaximizeBtn.ToolTip = LocalizationService.Instance.Get(
            WindowState == WindowState.Maximized ? "RestoreTooltip" : "MaximizeTooltip");
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= IncognitoWindow_LanguageChanged;
        base.OnClosed(e);
        // Clean up temp profile directory
        try
        {
            foreach (var tab in Tabs.ToList()) tab.Dispose();
            if (_tempFolder != null && Directory.Exists(_tempFolder))
                Directory.Delete(_tempFolder, true);
        }
        catch { }
    }
}
