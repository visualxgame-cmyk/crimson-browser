using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private sealed record AddressSuggestion(string Title, string Url, string Source);
    private readonly DispatcherTimer _suggestionTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Guid _sessionWindowId = Guid.NewGuid();
    private bool _sessionReady;
    private bool _settingOmnibox;
    private sealed record DownloadTracking(BrowserTab Tab, EventHandler<object> Handler, EventHandler<object> Progress);
    private readonly Dictionary<CoreWebView2DownloadOperation, DownloadTracking> _downloads = new();

    private void InitializeBrowsingFeatures()
    {
        AttachDownloadsPanel();
        _suggestionTimer.Tick += (_, _) => RefreshSuggestions();
        _sessionTimer.Tick += (_, _) => { _sessionTimer.Stop(); SaveSession(); };
        ShortcutStore.Changed += BroadcastShortcuts;
        Closed += (_, _) =>
        {
            _suggestionTimer.Stop();
            _sessionTimer.Stop();
            ShortcutStore.Changed -= BroadcastShortcuts;
            foreach (var (operation, tracking) in _downloads) { operation.StateChanged -= tracking.Handler; operation.BytesReceivedChanged -= tracking.Progress; }
            _downloads.Clear();
            if (!IsPrivate) SessionStore.CloseWindow(_sessionWindowId);
        };
    }

    private void ScheduleSessionSave()
    {
        if (!_sessionReady || _shuttingDown) return;
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void QueueSuggestions()
    {
        _suggestionTimer.Stop();
        if (_settingOmnibox || !Omnibox.IsKeyboardFocusWithin) return;
        if (string.IsNullOrWhiteSpace(Omnibox.Text)) { CloseSuggestions(); return; }
        _suggestionTimer.Start();
    }

    private void RefreshSuggestions()
    {
        _suggestionTimer.Stop();
        if (!Omnibox.IsKeyboardFocusWithin || _shuttingDown) return;
        var query = Omnibox.Text.Trim();
        if (query.Length == 0) { CloseSuggestions(); return; }
        var results = new List<AddressSuggestion>(8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bookmark in _bookmarks.All)
        {
            if (results.Count == 8) break;
            if (!IsInternalUrl(bookmark.Url) && (bookmark.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                bookmark.Url.Contains(query, StringComparison.OrdinalIgnoreCase)) && seen.Add(bookmark.Url))
                results.Add(new(bookmark.Title, bookmark.Url, "Favorită"));
        }
        foreach (var entry in IsPrivate ? Array.Empty<HistoryEntry>() : _history.Search(query, 16))
        {
            if (results.Count == 8) break;
            if (!IsInternalUrl(entry.Url) && seen.Add(entry.Url)) results.Add(new(entry.Title, entry.Url, "Istoric"));
        }
        SuggestionsList.ItemsSource = results;
        SuggestionsList.SelectedIndex = -1;
        SuggestionsPopup.IsOpen = results.Count > 0;
    }

    private void CloseSuggestions()
    {
        _suggestionTimer.Stop();
        SuggestionsPopup.IsOpen = false;
        SuggestionsList.SelectedIndex = -1;
    }

    private bool HandleSuggestionKey(Key key)
    {
        if (key == Key.Escape) { CloseSuggestions(); return false; }
        if (key == Key.Down && !SuggestionsPopup.IsOpen && Omnibox.Text.Trim().Length > 0) RefreshSuggestions();
        if (!SuggestionsPopup.IsOpen) return false;
        if (key is Key.Down or Key.Up)
        {
            var count = SuggestionsList.Items.Count;
            SuggestionsList.SelectedIndex = key == Key.Down
                ? (SuggestionsList.SelectedIndex + 1) % count
                : (SuggestionsList.SelectedIndex <= 0 ? count - 1 : SuggestionsList.SelectedIndex - 1);
            SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
            return true;
        }
        if (key == Key.Enter && SuggestionsList.SelectedItem is AddressSuggestion suggestion)
        {
            AcceptSuggestion(suggestion);
            return true;
        }
        return false;
    }

    private void AcceptSuggestion(AddressSuggestion suggestion)
    {
        CloseSuggestions();
        Keyboard.ClearFocus();
        _ = NavigateActiveAsync(suggestion.Url);
    }

    private void Suggestion_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AddressSuggestion suggestion })
        {
            e.Handled = true;
            AcceptSuggestion(suggestion);
        }
    }

    private async Task ShowFindAsync()
    {
        CloseSuggestions();
        MenuPopup.IsOpen = false;
        if (_active?.View?.CoreWebView2 is not { } core || _env is null) return;
        try
        {
            _active.View.Focus();
            var options = _env.CreateFindOptions();
            options.FindTerm = "";
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = false;
            await core.Find.StartAsync(options);
        }
        catch (Exception ex)
        {
            Log.Write("Find in page: " + ex.Message);
            Status("Căutarea în pagină nu este disponibilă. Verifică actualizarea WebView2 Runtime.");
        }
    }

    private void Find_Click(object sender, RoutedEventArgs e) => _ = ShowFindAsync();

    private bool IsMemoryProtected(BrowserTab tab) => tab.HasUnloadHandler || IsHardMemoryProtected(tab);
    private bool IsHardMemoryProtected(BrowserTab tab) => tab.HasUnsavedChanges || tab.HasActiveMedia || tab.HasActiveSignIn || SignInProtection.IsActive(tab.Url) ||
        _windowClosePending || _closingTabs.Contains(tab.Id) || TabProtectionStore.Contains(tab.Url) || HasPendingDownload(tab) || HasPendingPermission(tab);

    private bool HasPendingDownload(BrowserTab? tab = null) => _downloads.Any(pair =>
        (tab is null || ReferenceEquals(pair.Value.Tab, tab)) &&
        (pair.Key.State == CoreWebView2DownloadState.InProgress || pair.Key.CanResume));

    private void TrackDownload(BrowserTab tab, CoreWebView2DownloadOperation operation)
    {
        TrackDownloadView(operation);
        EventHandler<object> handler = (_, _) =>
        {
            if (_downloadViews.TryGetValue(operation, out var item)) item.Refresh();
            if (operation.State == CoreWebView2DownloadState.Completed)
            {
                Status("Descărcat: " + Path.GetFileName(operation.ResultFilePath), 8);
                DetachDownload(operation);
            }
            else if (operation.State == CoreWebView2DownloadState.Interrupted)
            {
                Status(operation.CanResume ? "Descărcare întreruptă sau în pauză. O poți relua din Descărcări."
                    : "Descărcare oprită: " + operation.InterruptReason, 8);
                if (!operation.CanResume) DetachDownload(operation);
            }
            UpdateDownloadIndicator();
        };
        EventHandler<object> progress = (_, _) => QueueDownloadProgress();
        _downloads.Add(operation, new(tab, handler, progress));
        operation.StateChanged += handler;
        operation.BytesReceivedChanged += progress;
        UpdateDownloadIndicator();
    }

    private bool ConfirmDownloadClose(BrowserTab? tab = null) => !HasPendingDownload(tab) ||
        MessageDialog.Confirm(this, "Descărcări în curs",
            "Există descărcări în curs sau în pauză. Închiderea le poate anula.",
            "Închide oricum", "Anulează");

    private void ForgetDownloads(BrowserTab tab)
    {
        foreach (var operation in _downloads.Where(pair => ReferenceEquals(pair.Value.Tab, tab)).Select(pair => pair.Key).ToArray())
        {
            DetachDownload(operation);
        }
        UpdateDownloadIndicator();
    }

    private void RefreshProtectionMenu()
    {
        var host = TabProtectionStore.Host(_active?.Url ?? "");
        ProtectSiteToggle.IsEnabled = host.Length > 0;
        ProtectSiteLabel.Text = host.Length == 0 ? "Nu adormi acest site" : "Nu adormi: " + host;
        ProtectSiteToggle.IsChecked = host.Length > 0 && TabProtectionStore.Contains(_active!.Url);
        ProtectedSitesList.ItemsSource = TabProtectionStore.All;
    }

    private void ProtectSite_Click(object sender, RoutedEventArgs e)
    {
        var host = TabProtectionStore.Host(_active?.Url ?? "");
        if (host.Length > 0) SetSiteProtection(host, ProtectSiteToggle.IsChecked == true);
    }

    private void UnprotectSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string host }) SetSiteProtection(host, false);
    }

    private void SetSiteProtection(string host, bool protect)
    {
        try
        {
            TabProtectionStore.Set(host, protect);
            foreach (var window in Application.Current.Windows.OfType<MainWindow>())
            {
                if (protect)
                    foreach (var tab in window._tabs)
                        if (TabProtectionStore.Contains(tab.Url) && tab.IsSuspended)
                        {
                            tab.View?.CoreWebView2?.Resume();
                            tab.IsSuspended = false;
                        }
                if (window.MenuPopup.IsOpen) window.RefreshProtectionMenu();
            }
            Status(protect ? "Site protejat de adormire și eliberare din memorie." : "Site-ul urmează din nou politica de memorie.");
        }
        catch (Exception ex)
        {
            Log.Write("Save site protection: " + ex.Message);
            Status("Excepția nu a putut fi salvată.");
            RefreshProtectionMenu();
        }
    }

    private static string ShortcutsMessage() => JsonSerializer.Serialize(new
    {
        type = "shortcuts",
        items = ShortcutStore.All.Select(s => new { id = s.Id, name = s.Name, url = s.Url, icon = ShortcutStore.IconUrl(s) })
    });

    private void BroadcastShortcuts()
    {
        var message = ShortcutsMessage();
        foreach (var tab in _tabs)
        {
            if (!IsNewTabUrl(tab.Url) || tab.View?.CoreWebView2 is not { } core) continue;
            try { core.PostWebMessageAsJson(message); }
            catch (Exception ex) { Log.Write("Update shortcut page: " + ex.Message); }
        }
    }

    private void HandleShortcutMessage(BrowserTab tab, string type, JsonElement root)
    {
        try
        {
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
            switch (type)
            {
                case "shortcuts-ready":
                    tab.View?.CoreWebView2?.PostWebMessageAsJson(ShortcutsMessage());
                    return;
                case "shortcut-save":
                    var id = Text("id");
                    ShortcutStore.Save(id.Length == 0 ? null : id, Text("name"), Text("url"));
                    foreach (var window in Application.Current.Windows.OfType<MainWindow>())
                        foreach (var existing in window._tabs)
                            if (existing.Favicon is System.Windows.Media.Imaging.BitmapSource bitmap)
                                ShortcutStore.CacheIcon(existing.Url, bitmap);
                    break;
                case "shortcut-remove": ShortcutStore.Remove(Text("id")); break;
                case "shortcut-move":
                    if (root.TryGetProperty("direction", out var direction) && direction.TryGetInt32(out var delta))
                        ShortcutStore.Move(Text("id"), delta);
                    break;
            }
            tab.View?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "shortcut-saved" }));
        }
        catch (Exception ex)
        {
            Log.Write("Edit shortcut: " + ex.Message);
            var message = ex is ArgumentException ? ex.Message : "Modificarea nu a putut fi salvată. Încearcă din nou.";
            tab.View?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "shortcut-error", message }));
        }
    }
}
