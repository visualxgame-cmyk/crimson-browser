using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private sealed record BookmarkBarEntry(string Title, string Url, bool IsFolder, IReadOnlyList<Bookmark> Children);

    private void RefreshBookmarksBar()
    {
        var enabled = _settings.ShowBookmarksBar;
        BookmarksBarToggle.IsChecked = enabled;
        BookmarksBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled) return;

        var entries = new List<BookmarkBarEntry>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = _bookmarks.All;
        foreach (var mark in all)
        {
            if (mark.Folder.Length == 0)
            {
                entries.Add(new(mark.Title, mark.Url, false, []));
            }
            else if (seenFolders.Add(mark.Folder))
            {
                entries.Add(new(mark.Folder, "", true,
                    all.Where(item => string.Equals(item.Folder, mark.Folder, StringComparison.OrdinalIgnoreCase)).ToArray()));
            }
        }

        BookmarksBarItems.ItemsSource = entries;
        BookmarksBarEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBookmarksBar(bool visible)
    {
        if (_settings.ShowBookmarksBar == visible) return;
        _settings.ShowBookmarksBar = visible;
        SettingsStore.Save(_settings);
        RefreshBookmarksBar();
        Status(visible ? "Bara de favorite este afișată." : "Bara de favorite este ascunsă.", 4);
    }

    private void ToggleBookmarksBar() => SetBookmarksBar(!_settings.ShowBookmarksBar);

    private void BookmarksBarToggle_Click(object sender, RoutedEventArgs e) =>
        SetBookmarksBar(BookmarksBarToggle.IsChecked == true);

    private void BookmarksBarAll_Click(object sender, RoutedEventArgs e) => OpenLibrary("bookmarks");

    private void BookmarkBarItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BookmarkBarEntry entry } element) return;
        if (entry.IsFolder)
        {
            ShowBookmarkFolderMenu(element, entry);
            return;
        }

        _ = NavigateActiveAsync(entry.Url);
    }

    private void BookmarkBarItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BookmarkBarEntry entry } element) return;

        var menu = new ContextMenu();
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (entry.IsFolder)
        {
            Add("Deschide toate în taburi noi", () => OpenBookmarksInTabs(entry.Children));
            Add("Deschide toate în ferestre private", () => OpenBookmarksInPrivate(entry.Children));
            menu.Items.Add(new Separator());
            Add("Gestionează favoritele…", () => OpenLibrary("bookmarks"));
            Add("Elimină folderul din bară", () => RemoveBookmarks(entry.Children));
        }
        else
        {
            Add("Deschide în tab nou", () => _ = AddTabAsync(entry.Url, select: false));
            Add("Deschide în fereastră privată", () => _ = OpenInPrivateWindowAsync(entry.Url));
            menu.Items.Add(new Separator());
            Add("Copiază adresa", () => CopyBookmarkUrl(entry.Url));
            Add("Elimină din favorite", () => RemoveBookmarks([new Bookmark(entry.Url, entry.Title, DateTimeOffset.UtcNow)]));
        }

        element.ContextMenu = menu;
    }

    private void ShowBookmarkFolderMenu(FrameworkElement target, BookmarkBarEntry folder)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        foreach (var mark in folder.Children)
        {
            var item = new MenuItem { Header = mark.Title };
            item.Click += (_, _) => _ = NavigateActiveAsync(mark.Url);
            menu.Items.Add(item);
        }
        if (folder.Children.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Folder gol", IsEnabled = false });
        }
        else
        {
            menu.Items.Add(new Separator());
            var all = new MenuItem { Header = "Deschide toate în taburi noi" };
            all.Click += (_, _) => OpenBookmarksInTabs(folder.Children);
            menu.Items.Add(all);
        }
        menu.IsOpen = true;
    }

    private void OpenBookmarksInTabs(IReadOnlyList<Bookmark> marks)
    {
        foreach (var mark in marks) _ = AddTabAsync(mark.Url, select: false);
    }

    private void OpenBookmarksInPrivate(IReadOnlyList<Bookmark> marks)
    {
        var window = new MainWindow(_env, isPrivate: true, suppressStartupTab: true)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        foreach (var mark in marks) _ = window.AddTabAsync(mark.Url, select: false);
    }

    private async Task OpenInPrivateWindowAsync(string url)
    {
        var window = new MainWindow(_env, isPrivate: true, suppressStartupTab: true)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        await window.AddTabAsync(url);
    }

    private void CopyBookmarkUrl(string url)
    {
        try
        {
            Clipboard.SetText(url);
            Status("Adresa a fost copiată.", 3);
        }
        catch (Exception ex) { Log.Write("Copy bookmark: " + ex.Message); }
    }

    private void RemoveBookmarks(IEnumerable<Bookmark> marks)
    {
        var count = 0;
        foreach (var mark in marks)
        {
            _bookmarks.Remove(mark.Url);
            count++;
        }
        if (count > 0) Status(count == 1 ? "Favorita a fost eliminată." : $"{count} favorite au fost eliminate.", 4);
    }

    private void OnBookmarksChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshBookmarksBar);
            return;
        }
        RefreshBookmarksBar();
        PopulateMenu();
    }
}
