using System.Windows;
using System.Windows.Threading;
using Vela.Services;

namespace Vela;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureCreated();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("FATAL: " + args.ExceptionObject);

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("UNHANDLED: " + e.Exception);

        // A broken handler should not take the whole browser down.
        e.Handled = true;
        MessageBox.Show(
            "A apărut o eroare neașteptată:\n\n" + e.Exception.Message +
            "\n\nDetalii în %LOCALAPPDATA%\\Vela\\Logs\\vela.log",
            "Vela", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
