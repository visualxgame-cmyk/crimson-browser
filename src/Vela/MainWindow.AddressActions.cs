using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private void Omnibox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu();
        void Command(string label, ICommand command) => menu.Items.Add(new MenuItem { Header = label, Command = command, CommandTarget = Omnibox });
        void Action(string label, System.Action action)
        {
            var item = new MenuItem { Header = label }; item.Click += (_, _) => { try { action(); } catch (Exception ex) { Log.Write("Address bar action: " + ex.Message); Status("Acțiunea nu a putut fi executată."); } }; menu.Items.Add(item);
        }
        Command("Anulează", ApplicationCommands.Undo); menu.Items.Add(new Separator());
        Command("Decupează", ApplicationCommands.Cut); Command("Copiază selecția", ApplicationCommands.Copy); Command("Lipește", ApplicationCommands.Paste); Command("Selectează tot", ApplicationCommands.SelectAll);
        menu.Items.Add(new Separator());
        Action("Lipește și accesează", () => PasteAddress(searchOnly: false));
        Action("Lipește și caută", () => PasteAddress(searchOnly: true));
        Action("Copiază adresa completă", () => { if (_active is { } tab) Clipboard.SetText(tab.Reader?.SourceUrl ?? tab.Url); });
        Omnibox.ContextMenu = menu;
    }
    private void PasteAddress(bool searchOnly)
    {
        if (!Clipboard.ContainsText()) return;
        var text = Clipboard.GetText().Trim();
        if (text.Length == 0) return;
        if (text.Length > 8192) { Status("Textul din clipboard este prea lung pentru o adresă sau o căutare."); return; }
        _ = NavigateActiveAsync(searchOnly ? _settings.SearchUrl + Uri.EscapeDataString(text) : UrlHelper.ToNavigationUrl(text, _settings.SearchUrl));
    }
}
