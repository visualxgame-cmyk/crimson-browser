using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Vela.Models;
using Vela.Services;

namespace Vela.Tests;

internal static partial class Program
{
    private static async Task AuthenticationCompatibilityTests()
    {
        Check(SignInProtection.IsActive("https://accounts.google.com/") && SignInProtection.IsActive("https://accounts.youtube.com/"), "Only the intended Google authentication hosts receive automatic flow protection");
        Check(!SignInProtection.IsActive("https://accounts.google.com.evil.test/") && !SignInProtection.IsActive("https://accounts.google.com:8443/") && !SignInProtection.IsActive("http://accounts.google.com/") && !SignInProtection.IsActive("https://user@accounts.google.com/"), "Authentication classification rejects spoofed hosts, credentials, alternate ports and insecure transport");
        Check(SignInProtection.IsInteractiveFrame("https://accounts.google.com/v3/signin/challenge/fixture") && !SignInProtection.IsInteractiveFrame("https://accounts.google.com/RotateCookies") && !SignInProtection.IsInteractiveFrame("https://accounts.google.com/CheckConnection") && !SignInProtection.IsInteractiveFrame("https://accounts.google.com/gsi/iframe/select"), "Passive account/cookie frames do not pin ordinary signed-in tabs in memory");
        var previous = SettingsStore.Load(); var settings = previous.Clone(); settings.EconomyMode = true; settings.StartupMode = "newtab"; SettingsStore.Save(settings);
        var window = new MainWindow(); window.Show(); var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Authentication fixture engine starts", 30000);
        await (Task)Invoke(window, "WaitForViewInitializationAsync")!;
        var tab = tabs[0]; var core = tab.View!.CoreWebView2;
        // Local synthetic documents only. No provider login page, real credential, cookie export or phone prompt is used.
        File.WriteAllText(Path.Combine(AppPaths.Assets, "auth-fixture.html"), "<!doctype html><title>Authentication compatibility fixture</title><p>Local compatibility test; not a sign-in page.</p><form id=flow onsubmit='event.preventDefault()'><input id=identity autocomplete=username><input id=secret type=password autocomplete=current-password><textarea id=notes></textarea></form>");
        File.WriteAllText(Path.Combine(AppPaths.Assets, "auth-complete.html"), "<!doctype html><title>Fixture callback complete</title>");
        core.SetVirtualHostNameToFolderMapping("auth-fixture.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate("https://auth-fixture.test/auth-fixture.html");
        await ScriptUntil(core, "location.hostname==='auth-fixture.test' && document.readyState==='complete'", "Synthetic authentication fields load locally");
        try
        {
            await core.ExecuteScriptAsync("for(const [id,value] of [['identity','fixture-user'],['secret','fixture-only']]){const e=document.getElementById(id);e.value=value;e.dispatchEvent(new Event('input',{bubbles:true}))}");
            await Until(() => tab.HasUnsavedChanges, "Credential edit protection reaches the host");
            Check(!await FormStateTracker.HasUnloadHandlerAsync(core), "Credential-only edits do not install a browser-injected navigation veto");
            await core.ExecuteScriptAsync("(()=>{const e=document.getElementById('notes');e.value='unsaved document';e.dispatchEvent(new Event('input',{bubbles:true}))})()");
            Check(await FormStateTracker.HasUnloadHandlerAsync(core), "Ordinary content edits retain their navigation protection alongside credentials");
            await core.ExecuteScriptAsync("(()=>{const e=document.getElementById('notes');e.value='';e.dispatchEvent(new Event('input',{bubbles:true}))})()");
            Check(!await FormStateTracker.HasUnloadHandlerAsync(core) && tab.HasUnsavedChanges, "Clearing content edits releases the navigation veto while preserving credential memory protection");
            await core.ExecuteScriptAsync("document.getElementById('flow').requestSubmit();document.getElementById('flow').remove();window.fixtureConfirmed=false;addEventListener('message',e=>{if(e.data==='fixture-confirmed')window.fixtureConfirmed=true})");
            await Until(() => !tab.HasUnsavedChanges, "A removed SPA form releases detached edited fields");
            tab.Url = "https://accounts.google.com/"; // Model classification only; the actual document remains on auth-fixture.test.
            Check((bool)Invoke(window, "IsHardMemoryProtected", tab)!, "Google confirmation pages are exempt from automatic suspension and eviction");
            await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
            tab.LastActiveUtc = DateTime.UtcNow.AddMinutes(-6); await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
            window.WindowState = WindowState.Minimized;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(tab.View is not null && !tab.IsSuspended, "An empty phone-confirmation step stays alive past the Economy discard deadline and minimization");
            await core.ExecuteScriptAsync("dispatchEvent(new MessageEvent('message',{data:'fixture-confirmed'}))");
            await ScriptUntil(core, "window.fixtureConfirmed===true", "An asynchronous confirmation can complete while its tab is in the background");
            window.WindowState = WindowState.Normal;
            var dialogs = 0;
            EventHandler<CoreWebView2ScriptDialogOpeningEventArgs> listener = (_, e) => { if (e.Kind == CoreWebView2ScriptDialogKind.Beforeunload) { dialogs++; e.Accept(); } };
            core.ScriptDialogOpening += listener;
            try { core.Navigate("https://auth-fixture.test/auth-complete.html"); await ScriptUntil(core, "document.title==='Fixture callback complete'", "Callback navigation completes without an injected leave-page dialog"); }
            finally { core.ScriptDialogOpening -= listener; }
            Check(dialogs == 0 && !(bool)Invoke(window, "IsHardMemoryProtected", tab)!, "Leaving authentication removes its resource exemption");
            await (Task)Invoke(window, "ActivateTabAsync", tab)!;
            // Map this one native controller to an offline frame fixture in its temporary profile.
            // This never contacts Google's login service and contains no account inputs.
            Directory.CreateDirectory(Path.Combine(AppPaths.Assets, "signin"));
            File.WriteAllText(Path.Combine(AppPaths.Assets, "signin", "pending-frame-fixture.html"), "<!doctype html><title>Offline confirmation frame fixture</title><p>Local test, no account or sign-in form.</p>");
            core.SetVirtualHostNameToFolderMapping("accounts.google.com", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
            await core.ExecuteScriptAsync("(()=>{const f=document.createElement('iframe');f.id='pending-auth-frame';f.src='https://accounts.google.com/signin/pending-frame-fixture.html';document.body.append(f)})()");
            await Until(() => tab.HasActiveSignIn, "Native frame messages identify an offline authentication frame");
            await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
            tab.LastActiveUtc = DateTime.UtcNow.AddMinutes(-6); await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
            Check(tab.View is not null && !tab.IsSuspended, "A background embedded confirmation frame protects its parent tab");
            await (Task)Invoke(window, "ActivateTabAsync", tab)!;
            await core.ExecuteScriptAsync("document.getElementById('pending-auth-frame').remove()");
            await Until(() => !tab.HasActiveSignIn, "Removing the confirmation frame releases its parent exemption");
            var blocker = new AdBlocker(); blocker.Load();
            foreach (var host in new[] { "accounts.google.com", "accounts.youtube.com", "consent.google.com", "consent.youtube.com" })
            {
                var expression = "(()=>{const parse=JSON.parse,json=Response.prototype.json,sheets=document.adoptedStyleSheets.length;(function(location){" + blocker.InjectionScript + "})({hostname:" + JsonSerializer.Serialize(host) + "});return JSON.parse===parse&&Response.prototype.json===json&&document.adoptedStyleSheets.length===sheets})()";
                Check(await core.ExecuteScriptAsync(expression) == "true", "Authentication/consent documents retain their native JavaScript and styles: " + host);
            }
        }
        catch (Exception ex) { Console.WriteLine("Authentication fixture failure: " + ex.Message); throw; }
        finally
        {
            tab.Url = tab.PendingUrl = AppConstants.NewTabUrl;
            await (Task)Invoke(window, "ActivateTabAsync", tab)!;
            core = tab.View!.CoreWebView2;
            await core.ExecuteScriptAsync("document.getElementById('flow')?.reset()");
            core.Navigate(AppConstants.NewTabUrl); await ScriptUntil(core, "location.hostname==='vela.internal' && document.readyState==='complete'", "Authentication fixture cleanup stays local");
            await CloseWindowAsync(window); SettingsStore.Save(previous);
        }
    }
}
