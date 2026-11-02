using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private SiteSettingsWindow? _siteSettingsWindow;
    private readonly Queue<string> _externalUrls = new();
    private bool _openingExternal;

    public void OpenExternalUrls(IEnumerable<string> urls, bool activate = true)
    {
        if (_shuttingDown) return;
        foreach (var url in BrowserInstance.NavigationTargets(urls)) _externalUrls.Enqueue(url);
        if (activate)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        _ = OpenQueuedExternalUrlsAsync();
    }

    private async Task OpenQueuedExternalUrlsAsync()
    {
        if (!_sessionReady || _openingExternal || _shuttingDown) return;
        _openingExternal = true;
        try
        {
            while (_externalUrls.TryDequeue(out var url) && !_shuttingDown)
            {
                if (_tabs.Count == 1 && _active is { IsInternalPage: true } blank)
                {
                    // Startup restoration may still be creating the first native controller.
                    // Wait for the existing initialization instead of dropping the external URL.
                    if (await EnsureViewAsync(blank) is null) throw new InvalidOperationException("Tabul inițial nu a putut porni.");
                    if (ReferenceEquals(_active, blank) && blank.IsInternalPage) await NavigateActiveAsync(url);
                    else await AddTabAsync(url);
                }
                else await AddTabAsync(url);
            }
        }
        catch (Exception ex) { Log.Write("Open external link: " + ex); Status("Linkul extern nu a putut fi deschis."); }
        finally { _openingExternal = false; }
    }

    private void SiteSettings_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenSiteSettings();
    }

    private void OpenSiteSettings()
    {
        if (_siteSettingsWindow is { IsLoaded: true }) { _siteSettingsWindow.Activate(); return; }
        var core = _active?.View?.CoreWebView2;
        if (core is null) { Status("Așteaptă încărcarea tabului înainte de a administra permisiunile."); return; }
        var window = new SiteSettingsWindow(() => _active?.View?.CoreWebView2?.Profile ??
            throw new InvalidOperationException("Profilul browserului nu este disponibil momentan."), SiteDataService.Origin(_active?.Url), ClearSiteDataAsync)
        { Owner = _settingsWindow is { IsLoaded: true } ? _settingsWindow : this };
        _siteSettingsWindow = window;
        window.Closed += (_, _) => _siteSettingsWindow = null;
        window.ShowDialog();
    }

    private async Task<bool> ClearSiteDataAsync(string origin)
    {
        if (_env is null) return false;
        origin = SiteDataService.Origin(origin) ?? throw new ArgumentException("Adresa site-ului nu este validă.");
        var related = Application.Current.Windows.OfType<MainWindow>().Where(w => w.IsPrivate == IsPrivate)
            .SelectMany(w => w._tabs.Where(t => SiteDataService.Origin(t.Url) == origin).Select(t => (Window: w, Tab: t))).ToArray();
        foreach (var group in related.GroupBy(item => item.Window))
            if (!group.Key.ConfirmDownloadCloseForSite(group.Select(item => item.Tab)) ||
                !await group.Key.ConfirmUnsavedCloseAsync(group.Select(item => item.Tab))) return false;
        // An on-demand blank view remains alive while every affected site renderer is removed.
        // No page can immediately recreate its storage in the middle of the clear operation.
        using var utility = new WebView2 { Visibility = Visibility.Collapsed };
        ContentHost.Children.Add(utility);
        try
        {
            await utility.EnsureCoreWebView2Async(_env, ControllerOptions());
            foreach (var (window, tab) in related) window.DiscardTab(tab, force: true);
            await SiteDataService.ClearOriginAsync(utility.CoreWebView2, origin);
        }
        finally
        {
            ContentHost.Children.Remove(utility);
            foreach (var window in related.Select(item => item.Window).Distinct())
                if (window._active is { View: null } active && !window._shuttingDown) await window.ActivateTabAsync(active);
        }
        return true;
    }

    private bool ConfirmDownloadCloseForSite(IEnumerable<BrowserTab> tabs)
    {
        if (!tabs.Any(t => HasPendingDownload(t))) return true;
        return MessageBox.Show(_siteSettingsWindow ?? (Window)this,
            "Site-ul are descărcări în curs sau în pauză. Ștergerea datelor le poate anula. Continui?", "Vela — descărcări",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async void BrowserTool(string action)
    {
        try
        {
            switch (action)
            {
                case "permissions": OpenSiteSettings(); break;
                case "import": await ImportBookmarksAsync(); break;
                case "export": await ExportBookmarksAsync(); break;
                case "library": OpenLibrary("bookmarks"); break;
                case "clear-passwords": await ClearAutofillAsync(passwords: true); break;
                case "clear-autofill": await ClearAutofillAsync(passwords: false); break;
                default:
                    if (action.StartsWith("release:", StringComparison.Ordinal)) await AddTabAsync(action[8..]);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Browser tool: " + ex);
            MessageBox.Show(_settingsWindow ?? (Window)this, ex.Message, "Vela", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ImportBookmarksAsync()
    {
        var owner = _settingsWindow ?? (Window)this;
        var dialog = new OpenFileDialog { Title = "Importă favorite din HTML", Filter = "Favorite HTML|*.html;*.htm", CheckFileExists = true };
        if (dialog.ShowDialog(owner) != true) return;
        var result = await Task.Run(() =>
        {
            if (new FileInfo(dialog.FileName).Length > BookmarkHtml.MaxFileBytes) throw new InvalidDataException("Fișierul depășește limita de 16 MB.");
            return _bookmarks.Import(File.ReadAllText(dialog.FileName));
        });
        foreach (var window in Application.Current.Windows.OfType<MainWindow>()) window.PopulateMenu();
        MessageBox.Show(owner, $"Adăugate: {result.Added}\nDeja existente: {result.Duplicates}\nLinkuri neacceptate: {result.Skipped}",
            "Vela — import favorite", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ExportBookmarksAsync()
    {
        var owner = _settingsWindow ?? (Window)this;
        var dialog = new SaveFileDialog { Title = "Exportă favorite în HTML", Filter = "Favorite HTML|*.html", DefaultExt = ".html", FileName = "Vela-favorite.html" };
        if (dialog.ShowDialog(owner) != true) return;
        var bookmarks = _bookmarks.All;
        await Task.Run(() => AtomicFile.Write(dialog.FileName, BookmarkHtml.Export(bookmarks)));
        MessageBox.Show(owner, "Favoritele au fost exportate în fișierul ales.", "Vela", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
