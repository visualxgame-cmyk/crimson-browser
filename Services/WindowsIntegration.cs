using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CrimsonBrowser.Services;

/// <summary>
/// Registers Crimson Browser as a candidate default browser in the current user's registry
/// and opens the Windows default-apps panel so the user can confirm the choice.
/// No administrator rights are required.
/// </summary>
internal static class WindowsIntegration
{
    internal const string ProgId = "CrimsonBrowserHTML";
    private const string AppName = "Crimson Browser";
    private const string Capabilities = @"Software\Clients\StartMenuInternet\CrimsonBrowser\Capabilities";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private readonly record struct Registration(string Path, string Name, string Value);

    private static IReadOnlyList<Registration> RegistrationPlan(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) ||
            executable.Contains('"') ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Crimson Browser executable path is not valid.");
        }

        string quoted  = "\"" + executable + "\"";
        string command = quoted + " \"%1\"";

        return
        [
            new(@"Software\Classes\" + ProgId, "", "Crimson HTML Document"),
            new(@"Software\Classes\" + ProgId, "URL Protocol", ""),
            new(@"Software\Classes\" + ProgId + @"\DefaultIcon", "", quoted + ",0"),
            new(@"Software\Classes\" + ProgId + @"\shell\open\command", "", command),

            new(@"Software\Clients\StartMenuInternet\CrimsonBrowser", "", AppName),
            new(@"Software\Clients\StartMenuInternet\CrimsonBrowser\DefaultIcon", "", quoted + ",0"),
            new(@"Software\Clients\StartMenuInternet\CrimsonBrowser\shell\open\command", "", quoted),

            new(Capabilities, "ApplicationName", AppName),
            new(Capabilities, "ApplicationDescription", "A fast, low-memory browser with Crimson Shields privacy protection"),
            new(Capabilities, "ApplicationIcon", quoted + ",0"),
            new(Capabilities + @"\URLAssociations", "http",  ProgId),
            new(Capabilities + @"\URLAssociations", "https", ProgId),
            new(Capabilities + @"\FileAssociations", ".htm",  ProgId),
            new(Capabilities + @"\FileAssociations", ".html", ProgId),

            new(@"Software\RegisteredApplications", "CrimsonBrowser", Capabilities)
        ];
    }

    /// <summary>True when Windows currently routes HTTP links to Crimson Browser.</summary>
    public static bool IsDefaultBrowser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            return string.Equals(key?.GetValue("ProgId") as string, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes the registration entries and opens the Windows default-apps panel.</summary>
    public static void Register()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The Crimson Browser executable could not be located.");

        foreach (var entry in RegistrationPlan(executable))
        {
            using var key = Registry.CurrentUser.CreateSubKey(entry.Path);
            key.SetValue(entry.Name, entry.Value, RegistryValueKind.String);
        }

        // Tell Explorer that file/URL associations changed.
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=CrimsonBrowser")
        {
            UseShellExecute = true
        });
    }
}
