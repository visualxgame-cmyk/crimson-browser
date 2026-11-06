using System.Windows;
using CrimsonBrowser.Services;

namespace CrimsonBrowser;

/// <summary>Non-modal progress window shown while a GitHub update is downloaded.</summary>
public partial class UpdateProgressDialog : Window
{
    public UpdateProgressDialog(Window owner, Version version)
    {
        InitializeComponent();

        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var loc = LocalizationService.Instance;
        TitleText.Text = loc.Get("UpdateProgressTitle");
        MessageText.Text = loc.Format("UpdateProgressMessage", version);
        NoteText.Text = loc.Get("UpdateProgressNote");
    }

    public void SetProgress(double value)
    {
        double percent = Math.Clamp(value, 0.0, 1.0);
        UpdateProgress.Value = percent;
        ProgressText.Text = $"{Math.Round(percent * 100)}%";
    }
}
