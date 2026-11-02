using System.Windows;
using System.Windows.Threading;
using Vela.Services;

namespace Vela;

public partial class App : Application
{
    private BrowserInstance? _instance;
    private System.Threading.Mutex? _installationMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        try { ProfileContext.Initialize(e.Args, AppPaths.BaseRoot); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Vela — profil", MessageBoxButton.OK, MessageBoxImage.Warning); Shutdown(); return; }
        AppPaths.EnsureCreated();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("FATAL: " + args.ExceptionObject);

        // Theme must be in place before MainWindow.xaml is parsed, otherwise the first paint
        // would flash the default palette.
        ThemeCatalog.Apply(SettingsStore.Load());

        base.OnStartup(e);
        _instance = new BrowserInstance(AppPaths.Root);
        var targets = BrowserInstance.NavigationTargets(e.Args);
        if (!_instance.IsPrimary)
        {
            try { await _instance.SendAsync(targets); }
            catch (Exception ex)
            {
                Log.Write("Activate existing browser: " + ex.Message);
                MessageBox.Show("Instanța Vela deschisă nu răspunde. Încearcă din nou după închiderea ei.", "Vela", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }
        _installationMutex = new System.Threading.Mutex(false, @"Local\VelaBrowser.Running");
        _instance.Listen(urls => Dispatcher.BeginInvoke(() =>
        {
            var windows = Windows.OfType<MainWindow>().ToArray();
            var target = windows.FirstOrDefault(w => w.IsActive && !w.IsPrivate) ?? windows.FirstOrDefault(w => !w.IsPrivate);
            if (target is null) { target = new MainWindow(windows.FirstOrDefault()?.BrowserEnvironment); target.Show(); }
            target.OpenExternalUrls(urls);
        }));
        var window = new MainWindow();
        MainWindow = window;
        window.OpenExternalUrls(targets, activate: false);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _installationMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("UNHANDLED: " + e.Exception);

        // A broken handler should not take the whole browser down.
        e.Handled = true;
        MessageBox.Show(
            "A apărut o eroare neașteptată:\n\n" + e.Exception.Message +
            "\n\nDetalii în " + System.IO.Path.Combine(AppPaths.Logs, "vela.log"),
            "Vela", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
