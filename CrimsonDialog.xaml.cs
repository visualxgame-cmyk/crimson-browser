using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CrimsonBrowser;

/// <summary>Small Crimson-themed replacement for the stock Windows MessageBox.</summary>
public partial class CrimsonDialog : Window
{
    private CrimsonDialog(string title, string message, string okText, string? cancelText, bool danger)
    {
        InitializeComponent();

        TitleText.Text  = title;
        MessageText.Text = message;
        OkText.Text     = okText;

        if (cancelText is null)
        {
            CancelBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelText.Text = cancelText;
        }

        if (danger)
        {
            OkBtn.Background = new SolidColorBrush(Color.FromRgb(230, 25, 55));
            DialogIcon.Data = Geometry.Parse("M9 1 L17 16 L1 16 Z M9 6 L9 11 M9 13 L9 14.5");
        }
    }

    /// <summary>Shows a modal dialog. Returns true when the primary button was pressed.</summary>
    public static bool Show(
        Window? owner,
        string title,
        string message,
        string okText = "OK",
        string? cancelText = null,
        bool danger = false)
    {
        var dialog = new CrimsonDialog(title, message, okText, cancelText, danger);

        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }

        return dialog.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message) =>
        Show(owner, title, message, "OK");

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
