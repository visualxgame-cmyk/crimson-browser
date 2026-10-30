using System.IO;
using System.Text.Json;
using System.Threading;

namespace Vela.Services;

public sealed record HistoryEntry(string Url, string Title, DateTimeOffset VisitedAt);

/// <summary>Visit history backed by a JSON file, written with a small debounce.</summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 8000;
    private const int DebounceMs = 800;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path = Path.Combine(AppPaths.Data, "history.json");
    private readonly List<HistoryEntry> _items = new();
    private readonly Lock _gate = new();
    private Timer? _timer;

    public HistoryStore()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path));
                if (loaded is not null) _items.AddRange(loaded);
            }
        }
        catch
        {
            // Ignore a corrupt history file.
        }
    }

    public void Add(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;

        lock (_gate)
        {
            if (_items.Count > 0 &&
                string.Equals(_items[^1].Url, url, StringComparison.OrdinalIgnoreCase) &&
                (DateTimeOffset.UtcNow - _items[^1].VisitedAt).TotalMinutes < 10)
            {
                return;
            }

            _items.Add(new HistoryEntry(url, string.IsNullOrWhiteSpace(title) ? url : title, DateTimeOffset.UtcNow));

            if (_items.Count > MaxEntries)
                _items.RemoveRange(0, _items.Count - MaxEntries);
        }

        ScheduleSave();
    }

    public IReadOnlyList<HistoryEntry> Recent(int limit)
    {
        lock (_gate)
        {
            return _items
                .AsEnumerable()
                .Reverse()
                .Where(e => !e.Url.StartsWith(AppConstants.InternalHost, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToList();
        }
    }

    public IReadOnlyList<HistoryEntry> Search(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) return Recent(limit);

        lock (_gate)
        {
            return _items
                .AsEnumerable()
                .Reverse()
                .Where(e => e.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            e.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
        Flush();
    }

    private void ScheduleSave()
    {
        if (_timer is null)
        {
            _timer = new Timer(_ => Flush(), null, DebounceMs, Timeout.Infinite);
            return;
        }

        _timer.Change(DebounceMs, Timeout.Infinite);
    }

    public void Flush()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_items, JsonOptions);

        try { File.WriteAllText(_path, json); }
        catch { /* best effort */ }
    }
}
