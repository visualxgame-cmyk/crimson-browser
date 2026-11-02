using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Vela.Models;
using Vela.Services;
using Microsoft.Web.WebView2.Core;

namespace Vela.Tests;

internal static partial class Program
{
    private static async Task PublicSiteTests()
    {
        var window = new MainWindow(); window.Show(); var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Public test engine initializes", 30000);
        var core = tabs[0].View!.CoreWebView2;
        await ScriptUntil(core, "location.hostname==='vela.internal' && document.readyState==='complete'", "Public tests start in an isolated local page");
        var results = new List<object>(); var failures = new List<string>();
        async Task Navigate(string url)
        {
            var completion = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
            ulong? navigationId = null;
            EventHandler<CoreWebView2NavigationStartingEventArgs> starting = (_, e) =>
            { if (navigationId is not null || Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Equals(new Uri(url))) navigationId = e.NavigationId; };
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (_, e) =>
            { if (e.NavigationId == navigationId) completion.TrySetResult(e); };
            core.NavigationStarting += starting;
            core.NavigationCompleted += handler;
            try
            {
                core.Navigate(url); var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
                if (!result.IsSuccess) throw new IOException("Native navigation: " + result.WebErrorStatus);
            }
            finally { core.NavigationStarting -= starting; core.NavigationCompleted -= handler; }
        }
        foreach (var url in new[] { "https://www.wikipedia.org/", "https://learn.microsoft.com/en-us/microsoft-edge/webview2/", "https://www.youtube.com/" })
        {
            try
            {
                await Navigate(url);
                await ScriptUntil(core, "document.title.trim().length>0", "Public page title becomes available: " + new Uri(url).Host);
                var title = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.title"));
                if (string.IsNullOrWhiteSpace(title)) throw new IOException("The loaded page has no title.");
                var env = window.BrowserEnvironment!;
                (int, string)[] Ids() => env.GetProcessInfos().Select(p => ((int)p.ProcessId, p.Kind.ToString())).Append((Environment.ProcessId, "Application")).ToArray();
                var before = PerformanceProbe.Capture(Ids());
                await Task.Delay(5000); // Defined post-navigation measurement interval; no artificial idle claim.
                var after = PerformanceProbe.Capture(Ids());
                results.Add(new { url, success = true, ramMB = after.WorkingSetBytes / 1048576d, cpuPercentFiveSecondsAfterNavigation = PerformanceProbe.CpuPercent(before, after), processes = after.Processes.Count });
                Check(true, "Real public page renders over validated HTTPS: " + new Uri(url).Host);
            }
            catch (Exception ex) { results.Add(new { url, success = false, error = ex.Message, source = core.Source, document = await core.ExecuteScriptAsync("JSON.stringify({title:document.title,text:document.body?.innerText.slice(0,500)})") }); failures.Add(new Uri(url).Host + ": " + ex.Message); }
        }
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Assets, "public-media-test.html"), "<!doctype html><meta charset=utf-8><title>Public media fixture</title><video muted controls playsinline src='https://media.w3.org/2010/05/sintel/trailer.mp4'></video>");
            await Navigate("https://vela.internal/public-media-test.html");
            await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = "document.querySelector('video').play().then(()=>true).catch(e=>String(e))", userGesture = true, awaitPromise = true, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(45));
            await ScriptUntil(core, "document.querySelector('video').currentTime>0.5 && document.querySelector('video').videoWidth>0", "Public HTTPS video decodes and advances playback without audio output");
            await core.ExecuteScriptAsync("document.querySelector('video').pause()");
            results.Add(new { media = "W3C Sintel MP4", success = true });
        }
        catch (Exception ex) { failures.Add("Public video: " + ex.Message); results.Add(new { media = "W3C Sintel MP4", success = false, error = ex.Message }); }
        var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "public-sites-report.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.UtcNow, engine = window.BrowserEnvironment!.BrowserVersionString, results,
            limitations = "Isolated profile; no real login, call hardware or user accounts. CPU samples include post-navigation activity, not stabilized idle." }, new JsonSerializerOptions { WriteIndented = true }));
        await CloseWindowAsync(window);
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
        Console.WriteLine("SUCCESS: real public pages and media verified; " + path);
    }
}
