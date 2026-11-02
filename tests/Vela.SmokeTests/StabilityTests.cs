using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vela.Models;
using Vela.Services;
using Microsoft.Web.WebView2.Core;

namespace Vela.Tests;

internal static partial class Program
{
    private static async Task MediaProtectionTests(MainWindow window)
    {
        var before = window.LibrarySettings; var settings = before.Clone(); settings.EconomyMode = true;
        Invoke(window, "CommitSettings", settings);
        File.WriteAllText(Path.Combine(AppPaths.Assets, "activity-test.html"), "<!doctype html><title>Activity fixture</title><textarea id=edit></textarea><canvas width=160 height=90></canvas><video muted playsinline></video>");
        var tab = await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        var core = tab.View!.CoreWebView2;
        await ScriptUntil(core, "location.hostname==='vela.internal' && document.readyState==='complete'", "Activity fixture starts locally");
        core.SetVirtualHostNameToFolderMapping("activity.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate("https://activity.test/activity-test.html");
        await ScriptUntil(core, "location.hostname==='activity.test' && document.readyState==='complete'", "Activity tracker is installed on a website origin");
        await core.ExecuteScriptAsync("""
            window.callA=new RTCPeerConnection({iceServers:[]});window.callB=new RTCPeerConnection({iceServers:[]});
            window.callEcho='';callA.onicecandidate=e=>{if(e.candidate)callB.addIceCandidate(e.candidate)};
            callB.onicecandidate=e=>{if(e.candidate)callA.addIceCandidate(e.candidate)};
            callB.ondatachannel=e=>{e.channel.onmessage=m=>e.channel.send('echo:'+m.data)};
            window.callChannel=callA.createDataChannel('background-check');
            callChannel.onmessage=e=>window.callEcho=e.data;callChannel.onopen=()=>callChannel.send('initial');
            (async()=>{await callA.setLocalDescription(await callA.createOffer());await callB.setRemoteDescription(callA.localDescription);
            await callB.setLocalDescription(await callB.createAnswer());await callA.setRemoteDescription(callB.localDescription)})();
            """);
        await ScriptUntil(core, "callEcho==='echo:initial' && callA instanceof RTCPeerConnection && RTCPeerConnection.prototype.constructor===RTCPeerConnection", "Silent WebRTC connects with the native constructor intact");
        await Until(() => tab.HasActiveMedia, "WebRTC activity reaches the host");
        await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        tab.LastActiveUtc = DateTime.UtcNow.AddMinutes(-6);
        await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
        Check(tab.View is not null && !tab.IsSuspended, "Economy preserves a silent background call beyond its discard deadline");
        window.WindowState = WindowState.Minimized;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(!tab.IsSuspended && tab.View is not null, "Minimizing does not suspend an active silent call");
        await core.ExecuteScriptAsync("callChannel.send('background')");
        await ScriptUntil(core, "callEcho==='echo:background'", "Background call continues exchanging data while the browser is minimized");
        window.WindowState = WindowState.Normal;
        await (Task)Invoke(window, "ActivateTabAsync", tab)!;
        await core.ExecuteScriptAsync("callA.close();callB.close()");
        await Until(() => !tab.HasActiveMedia, "Closed WebRTC releases activity protection");
        await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", """{"expression":"(()=>{const c=document.querySelector('canvas');c.getContext('2d').fillRect(0,0,160,90);const v=document.querySelector('video');v.srcObject=c.captureStream(5);return v.play()})()","awaitPromise":true,"userGesture":true}""");
        await Until(() => tab.HasActiveMedia, "Muted video protects activity");
        await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        tab.LastActiveUtc = DateTime.UtcNow.AddMinutes(-6);
        await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
        Check(tab.View is not null && !tab.IsSuspended && !tab.IsAudible, "Economy preserves a playing muted video without relying on the audio indicator");
        await (Task)Invoke(window, "ActivateTabAsync", tab)!;
        await core.ExecuteScriptAsync("const v=document.querySelector('video');v.pause();v.srcObject.getTracks().forEach(t=>t.stop());v.srcObject=null;");
        await Until(() => !tab.HasActiveMedia, "Paused video releases activity protection");
        core = tab.View!.CoreWebView2;
        if (new Uri(core.Source).Host != "activity.test") throw new InvalidOperationException("Activity fixture unexpectedly changed origin.");
        await core.ExecuteScriptAsync("(()=>{const e=document.querySelector('textarea');e.value='unsaved';e.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await Until(() => tab.HasUnsavedChanges, "Form dirty state arrives");
        await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        tab.LastActiveUtc = DateTime.UtcNow.AddMinutes(-6);
        await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
        Check(tab.View is not null && !tab.IsSuspended, "Economy preserves unsaved edits beyond the discard deadline");
        // Configure the next fixture while active, before making it eligible for eviction.
        // Clearing edits in an expired background tab can legitimately release it immediately.
        await (Task)Invoke(window, "ActivateTabAsync", tab)!;
        await core.ExecuteScriptAsync("(()=>{const e=document.querySelector('textarea');e.value='';e.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await Until(() => !tab.HasUnsavedChanges, "Reverted form clears protection");
        await core.ExecuteScriptAsync("window.retainedMarker=true;window.temporaryUnload=e=>{e.preventDefault();e.returnValue=''};addEventListener('beforeunload',temporaryUnload)");
        await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        tab.LastActiveUtc = DateTime.UtcNow.AddMinutes(-6);
        await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
        Check(tab.View is not null && tab.HasUnloadHandler, "A site's unload protection remains respected during Economy eviction");
        Check(!tab.IsSuspended, "A known unload-protected page retains the conservative memory policy");
        await (Task)Invoke(window, "ActivateTabAsync", tab)!;
        await ScriptUntil(core, "window.retainedMarker===true && document.querySelector('textarea').value===''", "Reactivating a protected page preserves its DOM state");
        await core.ExecuteScriptAsync("removeEventListener('beforeunload',temporaryUnload)");
        await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        tab.LastActiveUtc = DateTime.UtcNow.AddMinutes(-6);
        await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
        Check(tab.View is null, "Removing unload protection releases the tab instead of retaining a stale exemption forever");
        await SyntheticCaptureTests(window.BrowserEnvironment!);
        await FileUploadTransportTest(window);
        Invoke(window, "CommitSettings", before);
    }

    private static async Task SyntheticCaptureTests(CoreWebView2Environment environment)
    {
        using var view = new Microsoft.Web.WebView2.Wpf.WebView2();
        var host = new Window { Title = "Synthetic media capture fixture", Content = view, Width = 700, Height = 500 }; host.Show();
        await view.EnsureCoreWebView2Async(environment); var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("capture.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        // A test-only provider creates real synthetic MediaStreamTracks; no device, permission or external connection is involved.
        await core.AddScriptToExecuteOnDocumentCreatedAsync("navigator.mediaDevices.getUserMedia=()=>{window.syntheticContext=new AudioContext();return Promise.resolve(syntheticContext.createMediaStreamDestination().stream)};");
        var active = false;
        using (var tracker = new FormStateTracker(core, _ => { }, value => active = value))
        {
            await tracker.InstallAsync(); core.Navigate("https://capture.test/activity-test.html");
            await ScriptUntil(core, "location.hostname==='capture.test' && document.readyState==='complete'", "Synthetic capture fixture loads without device permissions");
            await core.ExecuteScriptAsync("navigator.mediaDevices.getUserMedia({audio:true}).then(s=>{window.capture=s;window.captureClone=s.getAudioTracks()[0].clone();window.captureReady=true})");
            await ScriptUntil(core, "window.captureReady===true", "Capture wrapper returns the provider's MediaStream");
            await Until(() => active, "Capture activity is published");
            await core.ExecuteScriptAsync("capture.getTracks().forEach(t=>t.stop())");
            Check(active, "Stopping an original capture track keeps protection while its clone is live");
            await core.ExecuteScriptAsync("captureClone.stop();syntheticContext.close()");
            await Until(() => !active, "Last stopped capture track releases protection");
            Check(true, "MediaStreamTrack.stop clears protection even though it does not emit ended");
        }
        host.Close();
    }

    private static async Task FileUploadTransportTest(MainWindow window)
    {
        var payload = new byte[1024 * 1024]; Random.Shared.NextBytes(payload);
        var expected = Convert.ToHexString(SHA256.HashData(payload));
        var path = Path.Combine(AppPaths.Root, "synthetic-upload.bin"); await File.WriteAllBytesAsync(path, payload);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var transport = Task.Run(async () =>
        {
            for (var request = 0; request < 2; request++)
            {
                using var client = await listener.AcceptTcpClientAsync(); await using var stream = client.GetStream();
                using var header = new MemoryStream(); var single = new byte[1]; var tail = 0;
                while (tail != 0x0D0A0D0A)
                {
                    if (await stream.ReadAsync(single) == 0 || header.Length > 32768) throw new IOException("Incomplete test request headers.");
                    header.WriteByte(single[0]); tail = (tail << 8) | single[0];
                }
                var text = Encoding.ASCII.GetString(header.ToArray()); string body;
                if (request == 0) body = "<!doctype html><title>Local upload</title><link rel=icon href='data:,'><input type=file onchange=\"fetch('/upload',{method:'POST',body:this.files[0]}).then(r=>r.text()).then(t=>window.uploadResult=t)\">";
                else
                {
                    if (!text.StartsWith("POST /upload ")) throw new IOException("Expected native POST upload.");
                    var length = long.Parse(text.Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                    if (length != payload.Length) throw new IOException("Upload length differs.");
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536];
                    for (long remaining = length; remaining > 0;)
                    {
                        var received = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                        if (received == 0) throw new IOException("Upload ended early."); hash.AppendData(buffer, 0, received); remaining -= received;
                    }
                    if (Convert.ToHexString(hash.GetHashAndReset()) != expected) throw new IOException("Upload bytes changed in transport.");
                    body = "uploaded";
                }
                var bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n")); await stream.WriteAsync(bytes);
            }
        });
        var tab = await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!; var core = tab.View!.CoreWebView2;
        core.Navigate($"http://127.0.0.1:{port}/form");
        await ScriptUntil(core, "document.title==='Local upload' && document.readyState==='complete'", "Local upload form loads");
        using var document = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{}"));
        var root = document.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
        using var input = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector", JsonSerializer.Serialize(new { nodeId = root, selector = "input" })));
        await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", JsonSerializer.Serialize(new { nodeId = input.RootElement.GetProperty("nodeId").GetInt32(), files = new[] { path } }));
        await ScriptUntil(core, "window.uploadResult==='uploaded'", "Native file upload transfers 1 MB through loopback HTTP with an identical SHA-256");
        await transport.WaitAsync(TimeSpan.FromSeconds(15));
        await core.ExecuteScriptAsync("document.querySelector('input').value='';document.querySelector('input').dispatchEvent(new Event('input',{bubbles:true}))");
        await Until(() => !tab.HasUnsavedChanges, "Uploaded fixture clears dirty state");
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }
    private static async Task SmallWindowTests(MainWindow window)
    {
        MainWindow.Bookmarks.Save(null, new string('W', 500), "https://long-title.test/?q=" + new string('q', 2000), new string('F', 200));
        var liveBefore = Field<ObservableCollection<BrowserTab>>(window, "_tabs").Count(t => t.View is not null);
        var library = new LibraryWindow(window, "bookmarks") { Owner = window }; library.Show();
        library.MinWidth = 0; library.MinHeight = 0; library.Width = 760; library.Height = 500;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        library.UpdateLayout();
        var editor = Field<Border>(library, "BookmarkEditor"); var list = Field<ListBox>(library, "ItemsList");
        Check(Grid.GetRow(editor) == 1 && list.ActualWidth > 400 && list.ActualHeight > 40, "Narrow library places bookmark editing below a usable virtualized list");
        Check(Field<ComboBox>(library, "FolderFilter").ActualWidth <= 240, "Long bookmark folders keep the filter within its width budget");
        foreach (var mode in new[] { "history", "reading", "tabs", "diagnostics", "backup", "privacy" })
        {
            library.ShowMode(mode); library.UpdateLayout();
            Check(VisualChildren<Button>(library).Any(b => b.IsVisible && Equals(b.Content, "Închide") && b.TransformToAncestor(library).Transform(new Point()).Y + b.ActualHeight <= library.ActualHeight), "Small library keeps its close action reachable: " + mode);
        }
        library.Close();
        var settings = new SettingsWindow(window.LibrarySettings, _ => { }, _ => { }) { Owner = window }; settings.Show();
        settings.MinWidth = 0; settings.MinHeight = 0; settings.Width = 760; settings.Height = 500;
        foreach (var page in new[] { "aspect", "newtab", "memory", "privacy", "navigation", "about" })
        {
            Invoke(settings, "ShowPage", page); settings.UpdateLayout();
            var save = VisualChildren<Button>(settings).Single(b => Equals(b.Content, "Salvează"));
            Check(save.IsVisible && save.TransformToAncestor(settings).Transform(new Point()).Y + save.ActualHeight <= settings.ActualHeight, "Small settings keep Save reachable: " + page);
        }
        var footer = Field<TextBlock>(settings, "FooterStatusText"); footer.Text = new string('W', 120); settings.UpdateLayout();
        var saveButton = VisualChildren<Button>(settings).Single(b => Equals(b.Content, "Salvează"));
        Check(footer.TransformToAncestor(settings).Transform(new Point(footer.ActualWidth, 0)).X < saveButton.TransformToAncestor(settings).Transform(new Point()).X, "Long settings status text does not overlap the save button");
        saveButton.Focus(); saveButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
        Check(Keyboard.FocusedElement is Button { Content: "Anulează" }, "Keyboard traversal reaches Cancel before Save");
        settings.Close();
        Check(Field<ObservableCollection<BrowserTab>>(window, "_tabs").Count(t => t.View is not null) == liveBefore, "Small-window inspection creates no additional web renderers");
        await Task.CompletedTask;
    }
}
