using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Vela.Services;

namespace Vela;

/// <summary>One row in the themed downloads panel.</summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    public DownloadItem(CoreWebView2DownloadOperation operation, string filePath)
    {
        Operation = operation;
        FilePath = filePath;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal CoreWebView2DownloadOperation Operation { get; }

    public string FilePath { get; }

    public string Title => Path.GetFileName(FilePath);

    public double Progress { get; private set; }

    public string DetailText { get; private set; } = "";

    public bool IsActive => Operation.State == CoreWebView2DownloadState.InProgress;

    public bool IsFinished => Operation.State is CoreWebView2DownloadState.Completed or CoreWebView2DownloadState.Interrupted
        && !TryPaused;

    private bool TryPaused => Operation.State == CoreWebView2DownloadState.Interrupted &&
        Operation.CanResume && Operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused;

    public Visibility ProgressVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OpenVisibility => Operation.State == CoreWebView2DownloadState.Completed && File.Exists(FilePath)
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RevealVisibility => File.Exists(FilePath) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PauseVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ResumeVisibility => !IsActive && Operation.CanResume ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CancelVisibility => Operation.State == CoreWebView2DownloadState.InProgress ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh()
    {
        switch (Operation.State)
        {
            case CoreWebView2DownloadState.InProgress:
                var total = Convert.ToDouble(Operation.TotalBytesToReceive);
                var received = Convert.ToDouble(Operation.BytesReceived);
                Progress = total > 0 ? Math.Clamp(100 * received / total, 0, 100) : 0;
                DetailText = total > 0 ? $"{Progress:0}% · {FormatBytes(received)} din {FormatBytes(total)}" : FormatBytes(received);
                break;

            case CoreWebView2DownloadState.Completed:
                Progress = 100;
                DetailText = "Finalizat";
                break;

            default:
                DetailText = Operation.CanResume ? "În pauză — poți relua" : "Întrerupt: " + Describe(Operation.InterruptReason);
                break;
        }

        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(OpenVisibility));
        OnPropertyChanged(nameof(RevealVisibility));
        OnPropertyChanged(nameof(PauseVisibility));
        OnPropertyChanged(nameof(ResumeVisibility));
        OnPropertyChanged(nameof(CancelVisibility));
    }

    private static string Describe(CoreWebView2DownloadInterruptReason reason) => reason switch
    {
        CoreWebView2DownloadInterruptReason.UserCanceled => "anulată de utilizator",
        CoreWebView2DownloadInterruptReason.UserPaused => "în pauză",
        CoreWebView2DownloadInterruptReason.NetworkFailed => "rețeaua a căzut",
        CoreWebView2DownloadInterruptReason.NetworkTimeout => "timp de așteptare depășit",
        CoreWebView2DownloadInterruptReason.NetworkDisconnected => "conexiune întreruptă",
        CoreWebView2DownloadInterruptReason.ServerUnauthorized => "acces neautorizat",
        CoreWebView2DownloadInterruptReason.NetworkServerDown => "serverul nu răspunde",
        CoreWebView2DownloadInterruptReason.FileAccessDenied => "acces la fișier refuzat",
        CoreWebView2DownloadInterruptReason.FileNoSpace => "spațiu insuficient pe disc",
        _ => "descărcarea a eșuat",
    };

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024 * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024:0} KB",
        _ => $"{bytes:0} B",
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow
{
    private long _lastDownloadPaint;
    private bool _downloadPaintQueued;
    private readonly ObservableCollection<DownloadItem> _downloadItems = [];
    private readonly Dictionary<CoreWebView2DownloadOperation, DownloadItem> _downloadViews = [];

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
        Dispatcher.BeginInvoke(() =>
        {
            _downloadPaintQueued = false;
            if (_shuttingDown) return;
            UpdateDownloadIndicator();
            foreach (var item in _downloadItems) if (item.IsActive) item.Refresh();
        });
    }

    private void UpdateDownloadIndicator()
    {
        _lastDownloadPaint = Stopwatch.GetTimestamp();
        var active = _downloads.Keys.Where(d => d.State == CoreWebView2DownloadState.InProgress).ToArray();
        var paused = _downloads.Keys.Count(d => d.State != CoreWebView2DownloadState.InProgress && d.CanResume);
        if (active.Length == 0 && paused == 0) { DownloadProgressText.Visibility = Visibility.Collapsed; DownloadsButton.ToolTip = "Descărcări (Ctrl+J)"; }
        else
        {
            var total = active.Sum(d => Convert.ToDouble(d.TotalBytesToReceive));
            var received = active.Sum(d => Convert.ToDouble(d.BytesReceived));
            DownloadProgressText.Text = active.Length == 0 ? $"{paused} în pauză" : total > 0 ? $"{active.Length} · {Math.Clamp(100 * received / total, 0, 100):0}%" : active.Length.ToString();
            DownloadProgressText.Visibility = Visibility.Visible;
            DownloadsButton.ToolTip = $"{active.Length} descărcări active · {paused} în pauză (Ctrl+J)";
        }
        RefreshDownloadsChrome();
    }

    private void AttachDownloadsPanel()
    {
        DownloadsList.ItemsSource = _downloadItems;
        _downloadItems.CollectionChanged += (_, _) => RefreshDownloadsChrome();
        RefreshDownloadsChrome();
    }

    private void RefreshDownloadsChrome()
    {
        var active = _downloadItems.Count(item => item.IsActive);
        var finished = _downloadItems.Count(item => item.IsFinished);
        DownloadsCountText.Text = _downloadItems.Count == 0 ? "" : active > 0 ? $"{active} active" : _downloadItems.Count.ToString();
        DownloadsEmpty.Visibility = _downloadItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadsScroll.Visibility = _downloadItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DownloadsClearButton.Visibility = finished > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TrackDownloadView(CoreWebView2DownloadOperation operation)
    {
        var item = new DownloadItem(operation, operation.ResultFilePath);
        _downloadViews[operation] = item;
        _downloadItems.Insert(0, item);
        RefreshDownloadsChrome();
    }

    private void ForgetDownloadView(CoreWebView2DownloadOperation operation) => _downloadViews.Remove(operation);

    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        CloseSuggestions();
        RefreshDownloadsChrome();
        DownloadsPopup.IsOpen = true;
    }

    private void DownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = DownloadPaths.Resolve(_settings.DownloadDirectory);
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Open download folder: " + ex.Message); Status("Folderul descărcărilor nu a putut fi deschis."); }
    }

    private void DownloadsClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _downloadItems.Where(item => item.IsFinished).ToArray())
        {
            _downloadItems.Remove(item);
            ForgetDownloadView(item.Operation);
        }
        RefreshDownloadsChrome();
    }

    private static DownloadItem? DownloadItemFrom(object sender) =>
        sender is FrameworkElement { Tag: DownloadItem item } ? item : null;

    private void DownloadOpen_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadItemFrom(sender) is not { } item) return;
        try
        {
            if (!File.Exists(item.FilePath)) { Status("Fișierul nu mai există pe disc."); return; }
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Open download: " + ex.Message); Status("Fișierul nu a putut fi deschis."); }
    }

    private void DownloadReveal_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadItemFrom(sender) is not { } item) return;
        try
        {
            if (!File.Exists(item.FilePath)) { Status("Fișierul nu mai există pe disc."); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Reveal download: " + ex.Message); }
    }

    private void DownloadPause_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadItemFrom(sender) is not { } item) return;
        try { item.Operation.Pause(); }
        catch (Exception ex) { Log.Write("Pause download: " + ex.Message); }
    }

    private void DownloadResume_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadItemFrom(sender) is not { } item) return;
        try { item.Operation.Resume(); }
        catch (Exception ex) { Log.Write("Resume download: " + ex.Message); }
    }

    private void DownloadCancel_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadItemFrom(sender) is not { } item) return;
        try { item.Operation.Cancel(); }
        catch (Exception ex) { Log.Write("Cancel download: " + ex.Message); }
    }
}
