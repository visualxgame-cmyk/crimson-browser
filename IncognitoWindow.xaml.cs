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

    public ICommand NewTabCommand          { get; }
    public ICommand CloseCurrentTabCommand { get; }
    public ICommand RefreshCommand         { get; }
    public ICommand FocusOmniboxCommand    { get; }
    public ICommand OpenDevToolsCommand    { get; }

    public IncognitoWindow()
    {
        InitializeComponent();

        NewTabCommand          = new RelayCommand(_ => AddNewTab());
        CloseCurrentTabCommand = new RelayCommand(_ => { if (_activeTab != null) CloseTab(_activeTab); });
        RefreshCommand         = new RelayCommand(_ => _activeTab?.Reload());
        FocusOmniboxCommand    = new RelayCommand(_ => { OmniboxTextBox.Focus(); OmniboxTextBox.SelectAll(); });
        OpenDevToolsCommand    = new RelayCommand(_ => _activeTab?.WebView.CoreWebView2?.OpenDevToolsWindow());

        TabsItemsControl.ItemsSource = Tabs;

        Loaded       += IncognitoWindow_Loaded;
        StateChanged += IncognitoWindow_StateChanged;
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
            MessageBox.Show(
                $"Failed to start Incognito mode:\n{ex.Message}",
                "Crimson Browser",
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
        if (isLoading)
        {
            ReloadIcon.Data = Geometry.Parse("M3 3 L11 11 M11 3 L3 11");
            ReloadBtn.ToolTip = "Stop";
        }
        else
        {
            ReloadIcon.Data = Geometry.Parse("M7 1 A6 6 0 1 1 2 7 M7 1 L4 4 M7 1 L10 4");
            ReloadBtn.ToolTip = "Reload";
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
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
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
