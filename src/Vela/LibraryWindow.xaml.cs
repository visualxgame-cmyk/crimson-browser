using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class LibraryWindow : Window
{
    private sealed record Row(string Key, string Primary, string Secondary, string Detail, object Data);
    private sealed record ExtensionRow(CoreWebView2BrowserExtension Native, string Name, string Detail, bool Enabled);
    private readonly MainWindow _browser;
    private readonly DispatcherTimer _search = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private string _mode = "bookmarks";
    private string? _editingBookmark;
    private string? _diagnostic;
    private bool _ready, _loading, _busy;
    internal LibraryWindow(MainWindow browser, string mode)
    {
        _browser = browser;
        InitializeComponent();
        WindowSizing.Install(this);
        _ready = true;
        _search.Tick += (_, _) => { _search.Stop(); RefreshList(); };
        MainWindow.Bookmarks.Changed += BookmarksChanged;
        MainWindow.ReadingList.Changed += BookmarksChanged;
        Closed += (_, _) => { _search.Stop(); MainWindow.Bookmarks.Changed -= BookmarksChanged; MainWindow.ReadingList.Changed -= BookmarksChanged; TransferPassword.Clear(); };
        Closing += (_, e) => { if (_busy) { e.Cancel = true; StatusText.Text = "Așteaptă terminarea operației în curs."; } };
        ShowMode(mode);
    }
    internal void ShowMode(string mode)
    {
        if (!_ready || _busy) return;
        _mode = mode is "history" or "bookmarks" or "tabs" or "reading" or "diagnostics" or "backup" or "extensions" or "privacy" ? mode : "bookmarks";
        PageTitle.Text = _mode switch { "history" => "Istoric", "tabs" => "Taburi deschise", "diagnostics" => "Consum și diagnostic", "backup" => "Backup și sincronizare", "extensions" => "Extensii", "privacy" => "Date și protecție", _ => "Favorite" };
        if (_mode == "reading") PageTitle.Text = "Citește mai târziu";
        ListPage.Visibility = _mode is "history" or "bookmarks" or "tabs" or "reading" ? Visibility.Visible : Visibility.Collapsed;
        ReadingActions.Visibility = _mode == "reading" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = _mode == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = _mode == "backup" ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsPage.Visibility = _mode == "extensions" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = _mode == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        BookmarkEditor.Visibility = FolderFilter.Visibility = NewBookmarkButton.Visibility = MoveUpButton.Visibility = MoveDownButton.Visibility = _mode == "bookmarks" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPeriod.Visibility = _mode == "history" ? Visibility.Visible : Visibility.Collapsed;
        DeleteItemButton.Visibility = _mode == "tabs" ? Visibility.Collapsed : Visibility.Visible;
        SyncFolderText.Text = string.IsNullOrWhiteSpace(_browser.LibrarySettings.SyncDirectory) ? "Niciun folder ales." : _browser.LibrarySettings.SyncDirectory;
        ExtensionsToggle.IsChecked = _browser.LibrarySettings.ExtensionsEnabled;
        StatusText.Text = "";
        RefreshFolders(); RefreshList();
        if (_mode == "extensions") _ = RunAsync(RefreshExtensionsAsync);
        if (_mode is "history" or "tabs" or "bookmarks") QueryBox.Focus();
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && !_busy) { e.Handled = true; Close(); }
        else if (e.Key == Key.Enter && ItemsList.IsKeyboardFocusWithin) { e.Handled = true; Open_Click(ItemsList, new RoutedEventArgs()); }
    }
    private void Mode_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string mode }) ShowMode(mode); }
    private void BookmarkLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BookmarkEditor is null) return;
        var narrow = e.NewSize.Width < 600;
        Grid.SetRow(BookmarkEditor, narrow ? 1 : 0); Grid.SetColumn(BookmarkEditor, narrow ? 0 : 1); Grid.SetColumnSpan(BookmarkEditor, narrow ? 2 : 1);
        BookmarkEditor.Width = narrow ? double.NaN : 255;
        BookmarkEditor.MaxHeight = narrow ? Math.Max(100, e.NewSize.Height * .5) : double.PositiveInfinity;
        BookmarkEditor.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(14, 0, 0, 0);
    }
    private void Query_Changed(object sender, TextChangedEventArgs e) { if (_ready && !_loading) { _search.Stop(); _search.Start(); } }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (_ready && !_loading) RefreshList(); }
    private void BookmarksChanged() => Dispatcher.InvokeAsync(() => { if (IsLoaded && _mode is "bookmarks" or "reading") { RefreshFolders(); RefreshList(); } });
    private void RefreshFolders()
    {
        _loading = true;
        var selected = FolderFilter.SelectedItem as string;
        FolderFilter.ItemsSource = new[] { "Toate folderele" }.Concat(MainWindow.Bookmarks.All.Select(b => b.Folder.Length == 0 ? "Fără folder" : b.Folder).Distinct().Order()).ToArray();
        FolderFilter.SelectedItem = selected;
        if (FolderFilter.SelectedIndex < 0) FolderFilter.SelectedIndex = 0;
        _loading = false;
    }
    private void RefreshList()
    {
        if (!_ready || _mode is not ("history" or "bookmarks" or "tabs" or "reading")) return;
        var selectedKey = (ItemsList.SelectedItem as Row)?.Key;
        var query = QueryBox.Text.Trim();
        IEnumerable<Row> rows;
        if (_mode == "history")
        {
            var since = HistoryPeriod.SelectedIndex switch { 1 => new DateTimeOffset(DateTime.Today), 2 => DateTimeOffset.Now.AddDays(-7), 3 => DateTimeOffset.Now.AddDays(-30), _ => (DateTimeOffset?)null };
            rows = _browser.IsPrivate ? Array.Empty<Row>() : MainWindow.History.Query(query, since).Select(h => new Row(h.Url + h.VisitedAt.UtcTicks, h.Title, h.Url, h.VisitedAt.LocalDateTime.ToString("g"), h));
        }
        else if (_mode == "reading") rows = MainWindow.ReadingList.All.Where(b => b.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.IsRead).Select(b => new Row(b.Id, b.Title, b.Url, b.IsRead ? "Citit" : "Necitit", b));
        else if (_mode == "tabs") rows = _browser.LibraryTabs.Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Url.Contains(query, StringComparison.OrdinalIgnoreCase) || t.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(t => new Row(t.Id.ToString(), t.Title, t.Url, (t.GroupName.Length > 0 ? t.GroupName + " · " : "") + (t.IsPinned ? "Fixat · " : "") + (t.IsDiscarded ? "Eliberat din memorie" : t.IsSuspended ? "În repaus" : "Încărcat"), t));
        else
        {
            var folder = FolderFilter.SelectedItem as string;
            rows = MainWindow.Bookmarks.All.Where(b => (b.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Folder.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
                    (folder is null or "Toate folderele" || (folder == "Fără folder" ? b.Folder.Length == 0 : b.Folder == folder)))
                .Select(b => new Row(b.Id, b.Title, b.Url, b.Folder.Length == 0 ? "Fără folder" : b.Folder, b));
        }
        var result = rows.ToArray();
        _loading = true; ItemsList.ItemsSource = result; ItemsList.SelectedItem = result.FirstOrDefault(r => r.Key == selectedKey); _loading = false;
        StatusText.Text = _browser.IsPrivate && _mode == "history" ? "Navigarea privată nu salvează istoric." : $"{result.Length} rezultate";
    }
    private void Item_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mode != "bookmarks" || ItemsList.SelectedItem is not Row { Data: Bookmark bookmark }) return;
        _editingBookmark = bookmark.Id; BookmarkTitle.Text = bookmark.Title; BookmarkUrl.Text = bookmark.Url; BookmarkFolder.Text = bookmark.Folder;
    }
    private void NewBookmark_Click(object sender, RoutedEventArgs e)
    { _editingBookmark = null; ItemsList.SelectedIndex = -1; BookmarkTitle.Clear(); BookmarkUrl.Clear(); BookmarkFolder.Clear(); BookmarkTitle.Focus(); }
    private void SaveBookmark_Click(object sender, RoutedEventArgs e)
    {
        try { _editingBookmark = MainWindow.Bookmarks.Save(_editingBookmark, BookmarkTitle.Text, BookmarkUrl.Text, BookmarkFolder.Text); RefreshFolders(); RefreshList(); StatusText.Text = "Favorita a fost salvată."; }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not Row row) return;
        try
        {
            if (row.Data is BrowserTab tab) await _browser.ActivateLibraryTabAsync(tab);
            else await _browser.OpenLibraryAddressAsync(row.Secondary);
            Close();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not Row row || !MessageDialog.Confirm(this, "Șterge înregistrarea",
                "Ștergi înregistrarea selectată?", "Șterge", "Anulează")) return;
        try
        {
            if (row.Data is HistoryEntry history) MainWindow.History.Remove(history);
            else if (row.Data is Bookmark bookmark) (_mode == "reading" ? MainWindow.ReadingList : MainWindow.Bookmarks).Remove(bookmark.Url);
            RefreshList();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void MoveBookmark_Click(object sender, RoutedEventArgs e)
    { if (ItemsList.SelectedItem is Row { Data: Bookmark bookmark } && sender is Button { Tag: string raw } && int.TryParse(raw, out var delta)) { MainWindow.Bookmarks.Move(bookmark.Id, delta); RefreshList(); } }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();
    private void SaveReading_Click(object sender, RoutedEventArgs e) { try { _browser.SaveForLater(); RefreshList(); } catch (Exception ex) { ShowError(ex); } }
    private void ToggleReading_Click(object sender, RoutedEventArgs e)
    { try { if (ItemsList.SelectedItem is Row { Data: Bookmark item }) { MainWindow.ReadingList.SetRead(item.Id, !item.IsRead); RefreshList(); } } catch (Exception ex) { ShowError(ex); } }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
        finally { _busy = false; }
    }
    private void ShowError(Exception ex) { Log.Write("Library operation: " + ex.GetType().Name + ": " + ex.Message); StatusText.Text = ex.Message; }
    private async void Measure_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        StatusText.Text = "Se măsoară consumul timp de o secundă…";
        _diagnostic = await _browser.CaptureDiagnosticAsync();
        using var json = JsonDocument.Parse(_diagnostic); var root = json.RootElement;
        DiagnosticSummary.Text = $"RAM: {root.GetProperty("workingSetBytes").GetInt64() / 1048576d:0.0} MB\nMemorie privată: {root.GetProperty("privateBytes").GetInt64() / 1048576d:0.0} MB\nCPU: {root.GetProperty("cpuPercent").GetDouble():0.00}%\nProcese: {root.GetProperty("processes").GetArrayLength()}\nTaburi încărcate: {root.GetProperty("liveTabs")} / {root.GetProperty("tabs")}\nMotor: {root.GetProperty("engineVersion")}";
        StatusText.Text = "Măsurare finalizată. Valorile includ aplicația și procesele motorului.";
    });
    private void ExportDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnostic is null) { StatusText.Text = "Măsoară consumul înainte de export."; return; }
        var dialog = new SaveFileDialog { Filter = "Raport JSON|*.json", FileName = "Vela-diagnostic.json" };
        if (dialog.ShowDialog(this) == true) { try { AtomicFile.Write(dialog.FileName, _diagnostic); StatusText.Text = "Raport exportat."; } catch (Exception ex) { ShowError(ex); } }
    }
    private async void VerifyInstaller_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "Instalator Windows|*.exe;*.msi", Title = "Verifică un instalator fără a-l rula" };
        if (dialog.ShowDialog(this) != true) return;
        var check = await Task.Run(() => ReleaseVerification.Inspect(dialog.FileName));
        MessageDialog.Inform(this, "Verificare fișier", "SHA-256: " + check.Sha256 + "\n\n" +
            (check.TrustedSignature ? "Windows confirmă o semnătură de încredere folosind certificatele disponibile local. Verifică și numele editorului în proprietățile fișierului." : "Semnătura nu este confirmată de Windows (cod 0x" + check.TrustStatus.ToString("X8") + ").") +
            "\n\nAcest rezultat nu confirmă că fișierul este o versiune oficială Vela. Nu este configurat încă un certificat Vela.");
    });
    private async void Backup_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = new SaveFileDialog { Filter = "Backup criptat Vela|*.vela-backup", FileName = "Vela-" + DateTime.Now.ToString("yyyy-MM-dd") + ".vela-backup" };
        if (dialog.ShowDialog(this) != true) return;
        var password = TransferPassword.Password; var payload = _browser.CreateTransfer("backup");
        await Task.Run(() => EncryptedLibrary.Write(dialog.FileName, payload, password));
        StatusText.Text = "Backup criptat salvat. Păstrează parola separat; fără ea arhiva nu poate fi recuperată.";
    });
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "Backup criptat Vela|*.vela-backup" };
        if (dialog.ShowDialog(this) != true) return;
        var password = TransferPassword.Password;
        var payload = await Task.Run(() => EncryptedLibrary.Read(dialog.FileName, password));
        if (payload.Purpose != "backup") throw new InvalidDataException("Alege un backup, nu un fișier de sincronizare.");
        _browser.ValidateBackup(payload);
        if (!MessageDialog.Confirm(this, "Restaurare backup",
                "Restabilești favoritele și setările din backup? O copie criptată a stării actuale va fi păstrată pentru revenire.",
                "Restaurează", "Anulează")) return;
        var rollback = _browser.CreateTransfer("backup");
        var path = Path.Combine(AppPaths.Root, "Backups", "before-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".vela-backup");
        await Task.Run(() => EncryptedLibrary.Write(path, rollback, password));
        try { await _browser.RestoreBackupAsync(payload); }
        catch
        {
            await _browser.RestoreBackupAsync(rollback);
            throw;
        }
        RefreshFolders(); StatusText.Text = "Backup restaurat. Copia de revenire este în folderul local Backups, criptată cu aceeași parolă.";
    });
    private void ChooseSync_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Alege folderul de sincronizare Vela", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) == true) { _browser.SetSyncFolder(dialog.FolderName); SyncFolderText.Text = dialog.FolderName; }
    }
    private async void Sync_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var folder = _browser.LibrarySettings.SyncDirectory;
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Alege mai întâi folderul de sincronizare.");
        var password = TransferPassword.Password;
        var remote = await Task.Run(() => EncryptedLibrary.ReadSyncFolder(folder, password));
        var latest = remote.OrderByDescending(p => p.Preferences.ModifiedAt).FirstOrDefault();
        if (latest is not null) latest.Preferences.ApplyTo(_browser.LibrarySettings); // Validate before changing local data.
        var count = await Task.Run(() => MainWindow.Bookmarks.Merge(remote.SelectMany(p => p.Bookmarks)));
        await Task.Run(() => MainWindow.ReadingList.Merge(remote.SelectMany(p => p.ReadingList ?? [])));
        if (SyncSettingsToggle.IsChecked == true && latest is not null && latest.Preferences.ModifiedAt > _browser.LibrarySettings.PreferencesUpdatedAt)
            _browser.ApplyTransferPreferences(latest.Preferences);
        var payload = _browser.CreateTransfer("sync");
        await Task.Run(() => EncryptedLibrary.Write(Path.Combine(folder, "vela-" + EncryptedLibrary.DeviceId + ".sync"), payload, password));
        StatusText.Text = $"Sincronizare finalizată: {count} modificări îmbinate. Înregistrările de ștergere previn reapariția favoritelor eliminate.";
    });
    private void ExtensionsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_browser.IsPrivate) { ExtensionsToggle.IsChecked = false; StatusText.Text = "Extensiile se administrează în profilul normal."; return; }
        _browser.SetExtensionsEnabled(ExtensionsToggle.IsChecked == true); StatusText.Text = "Preferință salvată. Repornește Vela pentru a modifica suportul motorului.";
    }
    private async Task RefreshExtensionsAsync()
    {
        var installed = ManagedExtensions.Read();
        // WebView2 also exposes its built-in PDF/clipboard components. They are not user extensions.
        ExtensionList.ItemsSource = (await _browser.GetExtensionsAsync()).Where(e => installed.ContainsKey(e.Id)).Select(e =>
        {
            var detail = ManagedExtensions.TryGetFolder(e.Id, out var folder)
                ? SafeExtensionSummary(folder)
                : "Manifestul local nu mai este disponibil.";
            return new ExtensionRow(e, e.Name + (e.IsEnabled ? " — activă" : " — dezactivată"), detail, e.IsEnabled);
        }).ToArray();
    }
    private static string SafeExtensionSummary(string folder)
    {
        try { return ExtensionManifest.Read(folder).AccessSummary.Replace("\n", " · "); }
        catch (Exception ex) { Log.Write("Read extension manifest: " + ex.Message); return "Manifestul nu a putut fi citit."; }
    }
    private async void RefreshExtensions_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshExtensionsAsync);
    private async void InstallExtension_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = new OpenFolderDialog { Title = "Alege extensia neîmpachetată (manifest.json)" };
        if (dialog.ShowDialog(this) != true) return;
        var manifest = await Task.Run(() => ExtensionManifest.Read(dialog.FolderName));
        var prompt = $"Instalezi extensia „{manifest.Name}” ({manifest.Version})?\n\nPermisiuni declarate:\n{manifest.AccessSummary}\n\nContinuă numai dacă ai încredere în sursă.";
        if (!MessageDialog.Confirm(this, "Instalare extensie", prompt, "Instalează", "Anulează")) return;
        await _browser.InstallExtensionAsync(dialog.FolderName); await RefreshExtensionsAsync(); StatusText.Text = "Extensia este instalată în profilul normal.";
    });
    private async void ToggleExtension_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { if (ExtensionList.SelectedItem is ExtensionRow row) { await row.Native.EnableAsync(!row.Enabled); await RefreshExtensionsAsync(); } });
    private async void RemoveExtension_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ExtensionList.SelectedItem is not ExtensionRow row ||
            !MessageDialog.Confirm(this, "Dezinstalare extensie", "Dezinstalezi extensia selectată?", "Dezinstalează", "Anulează")) return;
        await row.Native.RemoveAsync();
        ManagedExtensions.Remove(row.Native.Id);
        await RefreshExtensionsAsync();
    });
    private async void SmartScreenInfo_Click(object sender, RoutedEventArgs e)
    { await _browser.OpenLibraryAddressAsync("https://learn.microsoft.com/microsoft-edge/webview2/concepts/data-privacy#smartscreen"); }
    private async void UpdateBlocklist_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        StatusText.Text = "Se descarcă și se validează lista StevenBlack/hosts…";
        var count = await Task.Run(() => BlocklistUpdates.UpdateAsync());
        await _browser.ReloadBlocklistAsync();
        StatusText.Text = $"Lista a fost actualizată: {count:N0} domenii. Copia precedentă este păstrată pentru revenire.";
    });
    private async void RollbackBlocklist_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await Task.Run(BlocklistUpdates.Rollback); await _browser.ReloadBlocklistAsync();
        StatusText.Text = "Lista precedentă a fost restaurată. Filtrele proprii au fost păstrate.";
    });
    private async void ClearRange_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ClearHistoryToggle.IsChecked != true && ClearCookiesToggle.IsChecked != true && ClearStorageToggle.IsChecked != true) throw new ArgumentException("Selectează cel puțin un tip de date.");
        if (!MessageDialog.Confirm(this, "Ștergere date",
                "Ștergi tipurile de date selectate pentru intervalul ales? Cookie-urile șterse te pot deconecta de pe site-uri.",
                "Șterge datele", "Anulează")) return;
        var since = ClearPeriod.SelectedIndex switch { 0 => DateTimeOffset.UtcNow.AddHours(-1), 1 => DateTimeOffset.UtcNow.AddDays(-1), 2 => DateTimeOffset.UtcNow.AddDays(-7), _ => DateTimeOffset.UnixEpoch };
        StatusText.Text = await _browser.ClearRangeAsync(since, ClearHistoryToggle.IsChecked == true, ClearCookiesToggle.IsChecked == true, ClearStorageToggle.IsChecked == true)
            ? "Datele selectate au fost șterse." : "Ștergerea a fost anulată.";
    });
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
