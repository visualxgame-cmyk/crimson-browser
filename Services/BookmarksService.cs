using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace CrimsonBrowser.Services;

public class Bookmark
{
    public string Title { get; set; } = string.Empty;
    public string Url   { get; set; } = string.Empty;
}

public class BookmarksService
{
    private static BookmarksService? _instance;
    public static BookmarksService Instance => _instance ??= new BookmarksService();

    private readonly string _filePath;

    public ObservableCollection<Bookmark> Bookmarks { get; } = new();

    public event Action? BookmarksChanged;

    public BookmarksService()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrimsonBrowser"
        );
        _filePath = Path.Combine(folder, "bookmarks.json");
        Load();
    }

    public bool Contains(string url) =>
        Bookmarks.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));

    public void Add(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (Contains(url)) return;

        Bookmarks.Insert(0, new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url });
        BookmarksChanged?.Invoke();
        Save();
    }

    public void Remove(string url)
    {
        var existing = Bookmarks.FirstOrDefault(b =>
            string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Bookmarks.Remove(existing);
            BookmarksChanged?.Invoke();
            Save();
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
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<Bookmark>>(json);
                if (list != null)
                {
                    foreach (var b in list) Bookmarks.Add(b);
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(_filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Bookmarks.ToList()));
        }
        catch { }
    }
}
