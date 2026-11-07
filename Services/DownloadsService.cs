using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;


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

    public string Filename   { get; set; } = string.Empty;
    public string SavePath   { get; set; } = string.Empty;
    public string Url        { get; set; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.Now;
    internal bool IsPrivate { get; init; }

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

    public bool IsCompleted => Status == DownloadStatus.Completed && File.Exists(SavePath);

    public string StatusText => Status switch
    {
        DownloadStatus.InProgress => $"{ReceivedBytes / 1024} KB / {(TotalBytes > 0 ? TotalBytes / 1024 + " KB" : "??")}",
        DownloadStatus.Completed  => LocalizationService.Instance.Get("DownloadCompleted"),
        DownloadStatus.Failed     => LocalizationService.Instance.Get("DownloadFailed"),
        DownloadStatus.Cancelled  => LocalizationService.Instance.Get("DownloadCancelled"),
        _                         => string.Empty
    };

    /// <summary>Re-evaluates <see cref="StatusText"/> after a language change.</summary>
    public void RefreshDetails()
    {
        foreach (string name in new[] { nameof(Filename), nameof(SavePath), nameof(IsCompleted) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void RefreshStatusText()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Status) || name == nameof(ReceivedBytes))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        if (name == nameof(Status))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompleted)));
        return true;
    }
}

public class DownloadsService
{
    private static DownloadsService? _instance;
    public static DownloadsService Instance => _instance ??= new DownloadsService();

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

    private readonly string _filePath;
    private readonly BackgroundProfileWriter<DownloadRecord[]> _writer;

    public DownloadsService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrimsonBrowser", "downloads.json"))
    {
        // Refresh the localized status labels of existing entries when the UI language changes.
        LocalizationService.Instance.LanguageChanged += () =>
        {
            foreach (var item in Downloads)
                item.RefreshStatusText();
        };
    }

    internal DownloadsService(string filePath)
    {
        _filePath = filePath;
        _writer = new BackgroundProfileWriter<DownloadRecord[]>(_filePath, (path, snapshot) =>
        {
            AtomicFileStore.Write(path, snapshot);
            if (snapshot.Length == 0) AtomicFileStore.Write(path, snapshot);
        });
        try
        {
            var records = AtomicFileStore.Read<DownloadRecord[]>(_filePath);
            if (records == null) return;
            foreach (var record in records.Take(200))
                Downloads.Add(new DownloadItem
                {
                    SavePath = record.SavePath, Filename = Path.GetFileName(record.SavePath), Url = record.Url,
                    StartedAt = record.StartedAt, TotalBytes = record.TotalBytes, ReceivedBytes = record.ReceivedBytes,
                    Status = record.Status == DownloadStatus.InProgress ? DownloadStatus.Failed : record.Status
                });
        }
        catch (Exception error) { ProfileStorageDiagnostics.Report(_filePath, error); }
    }

    public event Action? ActiveDownloadsChanged;

    public int ActiveCount => Downloads.Count(d => d.Status == DownloadStatus.InProgress);

    private readonly Dictionary<string, DownloadItem> _tracked = new();
    public void Update(string key, string path, string url, long total, long received, DownloadStatus status, bool isPrivate = false)
    {
        bool created = false;
        if (!_tracked.TryGetValue(key, out var item))
        {
            item = new DownloadItem { IsPrivate = isPrivate };
            _tracked.Add(key, item);
            Downloads.Insert(0, item);
            created = true;
        }
        var previousStatus = item.Status;
        bool pathChanged = item.SavePath != path;
        item.SavePath = path;
        item.Filename = Path.GetFileName(path);
        item.Url = url;
        item.TotalBytes = Math.Max(total, 0);
        item.ReceivedBytes = received;
        item.Status = status;
        item.RefreshDetails();
        if (created || previousStatus != status) ActiveDownloadsChanged?.Invoke();
        if (created || previousStatus != status) TrimHistory();
        // Progress callbacks stay in memory. Persist starts and terminal transitions only.
        if (!item.IsPrivate && (created || previousStatus != status || pathChanged))
        {
            Save();
        }
    }

    public void ClearCompleted()
    {
        var done = Downloads.Where(d => d.Status != DownloadStatus.InProgress).ToList();
        foreach (var d in done) Downloads.Remove(d);
        foreach (var key in _tracked.Where(pair => done.Contains(pair.Value)).Select(pair => pair.Key).ToArray()) _tracked.Remove(key);
        Save();
    }

    private void TrimHistory()
    {
        var expired = Downloads.Where(item => item.Status != DownloadStatus.InProgress).Skip(200).ToHashSet();
        foreach (var item in expired) Downloads.Remove(item);
        foreach (var key in _tracked.Where(pair => expired.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
            _tracked.Remove(key);
    }

    private void Save() => _writer.Schedule(Downloads.Where(item => !item.IsPrivate && !string.IsNullOrWhiteSpace(item.SavePath)).Take(200).Select(item =>
        new DownloadRecord(item.SavePath, item.Url, item.StartedAt, item.TotalBytes, item.ReceivedBytes, item.Status)).ToArray());

    public bool FlushPendingWrites() => _writer.Flush();
    internal static bool FlushIfCreated() => _instance?.FlushPendingWrites() ?? true;
    private sealed record DownloadRecord(string SavePath, string Url, DateTime StartedAt, long TotalBytes,
        long ReceivedBytes, DownloadStatus Status);
}
