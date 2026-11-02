using System.Windows;
using Microsoft.Web.WebView2.Core;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private bool _applyingSiteZoom;
    private readonly Dictionary<string, double> _privateZoom = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _privateShields = new(StringComparer.OrdinalIgnoreCase);
    private void ApplyShieldPreference(Vela.Models.BrowserTab tab)
    {
        var origin = SiteDataService.Origin(tab.Url);
        tab.ShieldEnabled = origin is not null && IsPrivate && _privateShields.TryGetValue(origin, out var enabled) ? enabled : SitePreferencesStore.Get(origin).ShieldEnabled;
    }
    private string ShieldInjectionScript()
    {
        var disabled = SitePreferencesStore.DisabledOrigins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (IsPrivate) foreach (var (origin, enabled) in _privateShields) { if (enabled) disabled.Remove(origin); else disabled.Add(origin); }
        return "(()=>{if(" + System.Text.Json.JsonSerializer.Serialize(disabled) + ".includes(location.origin))return;" + _blocker.InjectionScript + "})();";
    }
    private async Task RefreshShieldInjectionsAsync()
    {
        foreach (var window in Application.Current.Windows.OfType<MainWindow>().Where(w => !IsPrivate || ReferenceEquals(w, this)))
            foreach (var tab in window._tabs)
            {
                window.ApplyShieldPreference(tab);
                if (tab.View?.CoreWebView2 is not { } core) continue;
                await window.RemoveInjectionAsync(tab, core);
                if (window._settings.BlockAds) await window.AddInjectionAsync(tab, core);
            }
    }
    private void ApplySitePreferences(Vela.Models.BrowserTab tab)
    {
        var origin = SiteDataService.Origin(tab.Url);
        if (origin is null || tab.View?.CoreWebView2 is not { } core) return;
        var saved = SitePreferencesStore.Get(origin);
        ApplyShieldPreference(tab);
        _applyingSiteZoom = true;
        try { tab.View.ZoomFactor = IsPrivate && _privateZoom.TryGetValue(origin, out var zoom) ? zoom : saved.Zoom; }
        finally { _applyingSiteZoom = false; }
    }
    private void RememberZoom(Vela.Models.BrowserTab tab, double zoom)
    {
        if (_applyingSiteZoom || SiteDataService.Origin(tab.Url) is not { } origin) return;
        if (IsPrivate) _privateZoom[origin] = zoom;
        else SitePreferencesStore.SetZoom(origin, zoom);
    }
    private async Task ClearAutofillAsync(bool passwords)
    {
        if (IsPrivate) throw new InvalidOperationException("Administrează datele salvate dintr-o fereastră normală.");
        var label = passwords ? "parolele salvate" : "datele de completare automată";
        var owner = _settingsWindow ?? (Window)this;
        if (MessageBox.Show(owner, "Ștergi " + label + " din profilul Vela? Această acțiune nu se poate anula.", "Vela — date salvate",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await LibraryCore.Profile.ClearBrowsingDataAsync(passwords ? CoreWebView2BrowsingDataKinds.PasswordAutosave : CoreWebView2BrowsingDataKinds.GeneralAutofill);
        MessageBox.Show(owner, "Datele selectate au fost șterse.", "Vela", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
