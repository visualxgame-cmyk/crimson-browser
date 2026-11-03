using System.Windows;
using System.Windows.Controls;
using Vela.Services;

namespace Vela.Tests;

internal static partial class Program
{
    /// <summary>Covers the 0.1.6 roadmap items that do not need a live page: appearance,
    /// the bookmarks bar, the downloads panel shell and the custom menu surface.</summary>
    private static async Task RoadmapTests(MainWindow window)
    {
        var settings = Field<Settings>(window, "_settings");

        // System theme resolves to one of the concrete palettes instead of staying unresolved.
        var system = ThemeCatalog.Get(ThemeCatalog.SystemId);
        Check(system.Id == (SystemTheme.IsLight ? ThemeCatalog.LightId : ThemeCatalog.DarkId),
            "System theme follows the current Windows app theme");

        var light = ThemeCatalog.Get(ThemeCatalog.LightId);
        var dark = ThemeCatalog.Get(ThemeCatalog.DarkId);
        Check(light.TextPrimary != dark.TextPrimary && light.WindowBg != dark.WindowBg && light.ContentBg != light.TextPrimary,
            "Light theme exposes its own readable palette");

        var originalTheme = settings.Theme;
        settings.Theme = ThemeCatalog.SystemId;
        ThemeCatalog.Apply(settings);
        Check(ThemeCatalog.Current.Id == system.Id, "Applying the System theme uses the OS light or dark palette");
        settings.Theme = originalTheme;
        ThemeCatalog.Apply(settings);

        // Bookmarks bar: visibility follows the setting and Ctrl+Shift+B flips it back.
        var bar = Field<Border>(window, "BookmarksBar");
        var before = settings.ShowBookmarksBar;
        Invoke(window, "SetBookmarksBar", !before);
        Check(settings.ShowBookmarksBar == !before &&
              bar.Visibility == (!before ? Visibility.Visible : Visibility.Collapsed),
            "Bookmarks bar visibility follows its setting");
        Invoke(window, "SetBookmarksBar", before);

        // A saved bookmark appears in the strip; removing it restores the previous list.
        const string barUrl = "https://roadmap-bar.test/item";
        var barItems = Field<ItemsControl>(window, "BookmarksBarItems");
        var initialBookmarks = barItems.Items.Count;
        MainWindow.Bookmarks.Save(null, "Roadmap bar", barUrl, "");
        await Until(() => barItems.Items.Count == initialBookmarks + 1, "Bookmarks bar refreshes after a save");
        Check(Field<TextBlock>(window, "BookmarksBarEmpty").Visibility == Visibility.Collapsed,
            "Bookmarks bar hides its empty hint once a favorite exists");
        MainWindow.Bookmarks.Remove(barUrl);
        await Until(() => barItems.Items.Count == initialBookmarks, "Bookmarks bar refreshes after a removal");
        Check(Field<TextBlock>(window, "BookmarksBarEmpty").Visibility ==
              (initialBookmarks == 0 ? Visibility.Visible : Visibility.Collapsed),
            "Bookmarks bar restores its empty hint when no favorites remain");

        // Themed download panel is part of the shell and starts collapsed.
        Check(Field<System.Windows.Controls.Primitives.Popup>(window, "DownloadsPopup").IsOpen == false,
            "Downloads panel starts closed");
        Check(Field<ItemsControl>(window, "DownloadsList").ItemsSource is not null,
            "Downloads panel binds its themed item list");

        // Rounded menu surface is installed application-wide.
        var menuStyle = Application.Current.TryFindResource(typeof(ContextMenu)) as Style;
        Check(menuStyle is not null && menuStyle.Setters.OfType<Setter>().Any(setter => setter.Property == ContextMenu.TemplateProperty),
            "Context menus use Vela's rounded themed template");

        // Settings expose the System card and the light palette next to the dark ones.
        var settingsWindow = new SettingsWindow(settings.Clone(), _ => { }, _ => { });
        var themePanel = Field<System.Windows.Controls.WrapPanel>(settingsWindow, "ThemePanel");
        var cards = themePanel.Children.OfType<System.Windows.Controls.Button>().ToArray();
        Check(cards.Length == ThemeCatalog.All.Count + 1 && cards[0].Tag as string == ThemeCatalog.SystemId,
            "Settings offer a System theme card next to every built-in palette");
        Check(ThemeCatalog.All.Count(theme => theme.Id == ThemeCatalog.LightId) == 1 &&
              ThemeCatalog.All.Any(theme => theme.Id == ThemeCatalog.DarkId),
            "Both the light and dark palettes are selectable");
        settingsWindow.Close();
    }
}
