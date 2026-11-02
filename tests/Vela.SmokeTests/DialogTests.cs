using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Vela.Tests;

internal static partial class Program
{
    /// <summary>
    /// The close/download prompts must be Vela's own rounded card, not a native Windows
    /// message box. These checks cover both the layout and the active palette.
    /// </summary>
    private static void MessageDialogTests()
    {
        var create = typeof(MessageDialog).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(string), typeof(string), typeof(string), typeof(string)], null)!;

        MessageDialog Build(string title, string message, string primary, string? secondary)
        {
            var dialog = (MessageDialog)create.Invoke([title, message, primary, secondary]);
            dialog.Show();
            dialog.UpdateLayout();
            return dialog;
        }

        var confirm = Build("Protecția modificărilor", "Pagina are modificări nesalvate.",
            "Închide pagina", "Anulează");
        var primary = (Button)confirm.FindName("PrimaryButton");
        var secondary = (Button)confirm.FindName("SecondaryButton");
        var card = (Border)confirm.FindName("Card");
        var title = (TextBlock)confirm.FindName("TitleText");

        Check((string)primary.Content == "Închide pagina" &&
              secondary.Visibility == Visibility.Visible && (string)secondary.Content == "Anulează",
            "Close confirmation offers a primary action and a cancel action in the custom dialog");
        Check(primary.IsDefault && secondary.IsCancel,
            "Custom dialog keeps Enter for confirm and Escape for cancel");
        Check(title.Text == "Protecția modificărilor" && card.CornerRadius.TopLeft >= 10,
            "Custom dialog renders its title inside a rounded browser card");

        var palette = (SolidColorBrush)Application.Current.Resources["PopupBg"];
        Check(card.Background is SolidColorBrush surface && surface.Color == palette.Color,
            "Dialog surface follows the active theme palette");
        Check(confirm.Width == 480 && confirm.SizeToContent == SizeToContent.Height,
            "Dialog is a compact card instead of a full window");
        confirm.Close();

        var info = Build("Despre Vela", "Vela", "OK", null);
        Check(((Button)info.FindName("SecondaryButton")).Visibility == Visibility.Collapsed,
            "Informational dialog shows a single button");
        info.Close();

        bool Answer(string button)
        {
            var dialog = (MessageDialog)create.Invoke(["Confirmare", "Mesaj", "Da", "Nu"]);
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
                ((Button)dialog.FindName(button)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))));
            return dialog.ShowDialog() == true;
        }

        Check(Answer("PrimaryButton"), "Choosing the primary action closes the dialog with a positive answer");
        Check(!Answer("SecondaryButton"), "Choosing cancel closes the dialog with a negative answer");
    }
}
