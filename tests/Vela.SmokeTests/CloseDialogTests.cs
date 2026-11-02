using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Vela.Models;

namespace Vela.Tests;

internal static partial class Program
{
    /// <summary>
    /// Closing a window with unsaved work must go through Vela's themed card, not a native
    /// Windows message box. This drives the real close request and cancels the result.
    /// </summary>
    private static async Task WindowCloseDialogTests(MainWindow window)
    {
        var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        var tab = tabs.FirstOrDefault(item => item.View?.CoreWebView2 is not null && !item.IsInternalPage);
        if (tab is null)
        {
            Console.WriteLine("SKIP: window close dialog check needs a loaded web tab.");
            return;
        }

        tab.HasUnsavedChanges = true;

        // Start the close request from the dispatcher: it opens a modal dialog and must not
        // block the assertion loop that runs inside the dialog's nested message pump.
        _ = window.Dispatcher.BeginInvoke(new Action(() => Invoke(window, "RequestWindowCloseAsync")));

        MessageDialog? dialog = null;
        await Until(() =>
        {
            dialog = Application.Current.Windows.OfType<MessageDialog>().FirstOrDefault();
            return dialog is not null;
        }, "Window close protection opens a Vela dialog", 15000);

        var name = new StringBuilder(64);
        GetClassName(new WindowInteropHelper(dialog!).Handle, name, name.Capacity);
        Check(name.ToString().StartsWith("HwndWrapper", StringComparison.Ordinal),
            "Window close protection is a WPF Vela card, not the native #32770 message box");
        Check(((TextBlock)dialog!.FindName("TitleText")).Text == "Protecția modificărilor",
            "Window close protection keeps its Romanian themed title");
        Check(((TextBlock)dialog.FindName("MessageText")).Text.Contains("modificări nesalvate"),
            "Window close protection explains the unsaved work in the same card");

        var cancel = (Button)dialog.FindName("SecondaryButton");
        Check((string)cancel.Content == "Anulează",
            "Window close protection offers a cancel action");
        cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        await Until(() => !Application.Current.Windows.OfType<MessageDialog>().Any(),
            "Themed close dialog closes when cancelled");
        Check(window.IsVisible, "Cancelling the themed close dialog keeps the browser open");

        tab.HasUnsavedChanges = false;
    }
}
