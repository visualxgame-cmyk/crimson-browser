using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        var elapsed = Stopwatch.StartNew();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CrimsonThemeSource.xaml")).Root!;
        var theme = new XElement(source.Name.Namespace + "ResourceDictionary",
            source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
            source.Element(source.Name.Namespace + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(theme.ToString());
        app.Startup += async (_, _) =>
        {
            var window = new CrimsonBrowser.MainWindow(deferBrowserStartup: true);
            int result = 0;
            try
            {
                var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ContentRendered += (_, _) => rendered.TrySetResult();
                window.Show();
                await rendered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                using var process = Process.GetCurrentProcess();
                bool nativeLoaded = process.Modules.Cast<ProcessModule>().Any(module =>
                    module.ModuleName.Equals("libcef.dll", StringComparison.OrdinalIgnoreCase));
                if (nativeLoaded || window.Tabs.Count != 0)
                    throw new InvalidOperationException("The startup shell loaded CEF before its first frame.");
                Console.WriteLine($"PASS: first shell frame without loading CEF ({elapsed.ElapsedMilliseconds} ms after managed entry)");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { window.Close(); app.Shutdown(result); }
        };
        return app.Run();
    }
}
