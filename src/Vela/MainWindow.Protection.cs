using System.Windows;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private readonly HashSet<Guid> _closingTabs = new();
    private readonly HashSet<Guid> _discardChecks = new();
    private bool _windowClosePending;
    private bool _windowCloseApproved;
    private readonly SemaphoreSlim _liveLimitGate = new(1, 1);

    private async Task<bool> TryDiscardTabAsync(BrowserTab tab)
    {
        if (_shuttingDown || ReferenceEquals(tab, _active) || IsHardMemoryProtected(tab) ||
            tab.View?.CoreWebView2 is not { } core || !_discardChecks.Add(tab.Id)) return false;
        try
        {
            if (tab.IsSuspended) { core.Resume(); tab.IsSuspended = false; }
            tab.HasUnloadHandler = await FormStateTracker.HasUnloadHandlerAsync(core);
            if (!_tabs.Contains(tab) || ReferenceEquals(tab, _active) || tab.IsLoading || IsMemoryProtected(tab) ||
                !ReferenceEquals(tab.View?.CoreWebView2, core)) return false;
            DiscardTab(tab);
            return tab.View is null;
        }
        catch (Exception ex)
        {
            // An unresponsive page must not lose work merely because the safety check failed.
            Log.Write("Check tab before memory eviction: " + ex.Message);
            return false;
        }
        finally { _discardChecks.Remove(tab.Id); }
    }

    private async Task<bool> ConfirmUnsavedCloseAsync(IEnumerable<BrowserTab> tabs)
    {
        foreach (var tab in tabs.ToArray())
        {
            if (tab.View?.CoreWebView2 is not { } core || tab.IsInternalPage) continue;
            var hasWork = tab.HasUnsavedChanges;
            var checkFailed = false;
            if (!hasWork)
            {
                try
                {
                    if (tab.IsSuspended) { core.Resume(); tab.IsSuspended = false; }
                    hasWork = await FormStateTracker.HasUnloadHandlerAsync(core);
                }
                catch (Exception ex) { Log.Write("Check page before close: " + ex.Message); checkFailed = true; }
            }
            if (!hasWork && !checkFailed) continue;
            var text = checkFailed
                ? "Starea paginii nu a putut fi verificată. Închiderea poate pierde modificări nesalvate."
                : "Pagina conține modificări nesalvate sau a înregistrat o protecție la închidere.";
            if (!MessageDialog.Confirm(this, "Protecția modificărilor",
                    text + "\n\n" + tab.Title, "Închide pagina", "Anulează")) return false;
        }
        return true;
    }

    private async Task RequestCloseTabAsync(BrowserTab tab)
    {
        if (_shuttingDown || _windowClosePending || !_tabs.Contains(tab) || !_closingTabs.Add(tab.Id)) return;
        try
        {
            if (_tabs.Count == 1) { Close(); return; }
            await WaitForViewInitializationAsync();
            if (ConfirmDownloadClose(tab) && await ConfirmUnsavedCloseAsync([tab]) && !_shuttingDown && _tabs.Contains(tab))
                CloseTabConfirmed(tab);
        }
        catch (Exception ex) { Log.Write("Close tab: " + ex); Status("Tabul nu a putut fi închis în siguranță."); }
        finally { _closingTabs.Remove(tab.Id); }
    }

    private async Task RequestWindowCloseAsync()
    {
        if (_windowClosePending || _shuttingDown) return;
        _windowClosePending = true;
        try
        {
            await WaitForViewInitializationAsync();
            if (!ConfirmDownloadClose() || !await ConfirmUnsavedCloseAsync(_tabs)) return;
            _windowCloseApproved = true;
            Close();
        }
        catch (Exception ex) { Log.Write("Close window: " + ex); Status("Fereastra nu a putut fi închisă în siguranță."); }
        finally { _windowClosePending = false; }
    }
    private async Task WaitForViewInitializationAsync()
    {
        // A controller can exist before its scripts/callbacks are configured. Closing
        // waits for that bounded creation operation instead of querying or disposing it halfway through.
        await _viewGate.WaitAsync();
        _viewGate.Release();
    }
}
