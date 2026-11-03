using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private const string GlyphCopy = "\uE8C8";
    private const string GlyphSearch = "\uE721";
    private const string GlyphLink = "\uE71B";
    private const string GlyphTab = "\uE710";
    private const string GlyphPrivate = "\uE727";
    private const string GlyphImage = "\uEB9F";
    private const string GlyphBack = "\uE72B";
    private const string GlyphForward = "\uE72A";
    private const string GlyphReload = "\uE72C";
    private const string GlyphPdf = "\uE8A5";
    private const string GlyphFind = "\uE721";
    private const string GlyphInspect = "\uE7B8";
    private const string GlyphSource = "\uE943";
    private const string GlyphStarOutline = "\uE734";
    private const string GlyphStarFilled = "\uE735";

    private void AttachPageContextMenu(BrowserTab tab, CoreWebView2 core)
    {
        core.ContextMenuRequested -= OnPageContextMenuRequested;
        core.ContextMenuRequested += OnPageContextMenuRequested;
    }

    /// <summary>
    /// Replaces Chromium's own page menu with Vela's themed one. The menu mirrors the page
    /// target (link, image, selection, editable field) so it is more useful than a generic
    /// list while still keeping every WebView2 command available through the page itself.
    /// </summary>
    private void OnPageContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;
        var tab = _tabs.FirstOrDefault(candidate => candidate.View?.CoreWebView2 == core);
        if (tab is null) return;

        using var deferral = e.GetDeferral();
        var target = e.ContextMenuTarget;
        var menu = new ContextMenu { PlacementTarget = tab.View, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        void Add(string label, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, Icon = MakeMenuIcon(glyph), IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        void Separator() => menu.Items.Add(new Separator());

        var selection = target.HasSelection ? target.SelectionText?.Trim() : null;

        if (!string.IsNullOrEmpty(selection))
        {
            Add("Copiază selecția", GlyphCopy, () => CopyText(selection));
            var query = selection.Length > 500 ? selection[..500] : selection;
            Add($"Caută „{Shorten(query, 40)}”", GlyphSearch,
                () => _ = AddTabAsync(_settings.SearchUrl + Uri.EscapeDataString(query)));
            Separator();
        }

        if (target.HasLinkUri && Uri.TryCreate(target.LinkUri, UriKind.Absolute, out var link))
        {
            Add("Deschide legătura în tab nou", GlyphTab, () => _ = AddTabAsync(link.AbsoluteUri, select: false));
            Add("Deschide legătura în prim-plan", GlyphTab, () => _ = AddTabAsync(link.AbsoluteUri));
            Add("Deschide legătura în fereastră privată", GlyphPrivate, () => _ = OpenInPrivateWindowAsync(link.AbsoluteUri));
            Add("Copiază adresa legăturii", GlyphLink, () => CopyText(link.AbsoluteUri));
            if (!string.IsNullOrWhiteSpace(target.LinkText))
                Add("Copiază textul legăturii", GlyphCopy, () => CopyText(target.LinkText));
            Separator();
        }

        if (target.Kind == CoreWebView2ContextMenuTargetKind.Image &&
            target.HasSourceUri && Uri.TryCreate(target.SourceUri, UriKind.Absolute, out var image))
        {
            Add("Deschide imaginea în tab nou", GlyphImage, () => _ = AddTabAsync(image.AbsoluteUri, select: false));
            Add("Copiază adresa imaginii", GlyphCopy, () => CopyText(image.AbsoluteUri));
            Separator();
        }

        if (target.IsEditable)
        {
            Add("Anulează", GlyphBack, () => DispatchEditCommand(core, "z"));
            Add("Refă", GlyphForward, () => DispatchEditCommand(core, "y"));
            Separator();
            Add("Taie", GlyphCopy, () => DispatchEditCommand(core, "x"));
            Add("Copiază", GlyphCopy, () => DispatchEditCommand(core, "c"));
            Add("Lipește", GlyphCopy, () => DispatchEditCommand(core, "v"));
            Add("Selectează tot", GlyphFind, () => DispatchEditCommand(core, "a"));
            Separator();
        }

        Add("Înapoi", GlyphBack, () => { try { if (core.CanGoBack) core.GoBack(); } catch { } }, core.CanGoBack);
        Add("Înainte", GlyphForward, () => { try { if (core.CanGoForward) core.GoForward(); } catch { } }, core.CanGoForward);
        Add("Reîncarcă", GlyphReload, () => { try { core.Reload(); } catch { } });

        var address = target.PageUri ?? tab.Url;
        var canBookmark = !tab.IsInternalPage &&
            Uri.TryCreate(address, UriKind.Absolute, out var pageUri) && pageUri.Scheme is "http" or "https";
        var bookmarked = canBookmark && _bookmarks.Contains(address);
        Add(bookmarked ? "Elimină din favorite" : "Adaugă la favorite",
            bookmarked ? GlyphStarFilled : GlyphStarOutline,
            () =>
            {
                var added = _bookmarks.Toggle(address, tab.Title);
                Status(added ? "Adăugat la favorite." : "Eliminat din favorite.");
            }, canBookmark);

        Separator();
        Add("Caută în pagină…", GlyphFind, () => _ = ShowFindAsync());
        Add("Salvează pagina ca PDF…", GlyphPdf, () => SavePdf_Click(this, new RoutedEventArgs()));
        if (target.PageUri is { Length: > 0 } pageText && Uri.TryCreate(pageText, UriKind.Absolute, out var sourcePage) &&
            sourcePage.Scheme is "http" or "https")
        {
            Add("Vezi codul sursă", GlyphSource, () => _ = AddTabAsync("view-source:" + sourcePage.AbsoluteUri, select: false));
        }

        if (core.Settings.AreDevToolsEnabled)
        {
            Separator();
            Add("Inspectează", GlyphInspect, () =>
            {
                try { core.OpenDevToolsWindow(); }
                catch (Exception ex) { Log.Write("Open DevTools: " + ex.Message); Status("Instrumentele dezvoltatorului nu au putut fi deschise."); }
            });
        }

        e.Handled = true;
        menu.IsOpen = true;
    }

    private static TextBlock MakeMenuIcon(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"],
        FontSize = 12,
        Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextSecondary"],
        Width = 16,
        TextAlignment = TextAlignment.Center,
    };

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

    private void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Status("Copiat în clipboard.", 3); }
        catch (Exception ex) { Log.Write("Copy text: " + ex.Message); }
    }

    /// <summary>
    /// Editing commands are sent as real keyboard input; Chromium grants clipboard access to
    /// input events, which <c>execCommand('paste')</c> from script would not get.
    /// </summary>
    private static async void DispatchEditCommand(CoreWebView2 core, string key)
    {
        var virtualKey = char.ToUpperInvariant(key[0]);
        var parameters = JsonSerializer.Serialize(new
        {
            type = "rawKeyDown",
            modifiers = 2, // Ctrl
            windowsVirtualKeyCode = (int)virtualKey,
            nativeVirtualKeyCode = (int)virtualKey,
            key,
        });
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", parameters);
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                JsonSerializer.Serialize(new { type = "keyUp", modifiers = 2, windowsVirtualKeyCode = (int)virtualKey, nativeVirtualKeyCode = (int)virtualKey, key }));
        }
        catch (Exception ex) { Log.Write("Edit command: " + ex.Message); }
    }
}
