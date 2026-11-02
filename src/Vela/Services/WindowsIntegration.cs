using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Vela.Services;

internal sealed record BrowserRegistration(string Path, string Name, string Value);

internal static class WindowsIntegration
{
    private const string Capabilities = @"Software\Clients\StartMenuInternet\Vela\Capabilities";
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    internal static IReadOnlyList<BrowserRegistration> RegistrationPlan(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Calea executabilului Vela nu este validă.");
        var quoted = "\"" + executable + "\"";
        var command = quoted + " -- \"%1\"";
        return
        [
            new(@"Software\Classes\VelaHTML", "", "Vela HTML Document"),
            new(@"Software\Classes\VelaHTML", "URL Protocol", ""),
            new(@"Software\Classes\VelaHTML\DefaultIcon", "", quoted + ",0"),
            new(@"Software\Classes\VelaHTML\shell\open\command", "", command),
            new(@"Software\Clients\StartMenuInternet\Vela", "", "Vela"),
            new(@"Software\Clients\StartMenuInternet\Vela\DefaultIcon", "", quoted + ",0"),
            new(@"Software\Clients\StartMenuInternet\Vela\shell\open\command", "", quoted),
            new(Capabilities, "ApplicationName", "Vela"),
            new(Capabilities, "ApplicationDescription", "Browser Windows cu consum redus de resurse"),
            new(Capabilities, "ApplicationIcon", quoted + ",0"),
            new(Capabilities + @"\URLAssociations", "http", "VelaHTML"),
            new(Capabilities + @"\URLAssociations", "https", "VelaHTML"),
            new(Capabilities + @"\FileAssociations", ".htm", "VelaHTML"),
            new(Capabilities + @"\FileAssociations", ".html", "VelaHTML"),
            new(@"Software\RegisteredApplications", "Vela", Capabilities)
        ];
    }

    public static void OpenDefaultApps()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executabilul Vela nu a putut fi identificat.");
        foreach (var entry in RegistrationPlan(executable))
        {
            using var key = Registry.CurrentUser.CreateSubKey(entry.Path);
            key.SetValue(entry.Name, entry.Value, RegistryValueKind.String);
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Vela") { UseShellExecute = true });
    }
}
