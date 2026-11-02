using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private LibraryWindow? _libraryWindow;
    private bool _applyingTransferredPreferences;
    private static bool _environmentExtensionsEnabled;
    internal Settings LibrarySettings => _settings.Clone();
    internal static BookmarksStore Bookmarks => _bookmarks;
    internal static HistoryStore History => _history;
    internal static BookmarksStore ReadingList { get; } = new("reading-list.json");
    internal void SaveForLater()
    {
        if (_active is not { } tab || tab.IsInternalPage && tab.Reader is null) throw new InvalidOperationException("Deschide o pagină web pentru a o salva.");
        var url = tab.Reader?.SourceUrl ?? tab.Url;
        if (!ReadingList.Contains(url)) ReadingList.Save(null, tab.Title, url, "");
    }
    internal CoreWebView2 LibraryCore => _active?.View?.CoreWebView2 ?? throw new InvalidOperationException("Așteaptă încărcarea paginii.");
    internal IReadOnlyList<BrowserTab> LibraryTabs => Application.Current.Windows.OfType<MainWindow>()
        .Where(w => w.IsPrivate == IsPrivate).SelectMany(w => w._tabs).ToArray();
    internal Task OpenLibraryAddressAsync(string url) => AddTabAsync(url);
    internal async Task ActivateLibraryTabAsync(BrowserTab tab)
    {
        var owner = tab.Owner;
        if (owner is null || owner.IsPrivate != IsPrivate) return;
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        owner.Activate(); await owner.ActivateTabAsync(tab);
    }
    private void Library_Click(object sender, RoutedEventArgs e) => OpenLibrary((sender as FrameworkElement)?.Tag as string ?? "bookmarks");
    internal void OpenLibrary(string mode)
    {
        MenuPopup.IsOpen = false;
        if (_libraryWindow is { IsLoaded: true }) { _libraryWindow.ShowMode(mode); _libraryWindow.Activate(); return; }
        var window = new LibraryWindow(this, mode) { Owner = _settingsWindow is { IsLoaded: true } ? _settingsWindow : this };
        _libraryWindow = window;
        window.Closed += (_, _) => _libraryWindow = null;
        window.Show();
    }
    internal LibraryTransfer CreateTransfer(string purpose)
    {
        var payload = new LibraryTransfer(1, purpose, DateTimeOffset.UtcNow, _bookmarks.Snapshot.ToList(), SharedPreferences.From(_settings)) { ReadingList = ReadingList.Snapshot.ToList() };
        if (purpose != "backup") return payload;
        byte[]? image = null;
        if (_settings.NewTabBackgroundImage is { Length: > 0 } name && Path.GetFileName(name) == name)
        {
            var path = Path.Combine(AppPaths.Backgrounds, name);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 12 * 1024 * 1024) throw new InvalidDataException("Imaginea de fundal depășește limita de backup de 12 MB.");
                image = File.ReadAllBytes(path);
            }
        }
        return payload with { LocalBackup = new(_settings.Clone(), ShortcutStore.All.ToList(), SitePreferencesStore.Snapshot, image) };
    }
    internal void ValidateBackup(LibraryTransfer payload)
    {
        payload.Preferences.ApplyTo(_settings); payload.LocalBackup?.Validate();
        // Validate bookmarks with the same checks as merge, without touching the store.
        BookmarksStore.ValidateSnapshot(payload.Bookmarks);
        if (payload.ReadingList is not null) BookmarksStore.ValidateSnapshot(payload.ReadingList);
    }
    internal async Task RestoreBackupAsync(LibraryTransfer payload)
    {
        ValidateBackup(payload);
        if (payload.LocalBackup is { } backup)
        {
            var next = backup.Settings.Clone();
            if (backup.Background is { Length: > 0 } bytes && next.NewTabBackgroundImage is { } name)
            {
                var path = Path.Combine(AppPaths.Backgrounds, "restored-" + Guid.NewGuid().ToString("N") + Path.GetExtension(name));
                await File.WriteAllBytesAsync(path, bytes); next.NewTabBackgroundImage = Path.GetFileName(path);
            }
            else if (next.NewTabBackgroundMode == "image") { next.NewTabBackgroundImage = null; next.NewTabBackgroundMode = "theme"; }
            ShortcutStore.Restore(backup.Shortcuts); SitePreferencesStore.Restore(backup.Sites);
            CommitSettings(next); await RefreshShieldInjectionsAsync();
        }
        else ApplyTransferPreferences(payload.Preferences);
        _bookmarks.Merge(payload.Bookmarks, replace: true);
        if (payload.ReadingList is not null) ReadingList.Merge(payload.ReadingList, replace: true);
    }
    internal void ApplyTransferPreferences(SharedPreferences preferences)
    {
        _applyingTransferredPreferences = true;
        try { CommitSettings(preferences.ApplyTo(_settings)); }
        finally { _applyingTransferredPreferences = false; }
    }
    internal void SetSyncFolder(string path)
    { var next = _settings.Clone(); next.SyncDirectory = Path.GetFullPath(path); CommitSettings(next); }
    internal void SetExtensionsEnabled(bool enabled)
    { var next = _settings.Clone(); next.ExtensionsEnabled = enabled; CommitSettings(next); }
    internal async Task ReloadBlocklistAsync()
    {
        _blocker.Load(); await RefreshShieldInjectionsAsync();
    }
    internal Task<IReadOnlyList<CoreWebView2BrowserExtension>> GetExtensionsAsync()
    {
        if (IsPrivate || !_environmentExtensionsEnabled) throw new InvalidOperationException("Activează suportul pentru extensii în profilul normal și repornește Vela.");
        return ReadExtensionsAsync();
    }
    private async Task<IReadOnlyList<CoreWebView2BrowserExtension>> ReadExtensionsAsync() => (await LibraryCore.Profile.GetBrowserExtensionsAsync()).ToArray();
    internal async Task InstallExtensionAsync(string source)
    {
        await GetExtensionsAsync();
        source = Path.GetFullPath(source);
        if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Folderul extensiei nu poate fi o legătură.");
        if (!File.Exists(Path.Combine(source, "manifest.json"))) throw new InvalidDataException("Folderul ales nu conține manifest.json.");
        var managedRoot = Path.Combine(AppPaths.Root, "Extensions");
        var target = Path.Combine(managedRoot, "ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        long size = 0;
        var count = 0;
        // Reject links instead of following them outside the explicitly selected package.
        void Copy(string directory, string destination, int depth = 0)
        {
            if (depth > 32) throw new InvalidDataException("Extensia conține prea multe niveluri de directoare.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Extensia conține legături de fișiere sau directoare.");
                var next = Path.Combine(destination, Path.GetFileName(path));
                if (attributes.HasFlag(FileAttributes.Directory)) { Directory.CreateDirectory(next); Copy(path, next, depth + 1); }
                else
                {
                    if (++count > 10000 || (size += new FileInfo(path).Length) > 64 * 1024 * 1024) throw new InvalidDataException("Pachetul extensiei este prea mare.");
                    File.Copy(path, next);
                }
            }
        }
        try
        {
            await Task.Run(() => Copy(source, target));
            var installed = await LibraryCore.Profile.AddBrowserExtensionAsync(target);
            try { ManagedExtensions.Add(installed.Id, target); }
            catch { await installed.RemoveAsync(); throw; }
        }
        catch
        {
            // This directory was created above and never contains followed links.
            if (Path.GetDirectoryName(Path.GetFullPath(target)) == Path.GetFullPath(managedRoot)) Directory.Delete(target, true);
            throw;
        }
    }
    internal async Task<bool> ClearRangeAsync(DateTimeOffset since, bool history, bool cookies, bool storage)
    {
        var windows = Application.Current.Windows.OfType<MainWindow>().Where(w => w.IsPrivate == IsPrivate).ToArray();
        var affected = windows.SelectMany(w => w._tabs.Select(t => (Window: w, Tab: t))).Where(p => !p.Tab.IsInternalPage).ToArray();
        if (cookies || storage)
            foreach (var window in windows)
                if (!window.ConfirmDownloadClose() || !await window.ConfirmUnsavedCloseAsync(window._tabs)) return false;
        using var utility = new WebView2 { Visibility = Visibility.Collapsed };
        ContentHost.Children.Add(utility);
        try
        {
            await utility.EnsureCoreWebView2Async(_env, ControllerOptions());
            if (cookies || storage) foreach (var (window, tab) in affected) window.DiscardTab(tab, force: true);
            var kinds = (CoreWebView2BrowsingDataKinds)0;
            if (history) kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
            if (cookies) kinds |= CoreWebView2BrowsingDataKinds.Cookies;
            if (storage) kinds |= CoreWebView2BrowsingDataKinds.AllDomStorage | CoreWebView2BrowsingDataKinds.DiskCache;
            if (kinds != 0) await utility.CoreWebView2.Profile.ClearBrowsingDataAsync(kinds, since.UtcDateTime, DateTime.UtcNow);
            if (history && !IsPrivate) _history.RemoveSince(since);
        }
        finally
        {
            ContentHost.Children.Remove(utility);
            if (cookies || storage)
                foreach (var window in windows)
                    if (window._active is { View: null } active && !window._shuttingDown) await window.ActivateTabAsync(active);
        }
        return true;
    }
}
