using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Vela.Services;

namespace Vela;

public partial class SiteSettingsWindow : Window
{
    private sealed record PermissionRow(CoreWebView2PermissionKind Kind, string Name, CoreWebView2PermissionState State)
    {
        public string Group { get; } = Guid.NewGuid().ToString("N");
        public bool Ask => State == CoreWebView2PermissionState.Default;
        public bool Allow => State == CoreWebView2PermissionState.Allow;
        public bool Deny => State == CoreWebView2PermissionState.Deny;
    }
    private readonly Func<CoreWebView2Profile> _profile;
    private readonly Func<string, Task<bool>> _clearData;
    private string? _origin;
    private bool _busy;
    public SiteSettingsWindow(Func<CoreWebView2Profile> profile, string? currentOrigin, Func<string, Task<bool>> clearData)
    {
        _profile = profile;
        _clearData = clearData;
        InitializeComponent();
        WindowSizing.Install(this);
        OriginBox.Text = currentOrigin ?? "";
        Loaded += async (_, _) => await LoadOriginAsync();
    }

    private async Task LoadOriginAsync()
    {
        if (_busy) return;
        _busy = true;
        PermissionsList.IsEnabled = false;
        try
        {
            // Profile wrappers are tied to their originating control, which site cleanup can recreate.
            var permissions = await _profile().GetNonDefaultPermissionSettingsAsync();
            OriginsList.ItemsSource = permissions.Select(p => p.PermissionOrigin).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            _origin = SiteDataService.Origin(OriginBox.Text);
            if (_origin is null)
            {
                PermissionsList.ItemsSource = null;
                ClearDataButton.IsEnabled = false;
                SelectedOriginText.Text = "Introdu adresa unui site HTTP sau HTTPS.";
                return;
            }
            OriginBox.Text = SelectedOriginText.Text = _origin;
            var kinds = new[] { CoreWebView2PermissionKind.Camera, CoreWebView2PermissionKind.Microphone,
                CoreWebView2PermissionKind.Geolocation, CoreWebView2PermissionKind.Notifications, CoreWebView2PermissionKind.ClipboardRead }
                .Concat(permissions.Where(p => SiteDataService.Origin(p.PermissionOrigin) == _origin).Select(p => p.PermissionKind)).Distinct();
            PermissionsList.ItemsSource = kinds.Select(kind => new PermissionRow(kind, PermissionName(kind),
                permissions.FirstOrDefault(p => SiteDataService.Origin(p.PermissionOrigin) == _origin && p.PermissionKind == kind)?.PermissionState
                    ?? CoreWebView2PermissionState.Default)).ToArray();
            ClearDataButton.IsEnabled = true;
        }
        catch (Exception ex) { Log.Write("Read site permissions: " + ex); StatusText.Text = "Permisiunile nu au putut fi încărcate: " + ex.Message; }
        finally { _busy = false; PermissionsList.IsEnabled = true; }
    }
    internal static string PermissionName(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Camera => "Cameră",
        CoreWebView2PermissionKind.Microphone => "Microfon",
        CoreWebView2PermissionKind.Geolocation => "Locație",
        CoreWebView2PermissionKind.Notifications => "Notificări",
        CoreWebView2PermissionKind.ClipboardRead => "Citirea clipboardului",
        _ => kind.ToString()
    };
    private async void Permission_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _origin is null || sender is not RadioButton { DataContext: PermissionRow row, Tag: string raw } ||
            !Enum.TryParse<CoreWebView2PermissionState>(raw, out var state)) return;
        var origin = _origin;
        _busy = true;
        PermissionsList.IsEnabled = false;
        try
        {
            await _profile().SetPermissionStateAsync(row.Kind, origin, state);
            StatusText.Text = "Permisiune salvată. Reîncarcă site-ul pentru a opri și accesul deja în curs.";
        }
        catch (Exception ex) { Log.Write("Set site permission: " + ex); StatusText.Text = "Modificarea nu a putut fi salvată: " + ex.Message; }
        finally { _busy = false; PermissionsList.IsEnabled = true; await LoadOriginAsync(); }
    }
    private async void LoadOrigin_Click(object sender, RoutedEventArgs e) => await LoadOriginAsync();
    private async void Origin_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await LoadOriginAsync(); } }
    private async void SelectOrigin_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string origin }) { OriginBox.Text = origin; await LoadOriginAsync(); } }
    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _origin is null) return;
        var origin = _origin;
        if (!MessageDialog.Confirm(this, "Datele site-ului",
                "Ștergi cookie-urile și datele locale pentru:\n\n" + origin +
                "\n\nTaburile acestui site vor fi reîncărcate. Poți fi deconectat de pe site.",
                "Șterge datele", "Anulează")) return;
        _busy = true;
        ClearDataButton.IsEnabled = false;
        try { StatusText.Text = await _clearData(origin) ? "Datele site-ului au fost șterse." : "Ștergerea a fost anulată."; }
        catch (Exception ex) { Log.Write("Clear site data: " + ex); StatusText.Text = "Datele nu au putut fi șterse: " + ex.Message; }
        finally { _busy = false; ClearDataButton.IsEnabled = true; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
