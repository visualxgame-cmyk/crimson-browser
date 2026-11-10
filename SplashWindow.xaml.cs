using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CrimsonBrowser;

public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();
}

/// <summary>
/// Runs the startup splash on its own STA thread so it keeps painting while the
/// WPF thread loads the native CEF engine. Showing the main window only after the
/// engine is ready keeps Windows from marking it "Not Responding" during the load.
/// </summary>
internal sealed class StartupSplash
{
    private readonly Window _window;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private int _closed;

    private StartupSplash(Window window, Thread thread)
    {
        _window = window;
        _dispatcher = window.Dispatcher;
        _thread = thread;
    }

    public static StartupSplash? Show()
    {
        StartupSplash? splash = null;
        using var ready = new ManualResetEventSlim(false);
        Thread? thread = null;
        thread = new Thread(() =>
        {
            try
            {
                var window = new SplashWindow();
                window.Show();
                splash = new StartupSplash(window, thread!);
            }
            catch
            {
                // A decorative splash must never stop the browser from starting.
            }
            finally
            {
                ready.Set();
            }
            if (splash != null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Crimson startup splash"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        return splash;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                try { _window.Close(); }
                catch { }
                _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }));
            // Wait for the window to leave Application.Windows so shutdown code
            // never enumerates a window owned by the splash thread.
            if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        }
        catch (InvalidOperationException) { }
        catch (TaskCanceledException) { }
        catch (ThreadStateException) { }
    }
}
