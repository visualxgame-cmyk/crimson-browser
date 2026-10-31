using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Vela.Services;

namespace Vela;

/// <summary>
/// Modal settings window. It edits a private copy of <see cref="Settings"/>; themes and the
/// new tab background are previewed live through <c>preview</c>, while everything is only
/// persisted (and applied to tabs) when the user presses Salvează. Cancelling restores the
/// palette that was active when the window opened.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] AccentPresets =
    [
        "#FB542B", "#3B82F6", "#34D399", "#A78BFA",
        "#F472B6", "#F59E0B", "#22D3EE", "#6B7280",
    ];

    private static readonly string[] BackgroundPresets =
    [
        "#101114", "#0B1220", "#0E1512", "#171021",
        "#1A0F14", "#1C1C1E", "#262A33", "#3A2B25",
    ];

    private readonly Settings _original;
    private readonly Settings _draft;
    private readonly Action<Settings> _preview;
    private readonly Action<Settings> _commit;

    private readonly Dictionary<string, Border> _themeCards = new();
    private readonly List<(Border Ring, string? Value)> _accentSwatches = new();
    private readonly List<(Border Ring, string Value)> _backgroundSwatches = new();

    private bool _syncing;
    private bool _committed;

    public SettingsWindow(Settings settings, Action<Settings> preview, Action<Settings> commit)
    {
        _original = settings.Clone();
        _draft = settings.Clone();
        _preview = preview;
        _commit = commit;

        InitializeComponent();

        _syncing = true;
        try
        {
            BuildThemeCards();
            BuildAccentSwatches();
            BuildBackgroundSwatches();
            BuildMemoryChips();
            LoadFromSettings();
            ShowPage("aspect");
        }
        finally
        {
            _syncing = false;
        }
    }

    // =====================================================================
    //  Loading state into the UI
    // =====================================================================

    private void LoadFromSettings()
    {
        SelectTheme(_draft.Theme);
        SetAccent(_draft.CustomAccent, updateBox: true);

        BgThemeChip.IsChecked = _draft.NewTabBackgroundMode == "theme";
        BgColorChip.IsChecked = _draft.NewTabBackgroundMode == "color";
        BgImageChip.IsChecked = _draft.NewTabBackgroundMode == "image";

        ColorHexBox.Text = _draft.NewTabBackgroundColor;
        BlockAdsSwitch.IsChecked = _draft.BlockAds;
        RestoreSwitch.IsChecked = _draft.RestoreSession;
        HomePageBox.Text = _draft.HomePage;
        SearchUrlBox.Text = _draft.SearchUrl;

        UpdateImageText();
        UpdateBackgroundPreview();
    }

    private void BuildThemeCards()
    {
        ThemePanel.Children.Clear();
        _themeCards.Clear();

        foreach (var theme in ThemeCatalog.All)
        {
            var frame = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(3),
                Child = BuildThemePreview(theme),
            };

            var name = new TextBlock
            {
                Text = theme.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 7, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

            var stack = new StackPanel { Width = 148 };
            stack.Children.Add(frame);
            stack.Children.Add(name);

            var button = new Button
            {
                Style = (Style)FindResource("ThemeCardButton"),
                Tag = theme.Id,
                Content = stack,
                ToolTip = theme.Tagline,
            };
            button.Click += ThemeCard_Click;

            ThemePanel.Children.Add(button);
            _themeCards[theme.Id] = frame;
        }
    }

    private static Grid BuildThemePreview(ThemePalette theme)
    {
        var preview = new Grid
        {
            Height = 82,
            Width = 148,
            ClipToBounds = true,
        };
        preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        preview.RowDefinitions.Add(new RowDefinition());

        var strip = new Border { Background = ThemeCatalog.Brush(theme.WindowBg) };
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        tabs.Children.Add(new Border
        {
            Width = 26, Height = 6, CornerRadius = new CornerRadius(3),
            Background = ThemeCatalog.Brush(theme.TabActiveBg), Margin = new Thickness(0, 0, 3, 0),
        });
        tabs.Children.Add(new Border
        {
            Width = 26, Height = 6, CornerRadius = new CornerRadius(3),
            Background = ThemeCatalog.Brush(theme.Accent),
        });
        strip.Child = tabs;
        preview.Children.Add(strip);

        var content = new Border { Background = ThemeCatalog.Brush(theme.ContentBg) };
        var inner = new Grid { Margin = new Thickness(8) };

        var omnibox = new Border
        {
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = ThemeCatalog.Brush(theme.OmniboxBg),
            VerticalAlignment = VerticalAlignment.Top,
        };
        inner.Children.Add(omnibox);

        var tiles = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        tiles.Children.Add(new Border
        {
            Width = 30, Height = 26, CornerRadius = new CornerRadius(5),
            Background = ThemeCatalog.Brush(theme.NewTabSurface), Margin = new Thickness(0, 0, 5, 0),
        });
        tiles.Children.Add(new Border
        {
            Width = 30, Height = 26, CornerRadius = new CornerRadius(5),
            Background = ThemeCatalog.Brush(theme.NewTabSurface2),
        });
        tiles.Children.Add(new Border
        {
            Width = 11, Height = 11, CornerRadius = new CornerRadius(6),
            Background = ThemeCatalog.Brush(theme.Accent),
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        inner.Children.Add(tiles);

        content.Child = inner;
        Grid.SetRow(content, 1);
        preview.Children.Add(content);

        return preview;
    }

    private void BuildAccentSwatches()
    {
        AccentPanel.Children.Clear();
        _accentSwatches.Clear();

        AddAccentSwatch(null, "Accentul temei");
        foreach (var hex in AccentPresets) AddAccentSwatch(hex, hex);
    }

    private void AddAccentSwatch(string? value, string label)
    {
        var theme = ThemeCatalog.Get(_draft.Theme);
        var fill = value is null ? theme.Accent : value;

        var inner = new Grid();
        inner.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = ThemeCatalog.Brush(fill),
        });

        if (value is null)
        {
            inner.Children.Add(new TextBlock
            {
                Text = "A",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var ring = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = inner,
            Tag = value ?? "",
            ToolTip = label,
        };
        ring.MouseLeftButtonDown += AccentSwatch_Click;

        AccentPanel.Children.Add(ring);
        _accentSwatches.Add((ring, value));
    }

    private void BuildBackgroundSwatches()
    {
        ColorPanel.Children.Clear();
        _backgroundSwatches.Clear();

        foreach (var hex in BackgroundPresets)
        {
            var inner = new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeCatalog.Brush(hex),
            };

            var ring = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(3),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = inner,
                Tag = hex,
                ToolTip = hex,
            };
            ring.MouseLeftButtonDown += BackgroundSwatch_Click;

            ColorPanel.Children.Add(ring);
            _backgroundSwatches.Add((ring, hex));
        }
    }

    private void BuildMemoryChips()
    {
        SuspendPanel.Children.Clear();
        DiscardPanel.Children.Clear();
        MaxLivePanel.Children.Clear();

        AddMemoryChip(SuspendPanel, "Oprit", 0, "suspend", _draft.SuspendAfterMinutes == 0);
        AddMemoryChip(SuspendPanel, "2 min", 2, "suspend", _draft.SuspendAfterMinutes == 2);
        AddMemoryChip(SuspendPanel, "5 min", 5, "suspend", _draft.SuspendAfterMinutes == 5);
        AddMemoryChip(SuspendPanel, "10 min", 10, "suspend", _draft.SuspendAfterMinutes == 10);
        AddMemoryChip(SuspendPanel, "30 min", 30, "suspend", _draft.SuspendAfterMinutes == 30);

        AddMemoryChip(DiscardPanel, "Oprit", 0, "discard", _draft.DiscardAfterMinutes == 0);
        AddMemoryChip(DiscardPanel, "10 min", 10, "discard", _draft.DiscardAfterMinutes == 10);
        AddMemoryChip(DiscardPanel, "30 min", 30, "discard", _draft.DiscardAfterMinutes == 30);
        AddMemoryChip(DiscardPanel, "60 min", 60, "discard", _draft.DiscardAfterMinutes == 60);

        AddMemoryChip(MaxLivePanel, "4 tab-uri", 4, "maxlive", _draft.MaxLiveTabs == 4);
        AddMemoryChip(MaxLivePanel, "8 tab-uri", 8, "maxlive", _draft.MaxLiveTabs == 8);
        AddMemoryChip(MaxLivePanel, "10 tab-uri", 10, "maxlive", _draft.MaxLiveTabs == 10);
        AddMemoryChip(MaxLivePanel, "16 tab-uri", 16, "maxlive", _draft.MaxLiveTabs == 16);
    }

    private void AddMemoryChip(WrapPanel panel, string label, int value, string group, bool selected)
    {
        var chip = new RadioButton
        {
            Style = (Style)FindResource("ChoiceChip"),
            Content = label,
            GroupName = group,
            Tag = group + ":" + value,
        };
        chip.Checked += MemoryChip_Checked;
        chip.IsChecked = selected;
        panel.Children.Add(chip);
    }

    // =====================================================================
    //  Theme + accent
    // =====================================================================

    private void SelectTheme(string id)
    {
        _draft.Theme = ThemeCatalog.Get(id).Id;

        foreach (var (key, frame) in _themeCards)
        {
            var selected = string.Equals(key, _draft.Theme, StringComparison.OrdinalIgnoreCase);
            frame.BorderBrush = selected
                ? ThemeCatalog.Brush(ThemeCatalog.Resolve(_draft).Accent)
                : Brushes.Transparent;
        }

        // A theme change also changes the "auto" accent swatch, so rebuild it.
        BuildAccentSwatches();
        SetAccent(_draft.CustomAccent, updateBox: true);
    }

    private void ThemeCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || _syncing) return;

        SelectTheme(id);
        Preview();
    }

    private void SetAccent(string? hex, bool updateBox)
    {
        _draft.CustomAccent = hex;

        if (updateBox)
            AccentHexBox.Text = hex ?? ThemeCatalog.Get(_draft.Theme).Accent;

        AccentStatusText.Visibility = Visibility.Collapsed;

        UpdateAccentRings();
    }

    private void UpdateAccentRings()
    {
        var accent = ThemeCatalog.Brush(ThemeCatalog.Resolve(_draft).Accent);

        foreach (var (ring, value) in _accentSwatches)
        {
            var selected = value is null
                ? string.IsNullOrWhiteSpace(_draft.CustomAccent)
                : string.Equals(value, _draft.CustomAccent, StringComparison.OrdinalIgnoreCase);
            ring.BorderBrush = selected ? accent : Brushes.Transparent;
        }
    }

    private void AccentSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string tag }) return;
        SetAccent(string.IsNullOrWhiteSpace(tag) ? null : tag, updateBox: true);
        Preview();
    }

    private void AccentApply_Click(object sender, RoutedEventArgs e) => ApplyAccentBox();

    private void AccentHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyAccentBox();
        e.Handled = true;
    }

    private void ApplyAccentBox()
    {
        if (!ThemeCatalog.TryParse(AccentHexBox.Text, out var color))
        {
            AccentStatusText.Text = "Cod de culoare invalid. Folosește #RRGGBB.";
            AccentStatusText.Visibility = Visibility.Visible;
            return;
        }

        SetAccent(ThemeCatalog.ToHex(color), updateBox: true);
        Preview();
    }

    private void AccentAuto_Click(object sender, RoutedEventArgs e)
    {
        SetAccent(null, updateBox: true);
        Preview();
    }

    // =====================================================================
    //  New tab background
    // =====================================================================

    private void BgMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string mode }) return;

        _draft.NewTabBackgroundMode = mode;
        UpdateBackgroundPreview();
        Preview();
    }

    private void BackgroundSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string hex }) return;

        _draft.NewTabBackgroundColor = hex;
        _draft.NewTabBackgroundMode = "color";
        ColorHexBox.Text = hex;

        _syncing = true;
        BgColorChip.IsChecked = true;
        _syncing = false;

        UpdateBackgroundSwatches();
        UpdateBackgroundPreview();
        Preview();
    }

    private void ColorApply_Click(object sender, RoutedEventArgs e) => ApplyColorBox();

    private void ColorHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyColorBox();
        e.Handled = true;
    }

    private void ApplyColorBox()
    {
        if (!ThemeCatalog.TryParse(ColorHexBox.Text, out var color))
        {
            FooterStatusText.Text = "Culoarea trebuie scrisă ca #RRGGBB.";
            return;
        }

        _draft.NewTabBackgroundColor = ThemeCatalog.ToHex(color);
        _draft.NewTabBackgroundMode = "color";
        ColorHexBox.Text = _draft.NewTabBackgroundColor;

        _syncing = true;
        BgColorChip.IsChecked = true;
        _syncing = false;

        UpdateBackgroundSwatches();
        UpdateBackgroundPreview();
        Preview();
    }

    private void PickImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Alege imaginea de fundal",
            Filter = "Imagini|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Toate fișierele|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            Directory.CreateDirectory(AppPaths.Backgrounds);

            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (extension.Length == 0) extension = ".png";

            var name = "bg-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + extension;
            File.Copy(dialog.FileName, Path.Combine(AppPaths.Backgrounds, name), overwrite: true);

            DeleteManagedImage(_draft.NewTabBackgroundImage);

            _draft.NewTabBackgroundImage = name;
            _draft.NewTabBackgroundMode = "image";

            _syncing = true;
            BgImageChip.IsChecked = true;
            _syncing = false;

            UpdateImageText();
            UpdateBackgroundPreview();
            Preview();
        }
        catch (Exception ex)
        {
            Log.Write("Pick background image: " + ex);
            FooterStatusText.Text = "Imaginea nu a putut fi copiată.";
        }
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        DeleteManagedImage(_draft.NewTabBackgroundImage);
        _draft.NewTabBackgroundImage = null;

        if (_draft.NewTabBackgroundMode == "image")
            _draft.NewTabBackgroundMode = "color";

        _syncing = true;
        BgColorChip.IsChecked = _draft.NewTabBackgroundMode == "color";
        BgThemeChip.IsChecked = _draft.NewTabBackgroundMode == "theme";
        _syncing = false;

        UpdateImageText();
        UpdateBackgroundPreview();
        Preview();
    }

    private static void DeleteManagedImage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("bg-", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var path = Path.Combine(AppPaths.Backgrounds, name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A locked image file is harmless; it will be overwritten next time.
        }
    }

    private void UpdateImageText()
    {
        var hasImage = !string.IsNullOrWhiteSpace(_draft.NewTabBackgroundImage);
        RemoveImageButton.IsEnabled = hasImage;
        ImageNameText.Text = hasImage
            ? _draft.NewTabBackgroundImage
            : "";
    }

    private void UpdateBackgroundSwatches()
    {
        var accent = ThemeCatalog.Brush(ThemeCatalog.Resolve(_draft).Accent);

        foreach (var (ring, hex) in _backgroundSwatches)
        {
            var selected = string.Equals(hex, _draft.NewTabBackgroundColor, StringComparison.OrdinalIgnoreCase);
            ring.BorderBrush = selected ? accent : Brushes.Transparent;
        }
    }

    private void UpdateBackgroundPreview()
    {
        var theme = ThemeCatalog.Resolve(_draft);
        BgPreviewColor.Background = ThemeCatalog.Brush(ThemeCatalog.ResolveNewTabBackground(theme, _draft));

        if (BgPreviewImage.Background is ImageBrush brush)
        {
            var image = LoadBackgroundImage();
            brush.ImageSource = image;
        }

        BgPreviewImage.Visibility =
            _draft.NewTabBackgroundMode == "image" && LoadBackgroundImage() is not null
                ? Visibility.Visible
                : Visibility.Collapsed;

        UpdateBackgroundSwatches();
    }

    private BitmapImage? LoadBackgroundImage()
    {
        var name = _draft.NewTabBackgroundImage;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var path = Path.Combine(AppPaths.Backgrounds, name);
        if (!File.Exists(path)) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    // =====================================================================
    //  Memory, privacy, navigation
    // =====================================================================

    private void MemoryChip_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag }) return;

        var parts = tag.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var value)) return;

        switch (parts[0])
        {
            case "suspend": _draft.SuspendAfterMinutes = value; break;
            case "discard": _draft.DiscardAfterMinutes = value; break;
            case "maxlive": _draft.MaxLiveTabs = value; break;
            default: return;
        }
    }

    private void BlockAds_Click(object sender, RoutedEventArgs e) =>
        _draft.BlockAds = BlockAdsSwitch.IsChecked == true;

    private void Restore_Click(object sender, RoutedEventArgs e) =>
        _draft.RestoreSession = RestoreSwitch.IsChecked == true;

    private void OpenFilters_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Filters);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Filters) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Open filters: " + ex.Message);
            FooterStatusText.Text = "Folderul Filters nu a putut fi deschis.";
        }
    }

    private bool ApplyTextSettings()
    {
        var home = HomePageBox.Text.Trim();
        if (home.Length == 0) home = AppConstants.NewTabUrl;
        if (!home.Contains("://", StringComparison.Ordinal)) home = "https://" + home;
        _draft.HomePage = home;

        var search = SearchUrlBox.Text.Trim();
        if (search.Length == 0) search = AppConstants.DefaultSearchUrl;
        if (!search.Contains("://", StringComparison.Ordinal)) search = "https://" + search;

        if (!Uri.TryCreate(search, UriKind.Absolute, out _))
        {
            FooterStatusText.Text = "Adresa motorului de căutare nu este validă.";
            SearchUrlBox.Focus();
            return false;
        }

        _draft.SearchUrl = search;
        return true;
    }

    // =====================================================================
    //  Navigation / chrome
    // =====================================================================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (AspectPage is null || sender is not RadioButton { Tag: string page }) return;
        ShowPage(page);
    }

    private void ShowPage(string page)
    {
        AspectPage.Visibility = page == "aspect" ? Visibility.Visible : Visibility.Collapsed;
        NewTabPage.Visibility = page == "newtab" ? Visibility.Visible : Visibility.Collapsed;
        MemoryPage.Visibility = page == "memory" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = page == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        NavPage.Visibility = page == "navigation" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInteractiveSource(source)) return;

        try { DragMove(); }
        catch { /* not dragging */ }
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ButtonBase or TextBoxBase or ScrollBar or Thumb) return true;
            if (current is Grid) return false;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Preview()
    {
        if (_syncing) return;

        _preview(_draft);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyTextSettings()) return;

        if (ThemeCatalog.TryParse(AccentHexBox.Text, out var accent))
        {
            var hex = ThemeCatalog.ToHex(accent);
            var themeAccent = ThemeCatalog.Get(_draft.Theme).Accent;
            _draft.CustomAccent = string.Equals(hex, themeAccent, StringComparison.OrdinalIgnoreCase)
                ? null
                : hex;
        }

        if (ThemeCatalog.TryParse(ColorHexBox.Text, out var background))
            _draft.NewTabBackgroundColor = ThemeCatalog.ToHex(background);

        _committed = true;
        _commit(_draft);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_committed) _preview(_original);
        base.OnClosing(e);
    }
}
