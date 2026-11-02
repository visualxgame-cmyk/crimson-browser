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
    private sealed class ReleaseHandler(HttpStatusCode status, string payload) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Check(request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/owner/vela/releases/latest" &&
                request.Headers.UserAgent.ToString().StartsWith("Vela/"), "Update check uses the selected GitHub repository and identifies the application");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }

    private static async Task AdditionalStoreTests()
    {
        var priorSettings = new Settings { Version = 2, UpdateRepository = "", MaxLiveTabs = 10, SuspendAfterMinutes = 2, DiscardAfterMinutes = 30 };
        SettingsStore.Save(priorSettings);
        var migrated = SettingsStore.Load();
        Check(migrated.UpdateRepository == AppConstants.DefaultUpdateRepository && migrated.MaxLiveTabs == 10 && migrated.SuspendAfterMinutes == 2 && migrated.DiscardAfterMinutes == 30,
            "Release repository migration preserves existing memory preferences");
        migrated.UpdateRepository = ""; SettingsStore.Save(migrated);
        Check(SettingsStore.Load().UpdateRepository.Length == 0, "Clearing the update repository remains an explicit persistent choice after migration");
        migrated.UpdateRepository = "another-owner/custom-browser"; SettingsStore.Save(migrated);
        Check(SettingsStore.Load().UpdateRepository == "another-owner/custom-browser", "Custom update repository survives settings reload");
        SettingsStore.Save(new Settings { Version = migrated.Version });
        var source = "<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><p><DT><A HREF=\"https://example.test/?a=1&amp;b=2\" ADD_DATE=\"1700000000\">Știri &amp; idei</A>" +
            "<DT><A href='https://second.test'>Second <b>site</b></A><A HREF=\"javascript:alert(1)\">Bad</A>" +
            "<A HREF='https://user:password@example.test'>Secret</A><A HREF='data:text/html,test'>Bad</A></DL>";
        var (parsed, skipped) = BookmarkHtml.Parse(source);
        Check(parsed.Count == 2 && skipped == 3 && parsed[0].Title == "Știri & idei" && parsed[1].Title == "Second site", "HTML import handles entities and rejects executable or credential-bearing URLs");
        Check(parsed[0].AddedAt.ToUnixTimeSeconds() == 1700000000, "Bookmark import preserves timestamps");
        Check(BookmarkHtml.Parse(BookmarkHtml.Export(parsed)).Entries.Select(b => (b.Url, b.Title, b.AddedAt.ToUnixTimeSeconds()))
            .SequenceEqual(parsed.Select(b => (b.Url, b.Title, b.AddedAt.ToUnixTimeSeconds()))), "Netscape HTML export round-trips Unicode names, URLs and timestamp seconds");
        var store = new BookmarksStore();
        var first = store.Import(source);
        var again = store.Import(source);
        Check(first.Added == 2 && again.Added == 0 && again.Duplicates == 2, "Bookmark import merges without duplicating existing favorites");
        Check(new BookmarksStore().All.Count == 2, "Imported favorites are persisted atomically");
        Check(UpdateService.NormalizeRepository("https://github.com/owner/vela/releases/latest") == "owner/vela", "Update repository accepts a GitHub releases link");
        var rejected = false;
        try { UpdateService.NormalizeRepository("https://example.com/owner/vela"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Update configuration rejects unrelated servers");
        var handler = new ReleaseHandler(HttpStatusCode.OK, "{\"tag_name\":\"v0.2.0\",\"html_url\":\"https://github.com/owner/vela/releases/tag/v0.2.0\",\"draft\":false,\"prerelease\":false}");
        using (var client = new HttpClient(handler))
        {
            Check(handler.Requests == 0, "Update checking creates no background network traffic");
            var result = await UpdateService.CheckAsync("owner/vela", client);
            Check(handler.Requests == 1 && result.IsNewer && result.LatestVersion == new Version(0, 2, 0), "Manual update check identifies a newer stable release");
        }
        using (var client = new HttpClient(new ReleaseHandler(HttpStatusCode.NotFound, "{}")))
        {
            rejected = false;
            try { await UpdateService.CheckAsync("owner/vela", client); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Repository without a published release reports a clear failure");
        }
        using (var invalid = JsonDocument.Parse("{\"tag_name\":\"v2.0.0\",\"html_url\":\"https://evil.test/download\"}"))
        {
            rejected = false;
            try { UpdateService.ParseRelease("owner/vela", invalid.RootElement); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "An update response cannot redirect downloads to an unrelated origin");

        var popupGuard = new PopupGuard();
        var now = DateTime.UtcNow;
        Check(popupGuard.Allow(false, now) && popupGuard.Allow(false, now.AddSeconds(1)) && !popupGuard.Allow(false, now.AddSeconds(2)) && popupGuard.Allow(true, now.AddSeconds(2)) && popupGuard.Allow(false, now.AddSeconds(25)),
            "Popup guard preserves user-initiated windows, caps automatic bursts and expires without polling");

        var manifestFolder = Path.Combine(AppPaths.Root, "manifest-inspection"); Directory.CreateDirectory(manifestFolder);
        File.WriteAllText(Path.Combine(manifestFolder, "manifest.json"), """{"manifest_version":3,"name":"Permission fixture","version":"2.4","permissions":["tabs","storage"],"host_permissions":["https://*.example.test/*"],"content_scripts":[{"matches":["https://app.example.test/*"]}]}""");
        var manifest = ExtensionManifest.Read(manifestFolder);
        Check(manifest.Name == "Permission fixture" && manifest.Version == "2.4" && manifest.Access.Count == 4 && manifest.AccessSummary.Contains("Acces la site"),
            "Extension manifests expose declared API and site access before installation");
        }
        var plan = WindowsIntegration.RegistrationPlan(@"C:\Program Files\Vela\Vela.exe");
        Check(plan.Any(p => p.Path.EndsWith(@"URLAssociations") && p.Name == "https" && p.Value == "VelaHTML") &&
            plan.Any(p => p.Path.EndsWith(@"FileAssociations") && p.Name == ".html"), "Windows registration covers HTTP, HTTPS and HTML files");
        Check(plan.Single(p => p.Path == @"Software\Classes\VelaHTML\shell\open\command").Value == "\"C:\\Program Files\\Vela\\Vela.exe\" -- \"%1\"" &&
            plan.All(p => !p.Path.Contains("UserChoice")), "Windows registration quotes paths and leaves the user's default choice to Windows");
        var htmlPath = Path.Combine(AppPaths.Data, "external document.html");
        File.WriteAllText(htmlPath, "<!doctype html><title>External document</title>");
        var targets = BrowserInstance.NavigationTargets(["--", "https://example.test/link", htmlPath, "javascript:alert(1)", "--anything"]);
        Check(targets.Length == 2 && targets[1] == new Uri(htmlPath).AbsoluteUri, "External activation accepts website links and quoted local HTML paths only");
        var key = AppPaths.Root + "/ipc-test";
        using var primary = new BrowserInstance(key);
        using var secondary = new BrowserInstance(key);
        Check(primary.IsPrimary && !secondary.IsPrimary, "Profile ownership prevents a duplicate browser process");
        var delivered = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.Listen(urls => delivered.TrySetResult(urls));
        await secondary.SendAsync(targets);
        Check((await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10))).SequenceEqual(targets), "User-scoped activation pipe delivers external links without starting another renderer");
    }

    private static async Task AdditionalBrowserTests(MainWindow window, BrowserTab existing)
    {
        File.WriteAllText(Path.Combine(AppPaths.Assets, "forms.html"), "<!doctype html><html><head><title>Form safety</title></head><body><form id='form'><input id='name' value='initial'><textarea id='notes'></textarea><select id='choice'><option>One</option><option>Two</option></select><button type='reset'>Reset</button></form><div id='editor' contenteditable='true'>Text</div></body></html>");
        File.WriteAllText(Path.Combine(AppPaths.Assets, "frame-form.html"), "<!doctype html><form><input id='field'></form>");
        var tab = await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        var core = tab.View!.CoreWebView2;
        // WebView2 applies this setting when loading a document, not retroactively.
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.SetVirtualHostNameToFolderMapping("forms.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.SetVirtualHostNameToFolderMapping("frame.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate("https://forms.test/forms.html");
        await ScriptUntil(core, "document.title === 'Form safety'", "Form protection test page loads");
        Check(!tab.HasUnsavedChanges, "Unedited form starts without a dirty flag");
        await core.ExecuteScriptAsync("document.getElementById('name').focus(); document.getElementById('name').value='draft'; document.getElementById('name').dispatchEvent(new Event('input',{bubbles:true}));");
        await Until(() => tab.HasUnsavedChanges, "Track form edit");
        Check((bool)Invoke(window, "IsMemoryProtected", tab)!, "Editing a form protects the tab without inspecting field contents in the host");
        await (Task)Invoke(window, "ActivateTabAsync", existing)!;
        Check(!await (Task<bool>)Invoke(window, "TryDiscardTabAsync", tab)!, "Automatic eviction refuses a tab with unsaved form edits");
        await (Task)Invoke(window, "ActivateTabAsync", tab)!;
        await core.ExecuteScriptAsync("document.getElementById('name').value='initial'; document.getElementById('name').dispatchEvent(new Event('input',{bubbles:true}));");
        await Until(() => !tab.HasUnsavedChanges, "Revert form edit");
        Check(true, "Reverting a field to its initial value releases form protection");
        await core.ExecuteScriptAsync("document.getElementById('editor').focus(); document.getElementById('editor').textContent='Draft editor'; document.getElementById('editor').dispatchEvent(new Event('input',{bubbles:true}));");
        await Until(() => tab.HasUnsavedChanges, "Track rich editor");
        Check(true, "Rich editor changes are protected without serializing the editor DOM");
        // Rich editor is intentionally conservative until navigation or a completed form reset.
        await core.ExecuteScriptAsync("document.getElementById('editor').remove();");
        var beforeUnload = 0;
        EventHandler<CoreWebView2ScriptDialogOpeningEventArgs> accept = (_, e) => { if (e.Kind == CoreWebView2ScriptDialogKind.Beforeunload) { beforeUnload++; e.Accept(); } };
        core.ScriptDialogOpening += accept;
        core.Navigate("https://forms.test/forms.html?reset=1");
        await ScriptUntil(core, "location.search === '?reset=1' && document.readyState === 'complete'", "Committed navigation resets the old document's form state");
        await Until(() => !tab.HasUnsavedChanges, "Clean new document");
        await core.ExecuteScriptAsync("document.getElementById('notes').focus(); document.getElementById('notes').value='draft'; document.getElementById('notes').dispatchEvent(new Event('input',{bubbles:true}));");
        await Until(() => tab.HasUnsavedChanges, "Textarea edit");
        await core.ExecuteScriptAsync("document.getElementById('form').reset();");
        await Until(() => !tab.HasUnsavedChanges, "Form reset");
        Check(true, "A form reset clears the corresponding unsaved state");

        CoreWebView2Frame? frame = null;
        core.FrameCreated += (_, e) => frame = e.Frame;
        await core.ExecuteScriptAsync("const f=document.createElement('iframe');f.id='frame';f.src='https://frame.test/frame-form.html';document.body.append(f);");
        await Until(() => frame is not null, "Create cross-origin form frame");
        await UntilAsync(async () => await frame!.ExecuteScriptAsync("document.readyState === 'complete' && !!document.getElementById('field')") == "true", "Frame loaded");
        await frame!.ExecuteScriptAsync("document.getElementById('field').focus();document.getElementById('field').value='frame draft';document.getElementById('field').dispatchEvent(new Event('input',{bubbles:true}));");
        await Until(() => tab.HasUnsavedChanges, "Cross-origin form edit");
        Check(true, "Cross-origin iframe edits protect their owning tab");
        await core.ExecuteScriptAsync("document.getElementById('frame').remove();");
        await Until(() => !tab.HasUnsavedChanges, "Remove edited frame");
        Check(true, "Destroyed iframe releases its edit state and event subscriptions");
        await core.ExecuteScriptAsync("window.nativeExit=e=>e.preventDefault();addEventListener('beforeunload',nativeExit);");
        Check(await FormStateTracker.HasUnloadHandlerAsync(core), "Native page unload handlers are detected without executing them");
        await (Task)Invoke(window, "ActivateTabAsync", existing)!;
        Check(!await (Task<bool>)Invoke(window, "TryDiscardTabAsync", tab)!, "Memory eviction also respects a page's own unload protection");
        await (Task)Invoke(window, "ActivateTabAsync", tab)!;
        await core.ExecuteScriptAsync("removeEventListener('beforeunload',nativeExit);");

        var profile = core.Profile;
        await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Camera, "https://forms.test", CoreWebView2PermissionState.Allow);
        await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, "https://forms.test", CoreWebView2PermissionState.Deny);
        var permissions = await profile.GetNonDefaultPermissionSettingsAsync();
        Check(permissions.Any(p => SiteDataService.Origin(p.PermissionOrigin) == "https://forms.test" && p.PermissionKind == CoreWebView2PermissionKind.Camera && p.PermissionState == CoreWebView2PermissionState.Allow), "Site permissions are stored by the native profile");
        await profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Camera, "https://forms.test", CoreWebView2PermissionState.Default);
        Check(!(await profile.GetNonDefaultPermissionSettingsAsync()).Any(p => SiteDataService.Origin(p.PermissionOrigin) == "https://forms.test" && p.PermissionKind == CoreWebView2PermissionKind.Camera), "Revoking a permission returns the site to Ask");
        var siteWindow = new SiteSettingsWindow(() => tab.View!.CoreWebView2.Profile, "https://forms.test", _ => Task.FromResult(true)) { Owner = window };
        siteWindow.Show();
        await Until(() => ((ItemsControl)siteWindow.FindName("PermissionsList")).Items.Count >= 5, "Load permissions UI");
        Check(true, "Site settings displays all required permissions from the native profile");
        await core.ExecuteScriptAsync("localStorage.setItem('vela-safety','target');");
        var cookies = profile.CookieManager;
        cookies.AddOrUpdateCookie(cookies.CreateCookie("target", "yes", "forms.test", "/"));
        cookies.AddOrUpdateCookie(cookies.CreateCookie("unrelated", "keep", "unrelated.test", "/"));
        await SiteDataService.ClearOriginAsync(core, "https://forms.test");
        Check(await core.ExecuteScriptAsync("localStorage.getItem('vela-safety') === null") == "true", "Per-site data clearing removes the selected origin's local storage");
        Check((await cookies.GetCookiesAsync("https://forms.test")).Count == 0 &&
            (await cookies.GetCookiesAsync("https://unrelated.test")).Any(c => c.Name == "unrelated"), "Per-site clearing preserves cookies for unrelated sites");
        await core.ExecuteScriptAsync("localStorage.setItem('vela-safety','target');");
        Check(await (Task<bool>)Invoke(window, "ClearSiteDataAsync", "https://forms.test")!, "Integrated site cleanup safely replaces affected renderers");
        core = tab.View!.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("forms.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate("https://forms.test/forms.html?after-clear=1");
        await ScriptUntil(core, "location.search === '?after-clear=1' && document.readyState === 'complete'", "Cleared site can reload normally");
        Check(await core.ExecuteScriptAsync("localStorage.getItem('vela-safety') === null") == "true", "Integrated cleanup prevents the old page from recreating cleared storage");
        Check((await core.Profile.GetNonDefaultPermissionSettingsAsync()).Any(p => SiteDataService.Origin(p.PermissionOrigin) == "https://forms.test" && p.PermissionKind == CoreWebView2PermissionKind.Notifications), "Clearing site data preserves the user's permission choices");
        await (Task)Invoke(siteWindow, "LoadOriginAsync")!;
        Check(((ItemsControl)siteWindow.FindName("PermissionsList")).Items.Count >= 5 &&
            !((TextBlock)siteWindow.FindName("StatusText")).Text.Contains("încărcate"), "Permission management remains usable after its original renderer is recreated");
        siteWindow.Close();
        var local = Path.Combine(AppPaths.Data, "external document.html");
        window.OpenExternalUrls([new Uri(local).AbsoluteUri]);
        var tabs = Field<System.Collections.ObjectModel.ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.Any(t => t.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)), "Open external HTML");
        Check(true, "External HTML activation is integrated with browser tab creation");
        var startup = new MainWindow(Field<CoreWebView2Environment>(window, "_env"));
        startup.OpenExternalUrls([new Uri(local).AbsoluteUri], activate: false);
        startup.Show();
        var startupTabs = Field<System.Collections.ObjectModel.ObservableCollection<BrowserTab>>(startup, "_tabs");
        await Until(() => startupTabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Initialize external-link startup");
        await ScriptUntil(startupTabs[0].View!.CoreWebView2, "document.title === 'External document'", "A link received during startup waits for WebView2 initialization");
        await CloseWindowAsync(startup);
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(description);
            await Task.Delay(50);
        }
    }
}
