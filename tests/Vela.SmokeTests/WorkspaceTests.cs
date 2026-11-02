using System.Collections.ObjectModel;
using System.IO;
using Vela.Models;
using Vela.Services;
using Microsoft.Web.WebView2.Core;

namespace Vela.Tests;

internal static partial class Program
{
    private static async Task WorkspaceTests(MainWindow window)
    {
        var environment = Field<CoreWebView2Environment>(window, "_env");
        var tab = await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        var view = tab.View!;
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("workspace.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate("https://workspace.test/forms.html");
        await ScriptUntil(core, "document.title === 'Form safety'", "Workspace fixture loads");
        await core.ExecuteScriptAsync("document.getElementById('name').focus();document.getElementById('name').value='preserved draft';document.getElementById('name').dispatchEvent(new Event('input',{bubbles:true}));");
        await Until(() => tab.HasUnsavedChanges, "Workspace draft tracked");
        var destination = new MainWindow(environment, suppressStartupTab: true);
        destination.Show();
        await Until(() => destination.IsLoaded, "Destination window loads");
        await window.TransferTabAsync(tab, destination);
        Check(ReferenceEquals(view, tab.View) && ReferenceEquals(tab.Owner, destination), "Moving a tab transfers its live native control instead of reloading it");
        Check(await core.ExecuteScriptAsync("document.getElementById('name').value === 'preserved draft'") == "true", "Cross-window tab movement preserves unsaved form contents");
        await core.ExecuteScriptAsync("document.getElementById('form').reset();");
        await Until(() => !tab.HasUnsavedChanges, "Transferred form reset");
        core.Navigate("https://workspace.test/forms.html?moved=1");
        await ScriptUntil(core, "location.search === '?moved=1'", "Transferred native callbacks continue updating the new owner");
        destination.PinTab(tab, true);
        Check(tab.IsPinned && tab.DisplayWidth < 100, "Pinned tabs use a compact header");
        Invoke(destination, "SaveSession");
        Check(SessionStore.Load()!.Tabs.Any(t => t.Url.Contains("moved=1") && t.IsPinned), "Pinned state is preserved in recovery snapshots");
        var normalCookies = core.CookieManager;
        normalCookies.AddOrUpdateCookie(normalCookies.CreateCookie("normal", "keep", "workspace.test", "/"));
        var privateWindow = new MainWindow(environment, isPrivate: true);
        privateWindow.Show();
        var privateTabs = Field<ObservableCollection<BrowserTab>>(privateWindow, "_tabs");
        await Until(() => privateTabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Private engine initializes");
        var privateCore = privateTabs[0].View!.CoreWebView2;
        await ScriptUntil(privateCore, "location.hostname === 'vela.internal' && !!document.getElementById('tiles')", "Private startup page completes initialization");
        Check(privateCore.Profile.IsInPrivateModeEnabled, "Private windows use the native InPrivate profile");
        privateCore.SetVirtualHostNameToFolderMapping("workspace.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        Check((await privateCore.CookieManager.GetCookiesAsync("https://workspace.test")).Count == 0, "Private browsing cannot read normal-profile cookies");
        privateCore.CookieManager.AddOrUpdateCookie(privateCore.CookieManager.CreateCookie("private", "temporary", "workspace.test", "/"));
        Check(!(await normalCookies.GetCookiesAsync("https://workspace.test")).Any(c => c.Name == "private"), "Private cookies do not enter the normal profile");
        privateCore.Navigate("https://workspace.test/forms.html?private=1");
        await ScriptUntil(privateCore, "location.search === '?private=1' && document.readyState === 'complete'", "Private page loads normally");
        var history = (HistoryStore)typeof(MainWindow).GetField("_history", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        Check(!history.Search("private=1", 10).Any(), "Private navigation is excluded from persistent browsing history");
        Invoke(privateWindow, "SaveSession");
        Check(!SessionStore.Load()!.Tabs.Any(t => t.Url.Contains("private=1")), "Private tabs are excluded from session recovery");
        await CloseWindowAsync(privateWindow);
        await CloseWindowAsync(destination);
        Log.Write("Privacy redaction test: https://workspace.test/private?secret=1");
        Check(!File.ReadAllText(Path.Combine(AppPaths.Logs, "vela.log")).Contains("secret=1"), "Local error logs redact complete website addresses");
        var diagnostics = await window.CaptureDiagnosticAsync();
        Check(!diagnostics.Contains(AppPaths.Root) && !diagnostics.Contains("workspace.test") && !diagnostics.Contains("preserved draft"), "Diagnostic export contains only allowlisted resource measurements");
        var lastSource = new MainWindow(environment, suppressStartupTab: true); lastSource.Show();
        await Until(() => lastSource.IsLoaded, "Last-tab source window loads");
        var movedLast = await (Task<BrowserTab>)Invoke(lastSource, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
        await ScriptUntil(movedLast.View!.CoreWebView2, "document.readyState==='complete'", "Last source tab is initialized");
        var movingCore = movedLast.View.CoreWebView2;
        var closedSource = new TaskCompletionSource(); lastSource.Closed += (_, _) => closedSource.TrySetResult();
        await lastSource.TransferTabAsync(movedLast, window);
        await closedSource.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(await movingCore.ExecuteScriptAsync("document.readyState==='complete'") == "true", "Moving the last live tab survives destruction of its original WPF window");
    }
}
