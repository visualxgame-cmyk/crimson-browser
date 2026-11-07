using System.Collections.ObjectModel;
using System.IO;

namespace CrimsonBrowser.Services;

public class Bookmark
{
    public string Title { get; set; } = string.Empty;
    public string Url   { get; set; } = string.Empty;
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title :
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : Url;
    public string TooltipText => DisplayTitle + Environment.NewLine + Url;
}

public class BookmarksService
{
    private static BookmarksService? _instance;
    public static BookmarksService Instance => _instance ??= new BookmarksService();

    private readonly string _filePath;

    public ObservableCollection<Bookmark> Bookmarks { get; } = new();

    public event Action? BookmarksChanged;

    public BookmarksService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrimsonBrowser", "bookmarks.json")) { }

    internal BookmarksService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public bool Contains(string url) =>
        Bookmarks.Any(b => BookmarkAddress.Equals(b.Url, url));

    public void Add(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (Contains(url)) return;

        var bookmark = new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url };
        var snapshot = Bookmarks.ToList();
        snapshot.Insert(0, bookmark);
        if (!Save(snapshot)) return;
        Bookmarks.Insert(0, bookmark);
        BookmarksChanged?.Invoke();
    }

    public void Remove(string url)
    {
        var existing = Bookmarks.FirstOrDefault(b =>
            BookmarkAddress.Equals(b.Url, url));
        if (existing != null)
        {
            var snapshot = Bookmarks.Where(b => b != existing).ToList();
            if (!Save(snapshot)) return;
            Bookmarks.Remove(existing);
            BookmarksChanged?.Invoke();
        }
    }

    public void Toggle(string title, string url)
    {
        if (Contains(url))
            Remove(url);
        else
            Add(title, url);
    }

    private void Load()
    {
        try
        {
            var list = AtomicFileStore.Read<List<Bookmark>>(_filePath);
            if (list != null)
            {
                foreach (var b in list) Bookmarks.Add(b);
            }
        }
        catch (Exception error) { ProfileStorageDiagnostics.Report(_filePath, error); }
    }

    private bool Save(List<Bookmark> snapshot)
    {
        try
        {
            AtomicFileStore.Write(_filePath, snapshot);
            return true;
        }
        catch (Exception error) { ProfileStorageDiagnostics.Report(_filePath, error); return false; }
    }
}
