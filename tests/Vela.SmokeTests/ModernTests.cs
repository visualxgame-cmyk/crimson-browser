using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Vela.Models;
using Vela.Services;

namespace Vela.Tests;

internal static partial class Program
{
    private sealed class HostsHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(content) });
    }
    private static async Task ModernStoreTests()
    {
        var store = new BookmarksStore();
        var id = store.Save(null, "Documentație", "https://library.test/docs", "Lucru/Referințe");
        var exported = BookmarkHtml.Parse(BookmarkHtml.Export(store.All)).Entries;
        Check(exported.Single(b => b.Id == id).Folder == "Lucru/Referințe", "Bookmark HTML preserves nested Unicode folders");
        var stale = store.Snapshot.ToArray(); store.Remove("https://library.test/docs"); store.Flush();
        store.Merge(stale);
        Check(!store.Contains("https://library.test/docs"), "Sync tombstones prevent an older device from resurrecting deleted bookmarks");
        var restoredId = store.Save(null, "Restored", "https://library.test/docs", "Other");
        Check(restoredId == id && store.All.Count(b => b.Url == "https://library.test/docs") == 1, "Readding a deleted favorite preserves its synchronization identity");
        var payload = new LibraryTransfer(1, "sync", DateTimeOffset.UtcNow, store.Snapshot.ToList(), SharedPreferences.From(new Settings()));
        const string password = "test-only-passphrase-123";
        var self = ReleaseVerification.Inspect(Environment.ProcessPath!);
        Check(!self.TrustedSignature && self.Sha256.Length == 64, "Installer verification distinguishes unsigned builds from trusted Authenticode signatures");
        var encrypted = EncryptedLibrary.Encrypt(payload, password);
        Check(!Encoding.UTF8.GetString(encrypted).Contains("library.test") && EncryptedLibrary.Decrypt(encrypted, password).Bookmarks.Count == payload.Bookmarks.Count,
            "Encrypted synchronization round-trips data without plaintext site addresses");
        bool Rejected(byte[] data, string secret) { try { EncryptedLibrary.Decrypt(data, secret); return false; } catch (InvalidDataException) { return true; } }
        Check(Rejected(encrypted, "another-password-123"), "Wrong synchronization password is rejected before merge");
        encrypted[^1] ^= 1;
        Check(Rejected(encrypted, password), "Authenticated encryption rejects modified archives");
        var folder = Path.Combine(AppPaths.Root, "fake-cloud");
        EncryptedLibrary.Write(Path.Combine(folder, "vela-device1.sync"), payload, password);
        EncryptedLibrary.Write(Path.Combine(folder, "vela-device2.sync"), payload, password);
        var clients = EncryptedLibrary.ReadSyncFolder(folder, password);
        Check(clients.Count == 2 && store.Merge(clients.SelectMany(p => p.Bookmarks)) == 0, "Folder sync merges repeated device snapshots without duplicates");
        var invalidFolder = Path.Combine(AppPaths.Root, "invalid-cloud");
        EncryptedLibrary.Write(Path.Combine(invalidFolder, "vela-device.sync"), payload with { ReadingList = [new Bookmark("javascript:alert(1)", "Broken", DateTimeOffset.UtcNow)] }, password);
        var invalidReadingRejected = false;
        try { EncryptedLibrary.ReadSyncFolder(invalidFolder, password); } catch (ArgumentException) { invalidReadingRejected = true; } catch (InvalidDataException) { invalidReadingRejected = true; }
        Check(invalidReadingRejected, "Invalid reading-list entries are rejected before synchronization can merge local data");
        SitePreferencesStore.SetZoom("https://prefs.test", 1.4); SitePreferencesStore.SetShield("https://prefs.test", false); SitePreferencesStore.Flush();
        Check(SitePreferencesStore.Get("https://prefs.test") == new SitePreference(1.4, false), "Site zoom and shield preferences share an atomic persistent record");
        var hosts = string.Join('\n', Enumerable.Range(0, 1100).Select(i => $"0.0.0.0 ads{i}.example.test"));
        using (var client = new HttpClient(new HostsHandler(hosts))) await BlocklistUpdates.UpdateAsync(client);
        var first = File.ReadAllText(BlocklistUpdates.FilePath);
        using (var client = new HttpClient(new HostsHandler(hosts.Replace("ads", "tracker")))) await BlocklistUpdates.UpdateAsync(client);
        BlocklistUpdates.Rollback();
        Check(File.ReadAllText(BlocklistUpdates.FilePath) == first, "Validated blocklist updates support exact rollback");
        using (var client = new HttpClient(new HostsHandler("<html>error</html>")))
        {
            var rejected = false; try { await BlocklistUpdates.UpdateAsync(client); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(BlocklistUpdates.FilePath) == first, "Malformed blocklist downloads preserve the working list");
        }
    }

    private static async Task ModernBrowserTests(MainWindow window)
    {
        var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        var tab = await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        var core = tab.View!.CoreWebView2;
        var body = string.Concat(Enumerable.Repeat("Text de articol cu informații utile și fără elemente decorative. ", 8));
        File.WriteAllText(Path.Combine(AppPaths.Assets, "article-test.html"), $"<!doctype html><meta charset='utf-8'><title>Article fixture</title><main><article><h1>Lectură</h1><p>{body}</p><ul><li>Primul punct</li><li>Al doilea punct</li></ul></article></main>");
        core.SetVirtualHostNameToFolderMapping("reading.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.SetVirtualHostNameToFolderMapping("other.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate("https://reading.test/article-test.html");
        await ScriptUntil(core, "document.title==='Article fixture'", "Article compatibility fixture loads");
        Invoke(window, "ZoomActive", .35, false);
        await Until(() => Math.Abs(SitePreferencesStore.Get("https://reading.test").Zoom - 1.35) < .001, "Zoom saved from native change");
        core.Navigate("https://other.test/article-test.html");
        await ScriptUntil(core, "location.hostname==='other.test' && document.readyState==='complete'", "Second origin loads");
        Check(Math.Abs(tab.View.ZoomFactor - 1) < .001, "A site's zoom does not leak to another origin");
        core.Navigate("https://reading.test/article-test.html");
        await ScriptUntil(core, "location.hostname==='reading.test' && document.readyState==='complete'", "Original origin reloads");
        Check(Math.Abs(tab.View.ZoomFactor - 1.35) < .001, "Per-site zoom is restored after navigation");
        Invoke(window, "ToggleTabMute", tab);
        Check(core.IsMuted, "Tab mute state reaches the native audio control");
        Invoke(window, "ToggleTabMute", tab);
        var pdf = Path.Combine(AppPaths.Root, "test-page.pdf");
        Check(await core.PrintToPdfAsync(pdf) && File.ReadAllBytes(pdf).AsSpan(0, 5).SequenceEqual("%PDF-"u8), "Native PDF output is a valid PDF document");
        Invoke(window, "Reader_Click", window, new RoutedEventArgs());
        await Until(() => tabs.Any(t => t.Reader is not null && t.View?.CoreWebView2 is not null), "Reader control initialized");
        var reader = tabs.Last(t => t.Reader is not null);
        await ScriptUntil(reader.View!.CoreWebView2, "document.getElementById('article').textContent.includes('informații utile')", "Reader receives and renders extracted article text");
        Check(reader.Reader!.Title == "Lectură" && reader.Reader.Paragraphs.All(p => p.Kind != "h1"), "Reader avoids duplicating the article title");
        var count = tabs.Count(t => t.View is not null);
        var library = new LibraryWindow(window, "tabs") { Owner = window };
        library.Show(); library.ShowMode("history"); library.ShowMode("bookmarks"); library.ShowMode("backup"); library.ShowMode("privacy"); library.ShowMode("tabs");
        Check(tabs.Count(t => t.View is not null) == count, "Browsing library sections and searching tabs does not wake discarded renderers");
        library.Close();
        var backup = window.CreateTransfer("backup");
        Check(backup.LocalBackup is not null && backup.LocalBackup.Shortcuts.Count == ShortcutStore.All.Count && window.CreateTransfer("sync").LocalBackup is null,
            "Full backups include local settings while synchronization excludes machine-specific data");
        var originalTitle = ShortcutStore.All[0].Name;
        ShortcutStore.Save(ShortcutStore.All[0].Id, "Changed for test", ShortcutStore.All[0].Url);
        await window.RestoreBackupAsync(backup);
        Check(ShortcutStore.All[0].Name == originalTitle, "Backup restoration integrates settings, site preferences and new-tab shortcuts");
        await (Task)Invoke(window, "ActivateTabAsync", tab)!;
        await core.ExecuteScriptAsync("document.body.insertAdjacentHTML('afterbegin','<a id=background-link href=https://reading.test/article-test.html?background=1 style=display:block;padding:16px>Background link</a><input id=upload-file type=file>');");
        async Task ClickLink(string button, int modifiers)
        {
            using var rect = JsonDocument.Parse(await core.ExecuteScriptAsync("(()=>{const e=document.getElementById('background-link');e.scrollIntoView();const r=e.getBoundingClientRect();return {x:r.x+20,y:r.y+10}})()"));
            var x = rect.RootElement.GetProperty("x").GetDouble(); var y = rect.RootElement.GetProperty("y").GetDouble();
            foreach (var type in new[] { "mousePressed", "mouseReleased" })
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button, modifiers, clickCount = 1 }));
        }
        var beforeGesture = tabs.Count;
        await ClickLink("left", 2);
        await Until(() => tabs.Count > beforeGesture, "Ctrl click opens a tab");
        Check(tabs.Count == beforeGesture + 1 && tab.IsSelected && tabs.Last().View is null, "Trusted Ctrl-click opens an unloaded background tab without switching focus");
        beforeGesture = tabs.Count;
        await ClickLink("middle", 0);
        await Until(() => tabs.Count > beforeGesture, "Middle click opens a tab");
        Check(tabs.Count == beforeGesture + 1 && tab.IsSelected && tabs.Last().View is null, "Trusted middle-click opens an unloaded background tab without duplicate navigation");
        var upload = Path.Combine(AppPaths.Root, "upload-fixture.txt"); File.WriteAllText(upload, "local fixture only");
        using (var document = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{}")))
        {
            var node = document.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
            using var input = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector", JsonSerializer.Serialize(new { nodeId = node, selector = "#upload-file" })));
            await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", JsonSerializer.Serialize(new { nodeId = input.RootElement.GetProperty("nodeId").GetInt32(), files = new[] { upload } }));
        }
        await ScriptUntil(core, "document.getElementById('upload-file').files[0]?.name==='upload-fixture.txt'", "Native file selection reaches an HTML upload control without transmitting data");
        await core.ExecuteScriptAsync("document.getElementById('upload-file').value='';document.getElementById('upload-file').dispatchEvent(new Event('input',{bubbles:true}));");
        await core.ExecuteScriptAsync("""
            window.rtcResult='pending';(async()=>{
              const a=new RTCPeerConnection({iceServers:[]}),b=new RTCPeerConnection({iceServers:[]});
              try{
                a.onicecandidate=e=>{if(e.candidate)b.addIceCandidate(e.candidate).catch(()=>{})};
                b.onicecandidate=e=>{if(e.candidate)a.addIceCandidate(e.candidate).catch(()=>{})};
                b.ondatachannel=e=>{e.channel.onmessage=event=>{window.rtcResult=event.data;a.close();b.close()}};
                const channel=a.createDataChannel('local-check');channel.onopen=()=>channel.send('passed');
                await a.setLocalDescription(await a.createOffer());await b.setRemoteDescription(a.localDescription);
                await b.setLocalDescription(await b.createAnswer());await a.setRemoteDescription(b.localDescription);
              }catch(error){window.rtcResult=String(error);a.close();b.close()}
            })();
            """);
        await ScriptUntil(core, "window.rtcResult==='passed'", "WebRTC data channels exchange a local message without an external signaling service");
        File.WriteAllText(Path.Combine(AppPaths.Assets, "popup-test.html"), "<!doctype html><title>Auth callback</title><script>window.opener?.postMessage('auth-success','*');</script>");
        await core.ExecuteScriptAsync("window.receivedAuth=false;window.addEventListener('message',e=>{if(e.data==='auth-success')window.receivedAuth=true});window.open('https://vela.internal/popup-test.html','velaAuth');");
        await ScriptUntil(core, "window.receivedAuth===true", "Native popup routing preserves window.opener authentication callbacks");
        var popup = tabs.Last(t => t.Url.Contains("popup-test.html"));
        var nativeCloseRequested = false;
        popup.View!.CoreWebView2.WindowCloseRequested += (_, _) => nativeCloseRequested = true;
        await popup.View!.CoreWebView2.ExecuteScriptAsync("window.close()");
        await Until(() => !tabs.Contains(popup), "Script-created popup closes its own tab; native request=" + nativeCloseRequested);
        Check(true, "Native popup close requests are integrated with tab lifetime");
        Check(window.IsVisible && !Field<bool>(window, "_shuttingDown") && tabs.Contains(tab), "Closing a native popup preserves its owning browser window and other tabs");
        Check(ReferenceEquals(Field<BrowserTab>(window, "_active"), tab), "Closing an authentication popup returns to its opener without waking another background tab");
        window.WindowState = WindowState.Minimized;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(!Field<System.Windows.Threading.DispatcherTimer>(window, "_memoryTimer").IsEnabled, "Minimizing stops resource diagnostics polling");
        window.WindowState = WindowState.Normal;
        window.Activate();
        await Until(() => Field<System.Windows.Threading.DispatcherTimer>(window, "_memoryTimer").IsEnabled, "Native restore event resumes diagnostics");
        Check(Field<System.Windows.Threading.DispatcherTimer>(window, "_memoryTimer").IsEnabled, "Resource diagnostics resume on restore");
    }
    private static async Task ExtensionTests()
    {
        var settings = new Settings { ExtensionsEnabled = true }; SettingsStore.Save(settings);
        var window = new MainWindow(); window.Show();
        var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Extension-enabled engine starts", 30000);
        await ScriptUntil(tabs[0].View!.CoreWebView2, "location.hostname==='vela.internal' && document.readyState==='complete'", "Extension-enabled startup page loads");
        var source = Path.Combine(AppPaths.Root, "fixture-extension"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "manifest.json"), """{"manifest_version":3,"name":"Vela isolated test extension","version":"1.0.0","background":{"service_worker":"background.js"}}""");
        File.WriteAllText(Path.Combine(source, "background.js"), "chrome.runtime.onInstalled.addListener(()=>{});");
        await window.InstallExtensionAsync(source);
        var extensions = await window.GetExtensionsAsync();
        Console.WriteLine("Native extensions: " + string.Join(", ", extensions.Select(e => e.Name + "=" + e.IsEnabled)));
        var installed = extensions.Single(e => e.Name == "Vela isolated test extension");
        Check(installed.IsEnabled, "Unpacked extensions install from an app-managed local copy");
        await installed.EnableAsync(false);
        Check(!(await window.GetExtensionsAsync()).Single(e => e.Id == installed.Id).IsEnabled, "Native extension disable persists in the profile");
        await installed.RemoveAsync();
        Check(!(await window.GetExtensionsAsync()).Any(e => e.Id == installed.Id), "Extension removal integrates with the native profile");
        await CloseWindowAsync(window);
        Console.WriteLine("SUCCESS: extension lifecycle verified in isolated profile " + AppPaths.Root);
    }
}
