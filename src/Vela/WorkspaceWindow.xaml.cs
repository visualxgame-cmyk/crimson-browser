using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class WorkspaceWindow : Window
{
    private sealed record Row(string Title, string Detail, object Data);
    private readonly MainWindow _browser;
    private readonly ProfileCatalog _profiles = new(AppPaths.BaseRoot);
    private string _mode = "profiles";
    private bool _busy;
    internal WorkspaceWindow(MainWindow browser, string mode)
    {
        _browser = browser; InitializeComponent(); WindowSizing.Install(this); ShowMode(mode);
        Closing += (_, e) => { if (_busy) { e.Cancel = true; Status.Text = "Așteaptă terminarea operației."; } };
    }
    internal void ShowMode(string mode)
    {
        if (_busy) return;
        _mode = mode is "import" or "resources" or "groups" ? mode : "profiles";
        NameInput.Visibility = _mode is "profiles" or "groups" ? Visibility.Visible : Visibility.Collapsed;
        NameLabel.Visibility = NameInput.Visibility;
        NameLabel.Text = _mode == "groups" ? "Numele grupului (gol pentru eliminare)" : "Numele profilului";
        Secondary.Visibility = _mode == "resources" ? Visibility.Collapsed : Visibility.Visible;
        Third.Visibility = _mode == "resources" ? Visibility.Collapsed : Visibility.Visible;
        Heading.Text = _mode switch { "import" => "Importă favorite", "resources" => "Consumul proceselor", "groups" => "Grupuri de taburi", _ => "Profiluri separate" };
        Hint.Text = _mode switch
        {
            "import" => "Citește favoritele din Chrome, Edge sau Brave fără să modifici sursa. Poți alege și un fișier Bookmarks JSON sau un export HTML din Firefox/alte browsere. Parolele și cookie-urile nu sunt importate.",
            "resources" => "Măsurare de o secundă, numai la cerere. Chromium poate partaja un proces între pagini. RAM și CPU sunt ale întregului proces, fără împărțire fictivă pe taburi. Paginile adormite nu sunt trezite.",
            "groups" => "Selectează un tab și introdu un nume de grup. Grupurile restrânse păstrează vizibil tabul activ și primul antet; paginile ascunse nu sunt încărcate. Un nume gol elimină gruparea.",
            _ => "Fiecare profil are cookie-uri, istoric, favorite, extensii și setări proprii. Deschiderea unui profil pornește sau activează procesul lui. Profilurile închise nu consumă RAM. Profil activ: " + ProfileContext.Name
        };
        Primary.Content = _mode switch { "import" => "Importă sursa selectată", "resources" => "Măsoară acum", "groups" => "Aplică grupul", _ => "Deschide profilul" };
        Secondary.Content = _mode switch { "import" => "Alege fișier…", "groups" => "Restrânge / extinde", _ => "Creează profil" };
        Third.Content = _mode switch { "import" => "Redetectează sursele", "groups" => "Actualizează lista", _ => "Redenumește profilul" };
        NameInput.Clear(); Status.Text = ""; Refresh();
    }
    private void Refresh()
    {
        Rows.ItemsSource = _mode switch
        {
            "profiles" => _profiles.All.Select(p => new Row(p.Name + (p.Id == ProfileContext.Id ? " — activ" : ""), p.Id == "default" ? "Profil principal; datele existente sunt păstrate." : "Date separate; pornește numai când îl deschizi.", p)).ToArray(),
            "import" => BrowserBookmarkImport.Discover().Select(p => new Row(p.Name, "Favorite locale, importate numai după selectare.", p)).ToArray(),
            "groups" => _browser.LibraryTabs.Select(t => new Row(t.Title, (t.GroupName.Length == 0 ? "Fără grup" : t.GroupName) + (t.IsDiscarded ? " · neîncărcat" : ""), t)).ToArray(),
            _ => Array.Empty<Row>()
        };
    }
    private void Mode_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string mode }) ShowMode(mode); }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Rows.SelectedItem is not Row row) return;
        if (row.Data is BrowserProfile profile) NameInput.Text = profile.Name;
        else if (row.Data is BrowserTab tab) NameInput.Text = tab.GroupName;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return; _busy = true; Primary.IsEnabled = Secondary.IsEnabled = Third.IsEnabled = false;
        try { await action(); } catch (Exception ex) { Status.Text = ex.Message; Log.Write("Workspace action: " + ex.Message); }
        finally { _busy = false; Primary.IsEnabled = Secondary.IsEnabled = Third.IsEnabled = true; }
    }
    private async Task ImportAsync(string path)
    {
        var parsed = await Task.Run(() => BrowserBookmarkImport.Read(path));
        var result = await Task.Run(() => MainWindow.Bookmarks.ImportEntries(parsed.Entries, parsed.Skipped));
        Status.Text = $"Import finalizat: {result.Added} adăugate, {result.Duplicates} duplicate, {result.Skipped} omise. Sursa a rămas intactă.";
    }
    private async void Primary_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_mode == "resources")
        {
            Status.Text = "Se măsoară timp de o secundă…";
            var result = await _browser.CaptureRendererUsageAsync();
            Rows.ItemsSource = result.Select(p => new Row(p.Pages, $"PID {p.ProcessId} · RAM {p.RamMB:0.0} MB · CPU {p.CpuPercent:0.00}%" + (p.Shared ? " · proces partajat" : ""), p)).ToArray();
            Status.Text = $"{result.Count} procese. CPU este normalizat la toate procesoarele logice.";
        }
        else if (Rows.SelectedItem is Row row)
        {
            if (row.Data is BrowserProfile profile) _profiles.Open(profile.Id);
            else if (row.Data is ImportSource source) await ImportAsync(source.Path);
            else if (row.Data is BrowserTab tab) { tab.Owner?.SetTabGroup(tab, NameInput.Text); Refresh(); Status.Text = "Grupul a fost aplicat."; }
        }
        else throw new InvalidOperationException("Selectează un element din listă.");
    });
    private async void Secondary_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_mode == "profiles") { var profile = _profiles.Save(null, NameInput.Text); Refresh(); Status.Text = "Profil creat: " + profile.Name + ". Selectează-l pentru deschidere."; }
        else if (_mode == "import") { var dialog = new OpenFileDialog { Title = "Alege Bookmarks JSON sau export HTML", Filter = "Favorite HTML sau JSON|*.html;*.htm;*.json;Bookmarks|Toate fișierele|*.*" }; if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileName); }
        else if (Rows.SelectedItem is Row { Data: BrowserTab tab }) { tab.Owner?.ToggleGroup(tab.GroupName); Status.Text = "Vizibilitatea grupului a fost schimbată."; }
    });
    private async void Third_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (_mode == "profiles" && Rows.SelectedItem is Row { Data: BrowserProfile profile })
        {
            var saved = _profiles.Save(profile.Id, NameInput.Text);
            if (saved.Id == ProfileContext.Id)
            {
                ProfileContext.RefreshName(saved.Name);
                foreach (var window in Application.Current.Windows.OfType<MainWindow>()) window.RefreshProfileBadge();
            }
            Status.Text = "Numele profilului a fost salvat.";
        }
        Refresh(); return Task.CompletedTask;
    });
    protected override void OnPreviewKeyDown(KeyEventArgs e) { base.OnPreviewKeyDown(e); if (e.Key == Key.Escape && !_busy) { e.Handled = true; Close(); } }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
