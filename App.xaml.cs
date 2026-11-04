using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CrimsonBrowser;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        base.OnStartup(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("DispatcherUnhandledException", e.Exception);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("CurrentDomain_UnhandledException", ex);
        }
    }

    private static void LogException(string source, Exception ex)
    {
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
        File.AppendAllText(logPath, $"[{DateTime.Now}] [{source}]\n{ex}\n\n");
    }
}
