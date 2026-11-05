using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CrimsonBrowser.Models;
using CrimsonBrowser.Services;

namespace CrimsonBrowser;

public partial class HistoryWindow : Window
{
    private readonly MainWindow _mainWindow;
    private string _lastQuery = string.Empty;

    public HistoryWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        Owner = mainWindow;

        InitializeComponent();
        Loaded += HistoryWindow_Loaded;
        LocalizationService.Instance.LanguageChanged += HistoryWindow_LanguageChanged;
    }

    private void HistoryWindow_LanguageChanged()
    {
        Dispatcher.Invoke(() => RefreshList(_lastQuery));
    }

    protected override void OnClosed(EventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= HistoryWindow_LanguageChanged;
        base.OnClosed(e);
    }

    private void HistoryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshList(string.Empty);
        // Update placeholder visibility
        SearchBox.GotFocus  += (s, _) => SearchPlaceholder.Visibility = Visibility.Collapsed;
        SearchBox.LostFocus += (s, _) =>
        {
            if (string.IsNullOrEmpty(SearchBox.Text))
                SearchPlaceholder.Visibility = Visibility.Visible;
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshList(SearchBox.Text);
    }

    private void RefreshList(string query)
    {
        _lastQuery = query;
        var entries = HistoryService.Instance.Search(query);
        HistoryList.ItemsSource = entries;

        string countKey = entries.Count == 1 ? "HistoryEntry" : "HistoryEntries";
        EntryCountText.Text = LocalizationService.Instance.Format(countKey, entries.Count);
    }

    private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is HistoryEntry entry)
        {
            // Navigate the active tab (or open new tab) to the clicked history item
            _mainWindow.NavigateOrAddTab(entry.Url);
            Close();
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        bool confirmed = CrimsonDialog.Show(
            this,
            loc.Get("HistoryClearTitle"),
            loc.Get("HistoryClearMessage"),
            loc.Get("HistoryClearAction"),
            loc.Get("DialogCancel"),
            danger: true);

        if (confirmed)
        {
            HistoryService.Instance.Clear();
            RefreshList(string.Empty);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
