using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace CrimsonBrowser.Services;

public enum DownloadStatus
{
    InProgress,
    Completed,
    Failed,
    Cancelled
}

public class DownloadItem : INotifyPropertyChanged
{
    private double   _progress;
    private long     _receivedBytes;
    private long     _totalBytes;
    private DownloadStatus _status = DownloadStatus.InProgress;

    public string Filename   { get; init; } = string.Empty;
    public string SavePath   { get; init; } = string.Empty;
    public string Url        { get; init; } = string.Empty;
    public DateTime StartedAt { get; } = DateTime.Now;

    public long TotalBytes
    {
        get => _totalBytes;
        set => SetField(ref _totalBytes, value);
    }

    public long ReceivedBytes
    {
        get => _receivedBytes;
        set
        {
            SetField(ref _receivedBytes, value);
            Progress = TotalBytes > 0 ? (double)value / TotalBytes * 100 : 0;
        }
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public DownloadStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string StatusText => Status switch
    {
        DownloadStatus.InProgress => $"{ReceivedBytes / 1024} KB / {(TotalBytes > 0 ? TotalBytes / 1024 + " KB" : "??")}",
        DownloadStatus.Completed  => "Completed",
        DownloadStatus.Failed     => "Failed",
        DownloadStatus.Cancelled  => "Cancelled",
        _                         => string.Empty
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Status) || name == nameof(ReceivedBytes))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        return true;
    }
}

public class DownloadsService
{
    private static DownloadsService? _instance;
    public static DownloadsService Instance => _instance ??= new DownloadsService();

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    public event Action? ActiveDownloadsChanged;

    public int ActiveCount => Downloads.Count(d => d.Status == DownloadStatus.InProgress);

    /// <summary>Wires up a CoreWebView2DownloadOperation to track progress.</summary>
    public void Track(CoreWebView2DownloadOperation op)
    {
        string filename = Path.GetFileName(op.ResultFilePath);
        var item = new DownloadItem
        {
            Filename  = filename,
            SavePath  = op.ResultFilePath,
            Url       = op.Uri,
            TotalBytes = (long)(op.TotalBytesToReceive ?? 0)
        };

        Downloads.Insert(0, item);
        ActiveDownloadsChanged?.Invoke();

        op.BytesReceivedChanged += (s, e) =>
        {
            item.ReceivedBytes = op.BytesReceived;
            if (op.TotalBytesToReceive.HasValue)
                item.TotalBytes = (long)op.TotalBytesToReceive.Value;
        };

        op.StateChanged += (s, e) =>
        {
            item.Status = op.State switch
            {
                CoreWebView2DownloadState.Completed  => DownloadStatus.Completed,
                CoreWebView2DownloadState.Interrupted => DownloadStatus.Failed,
                _                                    => DownloadStatus.InProgress
            };
            ActiveDownloadsChanged?.Invoke();
        };
    }

    public void ClearCompleted()
    {
        var done = Downloads.Where(d => d.Status != DownloadStatus.InProgress).ToList();
        foreach (var d in done) Downloads.Remove(d);
    }
}
