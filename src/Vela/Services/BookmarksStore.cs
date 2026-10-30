using System.IO;
using System.Text.Json;
using System.Threading;

namespace Vela.Services;

public sealed record Bookmark(string Url, string Title, DateTimeOffset AddedAt);

public sealed class BookmarksStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(AppPaths.Data, "bookmarks.json");
    private readonly List<Bookmark> _items = new();
    private readonly Lock _gate = new();
    private Timer? _timer;

    public BookmarksStore()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(_path));
                if (loaded is not null) _items.AddRange(loaded);
            }
        }
        catch
        {
            // Ignore a corrupt bookmarks file.
        }
    }

    public IReadOnlyList<Bookmark> All
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public bool Contains(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        lock (_gate) return _items.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds the URL, or removes it when already bookmarked. Returns true when added.</summary>
    public bool Toggle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith(AppConstants.InternalHost, StringComparison.OrdinalIgnoreCase))
            return false;

        bool added;
        lock (_gate)
        {
            var existing = _items.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _items.Remove(existing);
                added = false;
            }
            else
            {
                _items.Insert(0, new Bookmark(url, string.IsNullOrWhiteSpace(title) ? url : title, DateTimeOffset.UtcNow));
                added = true;
            }
        }

        ScheduleSave();
        return added;
    }

    public void Remove(string url)
    {
        lock (_gate) _items.RemoveAll(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_timer is null)
        {
            _timer = new Timer(_ => Flush(), null, 400, Timeout.Infinite);
            return;
        }

        _timer.Change(400, Timeout.Infinite);
    }

    public void Flush()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_items, JsonOptions);

        try { File.WriteAllText(_path, json); }
        catch { /* best effort */ }
    }
}
