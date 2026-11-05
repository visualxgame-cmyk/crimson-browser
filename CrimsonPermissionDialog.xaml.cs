using System.Windows;
using System.Windows.Input;
using CrimsonBrowser.Services;

namespace CrimsonBrowser;

/// <summary>The user's answer to a themed site-permission prompt.</summary>
public readonly record struct PermissionDecision(bool Allowed, bool Remember);

/// <summary>Crimson-themed prompt shown when a website asks for a browser permission.</summary>
public partial class CrimsonPermissionDialog : Window
{
    private CrimsonPermissionDialog(string origin, string permission, bool canPersist)
    {
        InitializeComponent();

        var loc = LocalizationService.Instance;

        OriginText.Text  = origin;
        RequestText.Text = loc.Format("PermissionWants", permission);
        RememberCheck.Content = loc.Get(canPersist
            ? "PermissionRemember"
            : "PermissionRememberIncognito");
    }

    public static PermissionDecision Show(Window? owner, string origin, string permission, bool canPersist)
    {
        var dialog = new CrimsonPermissionDialog(origin, permission, canPersist);

        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }

        bool allowed = dialog.ShowDialog() == true;
        return new PermissionDecision(allowed, dialog.RememberCheck.IsChecked == true);
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Block_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
