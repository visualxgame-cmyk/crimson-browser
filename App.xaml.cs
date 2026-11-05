using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CrimsonBrowser.Services;

namespace CrimsonBrowser;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private MainWindow? _mainWindow;
    private string? _pendingMessage;

    internal static string? PendingStartupUrl { get; private set; }

    /// <summary>Reads and clears the URL passed on the command line for the primary window.</summary>
    internal static string? TakePendingStartupUrl()
    {
        string? url = PendingStartupUrl;
        PendingStartupUrl = null;
        return url;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsFirstInstance)
        {
            ForwardToRunningInstance(StartupMessage(e.Args));
            Shutdown();
            return;
        }

        _singleInstance.MessageReceived += OnExternalMessage;
        PendingStartupUrl = StartupUrl(e.Args);
        StartupCleanup.RemoveStaleIncognitoProfiles();

        base.OnStartup(e);

        _mainWindow = new MainWindow(restoreSession: true);
        MainWindow = _mainWindow;
        _mainWindow.Show();

        if (_pendingMessage is not null)
        {
            var message = _pendingMessage;
            _pendingMessage = null;
            OnExternalMessage(message);
        }
    }

    private static void ForwardToRunningInstance(string message)
    {
        // The first instance may still be creating its pipe server when a fast second
        // launch arrives, so retry briefly instead of silently dropping the URL.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (SingleInstanceService.Send(message)) return;
            Thread.Sleep(150);
        }
    }

    private static string StartupMessage(string[] args) => StartupUrl(args) ?? "activate";

    private static string? StartupUrl(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (arg == "--") continue;
            if (arg.StartsWith("--", StringComparison.Ordinal)) continue;
            if (arg.StartsWith("-psn", StringComparison.OrdinalIgnoreCase)) continue;
            return arg.Trim('"');
        }
        return null;
    }

    private void OnExternalMessage(string message)
    {
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow is null)
            {
                _pendingMessage = message;
                return;
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;

            _mainWindow.Show();
            _mainWindow.Activate();

            if (!string.Equals(message, "activate", StringComparison.OrdinalIgnoreCase))
                _mainWindow.OpenExternalUrl(message);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("DispatcherUnhandledException", e.Exception);
        var loc = LocalizationService.Instance;
        MessageBox.Show(
            loc.Format("CrashMessage", e.Exception.Message, CrashLogPath),
            loc.Get("CrashTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogException("UnhandledException", ex);
    }

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrimsonBrowser",
        "crash.log");

    private static void LogException(string source, Exception ex)
    {
        try
        {
            string folder = Path.GetDirectoryName(CrashLogPath)!;
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // If logging itself fails there is nothing useful left to do.
        }
    }
}
