using System.Runtime.CompilerServices;

namespace CrimsonBrowser;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Render/GPU/utility processes must enter CEF before WPF startup or the
        // single-instance service. All Chromium processes use Crimson's executable.
        if (args.Any(arg => arg == "--type" || arg.StartsWith("--type=", StringComparison.Ordinal)))
            return RunSubprocess(args);
        return RunBrowser();
    }

    // Keep CEF and WPF entry points separate so JIT resolution does not load the
    // other runtime into a process before it is needed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunSubprocess(string[] args) => CefSharp.BrowserSubprocess.SelfHost.Main(args);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunBrowser()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
