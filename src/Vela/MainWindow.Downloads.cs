using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Vela;

public partial class MainWindow
{
    private long _lastDownloadPaint;
    private bool _downloadPaintQueued;
    private async Task<string?> ChooseDownloadPathAsync(string name, string folder) => await Dispatcher.InvokeAsync(() =>
    {
        var dialog = new SaveFileDialog { Title = "Salvează fișierul descărcat", FileName = name, InitialDirectory = folder, Filter = "Toate fișierele|*.*", OverwritePrompt = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    });
    private void DetachDownload(CoreWebView2DownloadOperation operation)
    {
        if (!_downloads.Remove(operation, out var tracking)) return;
        operation.StateChanged -= tracking.Handler;
        operation.BytesReceivedChanged -= tracking.Progress;
    }
    private void QueueDownloadProgress()
    {
        if (_downloadPaintQueued || Stopwatch.GetElapsedTime(_lastDownloadPaint).TotalMilliseconds < 250) return;
        _downloadPaintQueued = true;
        Dispatcher.BeginInvoke(() => { _downloadPaintQueued = false; if (!_shuttingDown) UpdateDownloadIndicator(); });
    }
    private void UpdateDownloadIndicator()
    {
        _lastDownloadPaint = Stopwatch.GetTimestamp();
        var active = _downloads.Keys.Where(d => d.State == CoreWebView2DownloadState.InProgress).ToArray();
        var paused = _downloads.Keys.Count(d => d.State != CoreWebView2DownloadState.InProgress && d.CanResume);
        if (active.Length == 0 && paused == 0) { DownloadProgressText.Visibility = Visibility.Collapsed; DownloadsButton.ToolTip = "Descărcări (Ctrl+J)"; return; }
        var total = active.Sum(d => Convert.ToDouble(d.TotalBytesToReceive));
        var received = active.Sum(d => Convert.ToDouble(d.BytesReceived));
        DownloadProgressText.Text = active.Length == 0 ? $"{paused} în pauză" : total > 0 ? $"{active.Length} · {Math.Clamp(100 * received / total, 0, 100):0}%" : active.Length.ToString();
        DownloadProgressText.Visibility = Visibility.Visible;
        DownloadsButton.ToolTip = $"{active.Length} descărcări active · {paused} în pauză (Ctrl+J)";
    }
}
