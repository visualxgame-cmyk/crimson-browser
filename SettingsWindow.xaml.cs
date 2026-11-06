using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CrimsonBrowser.Services;

namespace CrimsonBrowser;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _mainWindow;

    public SettingsWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        Owner = mainWindow;

        InitializeComponent();

        Loaded += SettingsWindow_Loaded;
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        bool isEn = SettingsService.Instance.Language == "en";
        RadioLangEn.IsChecked = isEn;
        RadioLangRo.IsChecked = !isEn;

        switch (SettingsService.Instance.SearchEngine)
        {
            case "DuckDuckGo": RadioDuckDuckGo.IsChecked = true; break;
            case "Brave":      RadioBrave.IsChecked = true; break;
            case "Bing":       RadioBing.IsChecked = true; break;
            default:           RadioGoogle.IsChecked = true; break;
        }

        AutoUpdateCheck.IsChecked = SettingsService.Instance.AutoCheckUpdates;

        UpdateWallpaperStatusText();
        ApplyLocalization();
    }

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || AutoUpdateCheck == null) return;
        SettingsService.Instance.AutoCheckUpdates = AutoUpdateCheck.IsChecked == true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            PaneGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            PaneAppearance.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            PaneSearch.Visibility = tag == "Search" ? Visibility.Visible : Visibility.Collapsed;
            PaneShields.Visibility = tag == "Shields" ? Visibility.Visible : Visibility.Collapsed;
            PaneAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || RadioLangEn == null || RadioLangRo == null) return;

        if (RadioLangEn.IsChecked == true)
        {
            SettingsService.Instance.Language = "en";
        }
        else if (RadioLangRo.IsChecked == true)
        {
            SettingsService.Instance.Language = "ro";
        }

        ApplyLocalization();
        _mainWindow.ApplyLocalizationToAllUi();
    }

    private void SearchEngine_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || RadioGoogle == null) return;

        if (RadioDuckDuckGo.IsChecked == true)     SettingsService.Instance.SearchEngine = "DuckDuckGo";
        else if (RadioBrave.IsChecked == true)     SettingsService.Instance.SearchEngine = "Brave";
        else if (RadioBing.IsChecked == true)      SettingsService.Instance.SearchEngine = "Bing";
        else if (RadioGoogle.IsChecked == true)    SettingsService.Instance.SearchEngine = "Google";
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = LocalizationService.Instance.Get("ImagesFilter"),
            Title = LocalizationService.Instance.Get("ChooseImageBtn")
        };

        if (dlg.ShowDialog(this) == true)
        {
            SettingsService.Instance.SetCustomBackgroundImage(dlg.FileName);
            UpdateWallpaperStatusText();
            _mainWindow.RefreshAllStartPages();
        }
    }

    private void ResetImage_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.ResetBackgroundImage();
        UpdateWallpaperStatusText();
        _mainWindow.RefreshAllStartPages();
    }

    private void UpdateWallpaperStatusText()
    {
        var loc = LocalizationService.Instance;
        TxtWallpaperStatus.Text = loc.Get(
            SettingsService.Instance.HasCustomWallpaper ? "WallpaperCustomActive" : "DefaultBackground");
    }

    private void ApplyLocalization()
    {
        // Static labels are bound with {loc:Loc ...}; only computed values need a refresh.
        VersionText.Text = LocalizationService.Instance.Format(
            "SettingsVersion", UpdateService.CurrentVersion);
        UpdateWallpaperStatusText();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        var loc = LocalizationService.Instance;
        UpdateStatusText.Text = loc.Get("CheckingUpdates");

        try
        {
            UpdateCheckResult result = await UpdateService.CheckAsync();
            if (result.Status == UpdateCheckStatus.NoPublishedRelease)
            {
                UpdateStatusText.Text = loc.Get("NoPublishedRelease");
                return;
            }

            if (result.Status == UpdateCheckStatus.Current)
            {
                UpdateStatusText.Text = loc.Format("UpdatesCurrent", UpdateService.CurrentVersion);
                return;
            }

            UpdateInfo update = result.Update!;

            UpdateStatusText.Text = loc.Format("UpdateAvailable", update.Version);
            if (CrimsonDialog.Show(this, loc.Get("UpdateAvailableTitle"),
                    loc.Format("UpdateAvailableMessage", update.ReleaseName, update.Version),
                    loc.Get("DownloadAndInstall"), loc.Get("DialogCancel")))
            {
                UpdateStatusText.Text = loc.Get("DownloadingUpdate");
                var progress = new Progress<double>(value =>
                    UpdateStatusText.Text = loc.Format("DownloadingUpdateProgress", Math.Round(value * 100)));
                await UpdateService.DownloadAndInstallAsync(update, progress);
            }
        }
        catch (Exception)
        {
            UpdateStatusText.Text = loc.Get("UpdateInstallFailed");
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }
}
