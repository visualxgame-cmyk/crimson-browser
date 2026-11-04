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

        UpdateWallpaperStatusText();
        ApplyLocalization();
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
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp",
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
        bool hasCustom = SettingsService.Instance.HasCustomWallpaper;
        bool isEn = LocalizationService.Instance.CurrentLanguage == "en";

        TxtWallpaperStatus.Text = hasCustom 
            ? (isEn ? "Custom Wallpaper: Active" : "Fundal personalizat: Activ")
            : (isEn ? "Default Crimson Stealth Background" : "Fundal implicit Crimson Stealth");
    }

    private void ApplyLocalization()
    {
        var loc = LocalizationService.Instance;

        TxtSettingsTitle.Text = loc.Get("SettingsTitle");
        TxtNavGeneral.Text = "🌐 " + loc.Get("GeneralTab");
        TxtNavAppearance.Text = "🎨 " + loc.Get("AppearanceTab");
        TxtNavShields.Text = "🛡️ " + loc.Get("PrivacyTab");
        TxtNavAbout.Text = "ℹ️ " + loc.Get("MenuAbout");

        TxtLangTitle.Text = loc.Get("LanguageSetting");
        TxtLangDesc.Text = loc.Get("LanguageSettingDesc");

        TxtBgTitle.Text = loc.Get("BackgroundSetting");
        TxtBgDesc.Text = loc.Get("BackgroundSettingDesc");
        TxtChooseImgBtn.Text = loc.Get("ChooseImageBtn");
        TxtResetImgBtn.Text = loc.Get("ResetBackgroundBtn");

        TxtShieldsTitle.Text = loc.Get("PrivacyTab");

        TxtCloseBtn.Text = loc.Get("CloseSettings");

        UpdateWallpaperStatusText();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
