using System.Collections.ObjectModel;
using System.IO;

namespace CrimsonBrowser.Services;

public record HistoryEntry(string Title, string Url, DateTime VisitedAt);

public class HistoryService
{
    private static HistoryService? _instance;
    public static HistoryService Instance => _instance ??= new HistoryService();

    private readonly string _historyFilePath;
    private List<HistoryEntry> _entries = new();

    public IReadOnlyList<HistoryEntry> Entries => _entries.AsReadOnly();

    public event Action? HistoryChanged;

    public HistoryService()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrimsonBrowser"
        );
        _historyFilePath = Path.Combine(folder, "history.json");
        Load();
    }

    public void AddEntry(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "crimson://start") return;
        if (url.StartsWith("https://start.crimson")) return;

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

    public void Clear()
    {
        _entries.Clear();
        HistoryChanged?.Invoke();
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                var loaded = AtomicFileStore.Read<List<HistoryEntry>>(_historyFilePath);
                if (loaded != null) _entries = loaded;
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            AtomicFileStore.Write(_historyFilePath, _entries);
        }
        catch { }
    }
}
