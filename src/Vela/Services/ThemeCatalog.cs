using System.Windows;
using System.Windows.Media;

namespace Vela.Services;

/// <summary>One colour scheme for the whole browser (window chrome + new tab page).</summary>
public sealed record ThemePalette
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Tagline { get; init; }

    public required string Accent { get; init; }
    public required string AccentHover { get; init; }

    public required string WindowBg { get; init; }
    public required string ToolbarBg { get; init; }
    public required string ContentBg { get; init; }

    public required string TabActiveBg { get; init; }
    public required string TabHoverBg { get; init; }

    public required string BorderSoft { get; init; }
    public required string BorderStrong { get; init; }
    public required string HoverBg { get; init; }
    public required string PressedBg { get; init; }

    public required string OmniboxBg { get; init; }
    public required string OmniboxFocus { get; init; }

    public required string TextPrimary { get; init; }
    public required string TextSecondary { get; init; }
    public required string TextMuted { get; init; }

    public required string PopupBg { get; init; }
    public required string InsetBg { get; init; }

    public required string NewTabSurface { get; init; }
    public required string NewTabSurface2 { get; init; }
}

/// <summary>
/// Built-in themes plus the code that pushes them into WPF resources. Brushes are replaced in
/// <see cref="Application.Resources"/> and every style uses DynamicResource, so a theme change is
/// instant and touches both the chrome and the open new tab pages.
/// </summary>
public static class ThemeCatalog
{
    public const string SystemId = "system";
    public const string DarkId = "vela";
    public const string LightId = "luminos";

    public static IReadOnlyList<ThemePalette> All { get; } =
    [
        new ThemePalette
        {
            Id = "vela", Name = "Vela", Tagline = "Portocaliul Brave pe grafit.",
            Accent = "#FB542B", AccentHover = "#FF7148",
            WindowBg = "#15161A", ToolbarBg = "#1B1C21", ContentBg = "#101114",
            TabActiveBg = "#282A31", TabHoverBg = "#23242A",
            BorderSoft = "#2A2C34", BorderStrong = "#3A3D47", HoverBg = "#2E3038", PressedBg = "#383B45",
            OmniboxBg = "#24262D", OmniboxFocus = "#33363F",
            TextPrimary = "#ECEDF1", TextSecondary = "#A0A3AE", TextMuted = "#6E7280",
            PopupBg = "#22242B", InsetBg = "#1A1C22",
            NewTabSurface = "#1B1D23", NewTabSurface2 = "#24262D",
        },
        new ThemePalette
        {
            Id = LightId, Name = "Luminos", Tagline = "Alb curat, pentru lumină puternică.",
            Accent = "#FB542B", AccentHover = "#D9431E",
            WindowBg = "#ECEEF2", ToolbarBg = "#F7F8FA", ContentBg = "#F7F8FA",
            TabActiveBg = "#FFFFFF", TabHoverBg = "#E9EBEF",
            BorderSoft = "#DDE1E7", BorderStrong = "#C7CCD5", HoverBg = "#E8EBF0", PressedBg = "#DDE2E9",
            OmniboxBg = "#FFFFFF", OmniboxFocus = "#FFFFFF",
            TextPrimary = "#1B1D21", TextSecondary = "#565B66", TextMuted = "#8A909C",
            PopupBg = "#FFFFFF", InsetBg = "#F2F4F7",
            NewTabSurface = "#FFFFFF", NewTabSurface2 = "#F1F3F6",
        },
        new ThemePalette
        {
            Id = "ocean", Name = "Ocean", Tagline = "Albastru rece, liniștit.",
            Accent = "#3B82F6", AccentHover = "#60A5FA",
            WindowBg = "#0F141C", ToolbarBg = "#141B25", ContentBg = "#0B1017",
            TabActiveBg = "#1C2735", TabHoverBg = "#17212E",
            BorderSoft = "#223041", BorderStrong = "#30425A", HoverBg = "#1D2939", PressedBg = "#263449",
            OmniboxBg = "#19222F", OmniboxFocus = "#223043",
            TextPrimary = "#E8EEF8", TextSecondary = "#A3B1C6", TextMuted = "#6E8098",
            PopupBg = "#161F2B", InsetBg = "#111925",
            NewTabSurface = "#131B26", NewTabSurface2 = "#1A2533",
        },
        new ThemePalette
        {
            Id = "padure", Name = "Pădure", Tagline = "Verde profund, odihnitor.",
            Accent = "#34D399", AccentHover = "#6EE7B7",
            WindowBg = "#0E1512", ToolbarBg = "#131D19", ContentBg = "#0A110E",
            TabActiveBg = "#1B2A23", TabHoverBg = "#16221C",
            BorderSoft = "#24362C", BorderStrong = "#36503F", HoverBg = "#1D3127", PressedBg = "#25402F",
            OmniboxBg = "#17241D", OmniboxFocus = "#203528",
            TextPrimary = "#E6F2EA", TextSecondary = "#A7BFB1", TextMuted = "#6F8C7D",
            PopupBg = "#131D18", InsetBg = "#0F1713",
            NewTabSurface = "#141F19", NewTabSurface2 = "#1B2921",
        },
        new ThemePalette
        {
            Id = "ametist", Name = "Ametist", Tagline = "Violet discret, seara.",
            Accent = "#A78BFA", AccentHover = "#C4B5FD",
            WindowBg = "#12101A", ToolbarBg = "#181524", ContentBg = "#0E0C16",
            TabActiveBg = "#241E34", TabHoverBg = "#1E192B",
            BorderSoft = "#312A46", BorderStrong = "#453C60", HoverBg = "#2A2340", PressedBg = "#342B4D",
            OmniboxBg = "#201A30", OmniboxFocus = "#2B2342",
            TextPrimary = "#EFEBFA", TextSecondary = "#B4A9CC", TextMuted = "#80759A",
            PopupBg = "#191524", InsetBg = "#14111D",
            NewTabSurface = "#1A1628", NewTabSurface2 = "#231D34",
        },
        new ThemePalette
        {
            Id = "trandafir", Name = "Trandafir", Tagline = "Roz cald, fără zgomot.",
            Accent = "#F472B6", AccentHover = "#F9A8D4",
            WindowBg = "#171017", ToolbarBg = "#1F151E", ContentBg = "#120C12",
            TabActiveBg = "#2C1F2A", TabHoverBg = "#251823",
            BorderSoft = "#3E2B39", BorderStrong = "#553C4E", HoverBg = "#332232", PressedBg = "#3F2A3D",
            OmniboxBg = "#291A27", OmniboxFocus = "#352336",
            TextPrimary = "#F8EDF4", TextSecondary = "#C6A9BD", TextMuted = "#927586",
            PopupBg = "#20161F", InsetBg = "#191119",
            NewTabSurface = "#201520", NewTabSurface2 = "#291D28",
        },
        new ThemePalette
        {
            Id = "grafit", Name = "Grafit", Tagline = "Monocrom, fără distrageri.",
            Accent = "#6B7280", AccentHover = "#9CA3AF",
            WindowBg = "#111214", ToolbarBg = "#17181B", ContentBg = "#0C0D0F",
            TabActiveBg = "#24262A", TabHoverBg = "#1D1F22",
            BorderSoft = "#2B2E33", BorderStrong = "#3C4046", HoverBg = "#272A2E", PressedBg = "#31353A",
            OmniboxBg = "#202226", OmniboxFocus = "#2A2D32",
            TextPrimary = "#ECEDEE", TextSecondary = "#A6A9AD", TextMuted = "#74787D",
            PopupBg = "#181A1D", InsetBg = "#131416",
            NewTabSurface = "#191B1E", NewTabSurface2 = "#222529",
        },
    ];

    /// <summary>The palette currently applied to the UI.</summary>
    public static ThemePalette Current { get; private set; } = All[0];

    public static ThemePalette Get(string? id)
    {
        if (string.Equals(id, SystemId, StringComparison.OrdinalIgnoreCase))
            return All.First(theme => theme.Id == (SystemTheme.IsLight ? LightId : DarkId));
        return All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }

    /// <summary>Theme with the user's custom accent (if any) applied on top.</summary>
    public static ThemePalette Resolve(Settings settings)
    {
        var theme = Get(settings.Theme);
        if (TryParse(settings.CustomAccent, out var accent))
        {
            theme = theme with
            {
                Accent = ToHex(accent),
                AccentHover = ToHex(Lighten(accent, 0.18)),
            };
        }

        return theme;
    }

    /// <summary>Writes the palette into Application.Resources; DynamicResource users update at once.</summary>
    public static ThemePalette Apply(Settings settings)
    {
        var theme = Resolve(settings);
        Current = theme;

        var app = Application.Current;
        if (app is null) return theme;

        var resources = app.Resources;
        resources["AccentColor"] = Parse(theme.Accent);

        SetBrush(resources, "Accent", theme.Accent);
        SetBrush(resources, "AccentHover", theme.AccentHover);
        SetBrush(resources, "WindowBg", theme.WindowBg);
        SetBrush(resources, "ChromeBg", theme.ToolbarBg);
        SetBrush(resources, "ToolbarBg", theme.ToolbarBg);
        SetBrush(resources, "ContentBg", theme.ContentBg);
        SetBrush(resources, "TabActiveBg", theme.TabActiveBg);
        SetBrush(resources, "TabHoverBg", theme.TabHoverBg);
        SetBrush(resources, "BorderSoft", theme.BorderSoft);
        SetBrush(resources, "BorderStrong", theme.BorderStrong);
        SetBrush(resources, "HoverBg", theme.HoverBg);
        SetBrush(resources, "PressedBg", theme.PressedBg);
        SetBrush(resources, "OmniboxBg", theme.OmniboxBg);
        SetBrush(resources, "OmniboxFocus", theme.OmniboxFocus);
        SetBrush(resources, "TextPrimary", theme.TextPrimary);
        SetBrush(resources, "TextSecondary", theme.TextSecondary);
        SetBrush(resources, "TextMuted", theme.TextMuted);
        SetBrush(resources, "PopupBg", theme.PopupBg);
        SetBrush(resources, "InsetBg", theme.InsetBg);

        return theme;
    }

    /// <summary>Colour used behind the new tab page: the theme, a custom solid colour or the image base.</summary>
    public static string ResolveNewTabBackground(ThemePalette theme, Settings settings)
    {
        if (settings.NewTabBackgroundMode is "color" or "image" &&
            TryParse(settings.NewTabBackgroundColor, out var custom))
            return ToHex(custom);

        return theme.ContentBg;
    }

    public static bool TryParse(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7) return false;

        try
        {
            color = Parse(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R + (255 - color.R) * amount, 0, 255),
        (byte)Math.Clamp(color.G + (255 - color.G) * amount, 0, 255),
        (byte)Math.Clamp(color.B + (255 - color.B) * amount, 0, 255));

    public static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush(Parse(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Darker/lighter overlay used on top of the new tab background image.</summary>
    public static string ImageLayer(string url) =>
        "linear-gradient(rgba(8, 9, 11, 0.55), rgba(8, 9, 11, 0.80)), url(\"" + url.Replace("\"", "%22") + "\")";

    private static void SetBrush(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush(Parse(hex));
        brush.Freeze();
        resources[key] = brush;
    }
}
