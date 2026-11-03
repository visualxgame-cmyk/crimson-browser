using Microsoft.Win32;

namespace Vela.Services;

/// <summary>
/// Reads the Windows app-theme preference and raises <see cref="Changed"/> when the user flips
/// between light and dark in Settings. The event can arrive on a system thread; subscribers are
/// expected to marshal to their UI dispatcher.
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    static SystemTheme()
    {
        IsLight = Read();
        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
                    Refresh();
            };
        }
        catch (Exception ex) { Log.Write("System theme watcher: " + ex.Message); }
    }

    /// <summary>True when Windows apps are configured to use the light theme.</summary>
    public static bool IsLight { get; private set; }

    public static event Action? Changed;

    private static bool Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch { return false; }
    }

    private static void Refresh()
    {
        var light = Read();
        if (light == IsLight) return;
        IsLight = light;
        Changed?.Invoke();
    }
}
