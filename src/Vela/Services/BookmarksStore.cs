using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Vela.Services;

public sealed record Bookmark(string Url, string Title, DateTimeOffset AddedAt)
{
    public string Id { get; init; } = StableId(Url);
    public string Folder { get; init; } = "";
    public DateTimeOffset ModifiedAt { get; init; } = AddedAt;
    public bool IsDeleted { get; init; }
    public bool IsRead { get; init; }
    private static string StableId(string url)
    {
        var key = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : url;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..32];
    }
}

public sealed class BookmarksStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly List<Bookmark> _items = new();
    private readonly Lock _gate = new();
    private Timer? _timer;
    public event Action? Changed;
    public BookmarksStore(string fileName = "bookmarks.json")
    {
        if (fileName is not ("bookmarks.json" or "reading-list.json")) throw new ArgumentException("Bibliotecă necunoscută.");
        _path = Path.Combine(AppPaths.Data, fileName);
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var loaded = JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(path));
                if (loaded is null) continue;
                _items.AddRange(loaded.Where(b => b is not null && !string.IsNullOrWhiteSpace(b.Url) && b.Title is not null)
                    .DistinctBy(b => b.Id).Take(BookmarkHtml.MaxEntries));
                break;
            }
            catch (Exception ex) { Log.Write("Read bookmarks: " + ex.Message); }
        }
    }
    public IReadOnlyList<Bookmark> All { get { lock (_gate) return _items.Where(b => !b.IsDeleted).ToArray(); } }
    internal IReadOnlyList<Bookmark> Snapshot { get { lock (_gate) return _items.ToArray(); } }
    public bool Contains(string url) { lock (_gate) return _items.Any(b => !b.IsDeleted && SameUrl(b.Url, url)); }
    private static bool SameUrl(string a, string b) => string.Equals(
        Uri.TryCreate(a, UriKind.Absolute, out var first) ? first.AbsoluteUri : a,
        Uri.TryCreate(b, UriKind.Absolute, out var second) ? second.AbsoluteUri : b, StringComparison.OrdinalIgnoreCase);
    public bool Toggle(string url, string title)
    {
        Validate(url, title, "");
        bool added;
        lock (_gate)
        {
            var index = _items.FindIndex(b => SameUrl(b.Url, url));
            if (index >= 0)
            {
                added = _items[index].IsDeleted;
                _items[index] = _items[index] with { IsDeleted = !added, ModifiedAt = DateTimeOffset.UtcNow };
            }
            else
            {
                if (_items.Count >= BookmarkHtml.MaxEntries) throw new InvalidOperationException("Limita de favorite a fost atinsă.");
                _items.Insert(0, new Bookmark(url, string.IsNullOrWhiteSpace(title) ? url : title, DateTimeOffset.UtcNow));
                added = true;
            }
        }
        ScheduleSave();
        Changed?.Invoke();
        return added;
    }
    public void Remove(string url)
    {
        lock (_gate)
            for (var i = 0; i < _items.Count; i++)
                if (SameUrl(_items[i].Url, url)) _items[i] = _items[i] with { IsDeleted = true, ModifiedAt = DateTimeOffset.UtcNow };
        ScheduleSave();
        Changed?.Invoke();
    }
    public string Save(string? id, string title, string url, string folder)
    {
        title = title.Trim(); url = url.Trim(); folder = folder.Trim().Trim('/');
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        Validate(url, title, folder);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(title)) title = url;
            if (_items.Any(b => !b.IsDeleted && b.Id != id && SameUrl(b.Url, url))) throw new ArgumentException("Adresa există deja în favorite.");
            var index = id is null ? -1 : _items.FindIndex(b => b.Id == id);
            if (id is not null && index < 0) throw new ArgumentException("Favorita nu mai există.");
            var next = new List<Bookmark>(_items);
            if (index < 0)
            {
                // Reuse a tombstone's identity when the same address is added again.
                index = next.FindIndex(b => SameUrl(b.Url, url));
                if (index < 0)
                {
                    if (next.Count >= BookmarkHtml.MaxEntries) throw new InvalidOperationException("Limita de favorite a fost atinsă.");
                    next.Insert(0, new Bookmark(url, title, DateTimeOffset.UtcNow) { Folder = folder }); index = 0;
                }
                else next[index] = next[index] with { Title = title, Folder = folder, IsDeleted = false, ModifiedAt = DateTimeOffset.UtcNow };
            }
            else next[index] = next[index] with { Title = title, Url = url, Folder = folder, ModifiedAt = DateTimeOffset.UtcNow, IsDeleted = false };
            AtomicFile.Write(_path, JsonSerializer.Serialize(next, JsonOptions));
            _items.Clear(); _items.AddRange(next); id = next[index].Id;
        }
        Changed?.Invoke();
        return id;
    }
    public void Move(string id, int direction)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(b => b.Id == id && !b.IsDeleted);
            if (index < 0 || direction is not (-1 or 1)) return;
            var target = index + direction;
            while (target >= 0 && target < _items.Count && (_items[target].IsDeleted || _items[target].Folder != _items[index].Folder)) target += direction;
            if (target < 0 || target >= _items.Count) return;
            var next = new List<Bookmark>(_items);
            (next[index], next[target]) = (next[target], next[index]);
            AtomicFile.Write(_path, JsonSerializer.Serialize(next, JsonOptions));
            _items.Clear(); _items.AddRange(next);
        }
        Changed?.Invoke();
    }
    private static void Validate(string url, string title, string folder)
    {
        if (title.Length > 500 || folder.Length > 240 || url.Length > 8192 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file") ||
            uri.Host is AppConstants.InternalHost or AppConstants.BackgroundHost or ShortcutStore.IconHost || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Introdu un nume și o adresă web sau HTML locală validă. Numele folderului poate avea maximum 240 de caractere.");
    }
    internal BookmarkImportResult Import(string html)
    {
        var (entries, skipped) = BookmarkHtml.Parse(html);
        return ImportEntries(entries, skipped);
    }
    internal BookmarkImportResult ImportEntries(IReadOnlyList<Bookmark> entries, int skipped = 0)
    {
        ValidateSnapshot(entries);
        int added = 0, duplicates = 0;
        lock (_gate)
        {
            var next = new List<Bookmark>(_items);
            foreach (var entry in entries)
            {
                var index = next.FindIndex(b => SameUrl(b.Url, entry.Url));
                if (index >= 0 && !next[index].IsDeleted) { duplicates++; continue; }
                if (index >= 0) next[index] = entry with { Id = next[index].Id, ModifiedAt = DateTimeOffset.UtcNow };
                else next.Add(entry);
                added++;
            }
            if (next.Count > BookmarkHtml.MaxEntries) throw new InvalidDataException("Importul depășește limita de 20.000 de favorite și înregistrări de ștergere.");
            AtomicFile.Write(_path, JsonSerializer.Serialize(next, JsonOptions));
            _items.Clear(); _items.AddRange(next);
        }
        Changed?.Invoke();
        return new(added, duplicates, skipped);
    }
    internal static void ValidateSnapshot(IReadOnlyList<Bookmark> remote)
    {
        if (remote.Count > BookmarkHtml.MaxEntries) throw new InvalidDataException("Prea multe favorite în arhivă.");
        foreach (var item in remote)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Title is null || item.Folder is null) throw new InvalidDataException("Arhivă de favorite invalidă.");
            Validate(item.Url, item.Title, item.Folder);
        }
    }
    internal void SetRead(string id, bool read)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(b => b.Id == id && !b.IsDeleted);
            if (index < 0) throw new ArgumentException("Înregistrarea nu mai există.");
            var next = new List<Bookmark>(_items);
            next[index] = next[index] with { IsRead = read, ModifiedAt = DateTimeOffset.UtcNow };
            AtomicFile.Write(_path, JsonSerializer.Serialize(next, JsonOptions)); _items.Clear(); _items.AddRange(next);
        }
        Changed?.Invoke();
    }
    internal int Merge(IEnumerable<Bookmark> incoming, bool replace = false)
    {
        var remote = incoming.Take(BookmarkHtml.MaxEntries * 32 + 1).ToArray();
        if (remote.Any(b => b is null)) throw new InvalidDataException("Arhivă de favorite invalidă.");
        remote = remote.GroupBy(b => b.Id).Select(g => g.OrderByDescending(b => b.ModifiedAt).First()).ToArray();
        ValidateSnapshot(remote);
        int changes = 0;
        lock (_gate)
        {
            var next = replace ? new List<Bookmark>() : new List<Bookmark>(_items);
            foreach (var item in remote)
            {
                var index = next.FindIndex(b => b.Id == item.Id || SameUrl(b.Url, item.Url));
                if (index < 0) { next.Add(item); changes++; }
                else if (item.ModifiedAt > next[index].ModifiedAt) { next[index] = item; changes++; }
            }
            if (next.Count > BookmarkHtml.MaxEntries) throw new InvalidDataException("Arhiva depășește limita de favorite.");
            AtomicFile.Write(_path, JsonSerializer.Serialize(next, JsonOptions));
            _items.Clear(); _items.AddRange(next);
        }
        Changed?.Invoke(); return changes;
    }
    private void ScheduleSave()
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(400, Timeout.Infinite);
        }
    }
    public void Flush()
    {
        lock (_gate)
        {
            try { AtomicFile.Write(_path, JsonSerializer.Serialize(_items, JsonOptions)); }
            catch (Exception ex) { Log.Write("Save bookmarks: " + ex.Message); }
        }
    }
}
