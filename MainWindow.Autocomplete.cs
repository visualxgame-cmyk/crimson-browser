using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CrimsonBrowser.Services;

namespace CrimsonBrowser;

public partial class MainWindow
{
    private bool _settingAddress, _editingAddress, _suppressInlineCompletion;
    private readonly Func<string, IReadOnlyList<OmniboxSuggestion>> _addressSuggestions;
    private int _addressRevision;
    private string? _suggestedText, _suggestedUrl;

    private void Omnibox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => _editingAddress = true;

    private void Omnibox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        _editingAddress = true;
        _suppressInlineCompletion = true;
    }

    private void Omnibox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _editingAddress = true;
        _suppressInlineCompletion = e.Key is Key.Back or Key.Delete;
        if (e.Key is Key.Escape && OmniboxSuggestionsPopup.IsOpen)
        {
            CloseAddressSuggestions();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up && OmniboxSuggestionsPopup.IsOpen)
        {
            int count = OmniboxSuggestionsList.Items.Count;
            int index = OmniboxSuggestionsList.SelectedIndex;
            OmniboxSuggestionsList.SelectedIndex = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, count - 1);
            if (OmniboxSuggestionsList.SelectedItem is OmniboxSuggestion item)
            {
                SetAddressText(item.Address);
                RememberAddressSuggestion(item);
                OmniboxTextBox.SelectAll();
                OmniboxSuggestionsList.ScrollIntoView(item);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && OmniboxSuggestionsPopup.IsOpen)
        {
            if (OmniboxSuggestionsList.SelectedItem is OmniboxSuggestion item)
                NavigateAddressSuggestion(item);
            else { CloseAddressSuggestions(); NavigateActiveTab(); Keyboard.ClearFocus(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && OmniboxTextBox.SelectionLength > 0 && OmniboxSuggestionsPopup.IsOpen)
        {
            OmniboxTextBox.CaretIndex = OmniboxTextBox.Text.Length;
            OmniboxTextBox.SelectionLength = 0;
            CloseAddressSuggestions();
            e.Handled = true;
        }
    }

    private void Omnibox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingAddress || !_editingAddress || !OmniboxTextBox.IsKeyboardFocusWithin) return;
        _suggestedText = _suggestedUrl = null;
        int revision = ++_addressRevision;
        bool allowCompletion = !_suppressInlineCompletion;
        _suppressInlineCompletion = false;
        // TextChanged precedes WPF's final caret update for a typed composition.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (revision == _addressRevision && OmniboxTextBox.IsKeyboardFocusWithin)
                RefreshAddressSuggestions(allowCompletion);
        }), DispatcherPriority.Input);
    }

    private void RefreshAddressSuggestions(bool allowCompletion)
    {
        string typed = OmniboxTextBox.Text;
        bool complete = allowCompletion && OmniboxTextBox.CaretIndex == typed.Length &&
            OmniboxTextBox.SelectionLength == 0 && !InputMethod.Current.ImeState.Equals(InputMethodState.On);
        _suppressInlineCompletion = false;
        var suggestions = _addressSuggestions(typed);
        OmniboxSuggestionsList.ItemsSource = suggestions;
        OmniboxSuggestionsList.SelectedIndex = -1;
        OmniboxSuggestionsPopup.IsOpen = suggestions.Count > 0;
        if (complete && suggestions.FirstOrDefault()?.Completion is { } address && address.Length > typed.Length)
        {
            SetAddressText(typed + address[typed.Length..]);
            RememberAddressSuggestion(suggestions[0]);
            OmniboxTextBox.Select(typed.Length, address.Length - typed.Length);
        }
    }

    private void SetAddressText(string text)
    {
        _suggestedText = _suggestedUrl = null;
        _settingAddress = true;
        try { OmniboxTextBox.Text = text; }
        finally { _settingAddress = false; }
    }

    private void RememberAddressSuggestion(OmniboxSuggestion item)
    {
        _suggestedText = OmniboxTextBox.Text;
        _suggestedUrl = item.Url;
    }

    private string AddressDestination() => _suggestedUrl != null &&
        string.Equals(OmniboxTextBox.Text, _suggestedText, StringComparison.Ordinal)
            ? _suggestedUrl : OmniboxTextBox.Text.Trim();

    private void CloseAddressSuggestions()
    {
        OmniboxSuggestionsPopup.IsOpen = false;
        _addressRevision++;
        OmniboxSuggestionsList.ItemsSource = null;
        _editingAddress = false;
    }

    private void NavigateAddressSuggestion(OmniboxSuggestion item)
    {
        CloseAddressSuggestions();
        SetAddressText(item.Address);
        _activeTab?.Navigate(item.Url);
        _activeTab?.WebView.Focus();
    }

    private void OmniboxSuggestion_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var target = ItemsControl.ContainerFromElement(OmniboxSuggestionsList, e.OriginalSource as DependencyObject);
        if (target is ListBoxItem { DataContext: OmniboxSuggestion item })
        {
            e.Handled = true;
            NavigateAddressSuggestion(item);
        }
    }
}
