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
    private readonly Queue<string> _pendingMessages = new();
    private bool _shuttingDown;
    private readonly HashSet<string> _reportedStorageErrors = new(StringComparer.Ordinal);

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

        base.OnStartup(e);
        ProfileStorageDiagnostics.Failed += ProfileStorageFailed;

        try
        {
            _mainWindow = new MainWindow(restoreSession: true, deferBrowserStartup: true);
            MainWindow = _mainWindow;
            _mainWindow.ContentRendered += MainWindow_FirstContentRendered;
            _mainWindow.Show();
            ShieldService.Instance.PropertyChanged += PopupSettingsChanged;
        }
        catch (Exception ex)
        {
            LogException("Startup", ex);
            MessageBox.Show(ex.Message, "Crimson Browser", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        while (_pendingMessages.TryDequeue(out string? message))
        {
            OnExternalMessage(message);
        }
    }

    private void MainWindow_FirstContentRendered(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.ContentRendered -= MainWindow_FirstContentRendered;
        // Give WPF its first frame before loading the native CEF engine. Initialize
        // and Shutdown remain on this same application thread, as required by CEF.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_shuttingDown || !window.IsLoaded) return;
            try
            {
                CefRuntime.Initialize();
                window.CompleteBrowserStartup();
                _ = Task.Run(StartupCleanup.RemoveStaleIncognitoProfiles);
            }
            catch (Exception ex)
            {
                LogException("Engine startup", ex);
                MessageBox.Show(window, ex.Message, "Crimson Browser — CEF", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }), DispatcherPriority.Background);
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
                _pendingMessages.Enqueue(message);
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
        _shuttingDown = true;
        foreach (var window in Windows.OfType<MainWindow>().ToArray()) window.DisposeBrowsers();
        bool historySaved = HistoryService.FlushIfCreated();
        bool downloadsSaved = DownloadsService.FlushIfCreated();
        if ((!historySaved && !_reportedStorageErrors.Contains("history.json")) ||
            (!downloadsSaved && !_reportedStorageErrors.Contains("downloads.json")))
            MessageBox.Show(LocalizationService.Instance.Get("StorageExitError"),
                LocalizationService.Instance.Get("StorageErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        ProfileStorageDiagnostics.Failed -= ProfileStorageFailed;
        if (_mainWindow != null) ShieldService.Instance.PropertyChanged -= PopupSettingsChanged;
        if (CefRuntime.IsInitialized) CefRuntime.Shutdown();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void ProfileStorageFailed(string file, Exception error)
    {
        LogException("Profile storage: " + file, error);
        if (_shuttingDown || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_shuttingDown || !_reportedStorageErrors.Add(file)) return;
            var loc = LocalizationService.Instance;
            CrimsonDialog.Info(_mainWindow, loc.Get("StorageErrorTitle"), loc.Format("StorageErrorMessage", file, error.Message));
        }));
    }

    private void PopupSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(ShieldService.IsShieldsEnabled) or nameof(ShieldService.BlockPopups) or
            nameof(ShieldService.BlockAdsAndTrackers))) return;
        bool adsChanged = args.PropertyName != nameof(ShieldService.BlockPopups);
        foreach (var window in Windows.OfType<MainWindow>())
            foreach (var tab in window.Tabs)
            {
                tab.UpdatePopupProtection();
                if (adsChanged) tab.UpdateAdProtection();
            }
        foreach (var window in Windows.OfType<IncognitoWindow>())
            foreach (var tab in window.Tabs)
            {
                tab.UpdatePopupProtection();
                if (adsChanged) tab.UpdateAdProtection();
            }
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
