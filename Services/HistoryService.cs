using System.IO;

namespace CrimsonBrowser.Services;

public record HistoryEntry(string Title, string Url, DateTime VisitedAt);

public class HistoryService
{
    private static HistoryService? _instance;
    public static HistoryService Instance => _instance ??= new HistoryService();

    private readonly string _historyFilePath;
    private readonly BackgroundProfileWriter<HistoryEntry[]> _writer;
    private List<HistoryEntry> _entries = new();

    public IReadOnlyList<HistoryEntry> Entries => _entries.AsReadOnly();

    public event Action? HistoryChanged;

    public HistoryService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrimsonBrowser", "history.json")) { }

    internal HistoryService(string filePath)
    {
        _historyFilePath = filePath;
        _writer = new BackgroundProfileWriter<HistoryEntry[]>(_historyFilePath, (path, snapshot) =>
        {
            AtomicFileStore.Write(path, snapshot);
            // Clearing history also replaces the recovery copy with empty history.
            if (snapshot.Length == 0) AtomicFileStore.Write(path, snapshot);
        });
        Load();
    }

    public void AddEntry(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "crimson://start") return;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Equals("start.crimson", StringComparison.OrdinalIgnoreCase)) return;

        // Remove duplicate if same URL
        _entries.RemoveAll(e => e.Url == url);

        _entries.Insert(0, new HistoryEntry(
            string.IsNullOrWhiteSpace(title) ? url : title,
            url,
            DateTime.Now
        ));

        // Keep only last 2000 entries
        if (_entries.Count > 2000)
            _entries = _entries.Take(2000).ToList();

        HistoryChanged?.Invoke();
        Save();
    }

    public List<HistoryEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _entries.Take(100).ToList();
        return _entries
            .Where(e => e.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || e.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .ToList();
    }

    public bool Clear()
    {
        _entries.Clear();
        HistoryChanged?.Invoke();
        Save();
        return FlushPendingWrites();
    }

    private void Load()
    {
        try
        {
            var loaded = AtomicFileStore.Read<List<HistoryEntry>>(_historyFilePath);
            if (loaded != null) _entries = loaded.Take(2000).ToList();
        }
        catch (Exception error) { ProfileStorageDiagnostics.Report(_historyFilePath, error); }
    }

    private void Save()
    {
        _writer.Schedule(_entries.ToArray());
    }

    public bool FlushPendingWrites() => _writer.Flush();
    internal static bool FlushIfCreated() => _instance?.FlushPendingWrites() ?? true;
}
