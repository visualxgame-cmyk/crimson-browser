using System.Windows;
using System.Windows.Input;

namespace Vela;

/// <summary>
/// Themed replacement for <see cref="MessageBox"/>. Native Windows message boxes break the
/// Brave-like look (light chrome, system font, square corners), so close/download/data
/// confirmations and informational notices are shown in this rounded card instead.
/// </summary>
public partial class MessageDialog : Window
{
    private readonly bool _confirm;

    private MessageDialog(string title, string message, string primary, string? secondary)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;

        _confirm = secondary is not null;
        if (secondary is null) SecondaryButton.Visibility = Visibility.Collapsed;
        else SecondaryButton.Content = secondary;

        // Keyboard first: Enter confirms, Escape cancels, Tab walks the two buttons.
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    /// <summary>Asks a yes/no question. Returns true only when the primary button is chosen.</summary>
    public static bool Confirm(Window? owner, string title, string message,
        string confirmText = "Continuă", string cancelText = "Anulează")
    {
        var dialog = new MessageDialog(title, message, confirmText, cancelText) { Owner = Resolve(owner) };
        return dialog.ShowDialog() == true;
    }

    /// <summary>Shows an informational notice with a single button.</summary>
    public static void Inform(Window? owner, string title, string message, string okText = "OK")
    {
        var dialog = new MessageDialog(title, message, okText, null) { Owner = Resolve(owner) };
        dialog.ShowDialog();
    }

    private static Window? Resolve(Window? owner) => owner ?? Application.Current?.MainWindow;

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = _confirm;

    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch { /* not dragging */ }
    }
}
