namespace CrimsonBrowser;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Render/GPU/utility processes must enter CEF before WPF startup or the
        // single-instance service. All Chromium processes use Crimson's executable.
        int exitCode = CefSharp.BrowserSubprocess.SelfHost.Main(args);
        if (exitCode >= 0) return exitCode;

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
