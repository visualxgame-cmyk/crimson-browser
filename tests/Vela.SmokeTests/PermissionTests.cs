using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Vela.Models;
using Vela.Services;

namespace Vela.Tests;

internal static partial class Program
{
    private static async Task PermissionPromptTests()
    {
        var window = new MainWindow(); window.Show(); var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
        await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Permission fixture engine starts", 30000);
        await (Task)Invoke(window, "WaitForViewInitializationAsync")!;
        var tab = tabs[0]; var core = tab.View!.CoreWebView2;
        File.WriteAllText(Path.Combine(AppPaths.Assets, "permission-test.html"), "<!doctype html><title>Permission fixture</title><p>Local permission test; no notifications are sent.</p>");
        core.SetVirtualHostNameToFolderMapping("permissions.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
        const string url = "https://permissions.test/permission-test.html";
        var bar = Field<Border>(window, "PermissionBar");
        var navigation = 0;
        async Task Load()
        {
            var query = "?case=" + ++navigation;
            core.Navigate(url + query); await ScriptUntil(core, "location.hostname==='permissions.test' && location.search===" + JsonSerializer.Serialize(query) + " && document.readyState==='complete'", "Permission fixture loads locally");
        }
        async Task Request()
        {
            await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = "window.permissionResult='pending';Notification.requestPermission().then(value=>window.permissionResult=value)", userGesture = true }));
        }
        async Task RequestMedia(string constraints)
        {
            await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression = "window.mediaResult='pending';navigator.mediaDevices.getUserMedia(" + constraints + ").then(stream=>{stream.getTracks().forEach(track=>track.stop());window.mediaResult='granted'}).catch(error=>window.mediaResult=error.name)",
                userGesture = true
            }));
        }
        void Choose(string button) => Field<Button>(window, button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, "https://permissions.test", CoreWebView2PermissionState.Default);
            await Load();
            var address = Field<TextBox>(window, "Omnibox"); address.Focus();
            await Request(); await Until(() => bar.Visibility == Visibility.Visible, "Permission request appears in the browser bar");
            Check(Field<TextBlock>(window, "PermissionOrigin").Text == "https://permissions.test" && Field<TextBlock>(window, "PermissionDescription").Text.Contains("notificări"), "Permission bar identifies the requesting origin and capability");
            Check(window.IsEnabled && ReferenceEquals(Keyboard.FocusedElement, address) && await core.ExecuteScriptAsync("2+2") == "4", "Permission requests neither block the renderer nor steal keyboard focus");
            window.WindowState = WindowState.Normal; window.Width = 760; window.UpdateLayout();
            var dismiss = Field<Button>(window, "PermissionDismiss");
            var buttonBounds = dismiss.TransformToAncestor(bar).TransformBounds(new Rect(dismiss.RenderSize));
            Check(buttonBounds.Right <= bar.ActualWidth && buttonBounds.Bottom <= bar.ActualHeight && bar.ActualHeight < 150, "Permission controls fit the minimum browser width without covering web content");
            var preview = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(bar.ActualWidth), (int)Math.Ceiling(bar.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var context = drawing.RenderOpen()) context.DrawRectangle(new System.Windows.Media.VisualBrush(bar), null, new Rect(bar.RenderSize));
            preview.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
            using (var output = File.Create(Path.Combine(AppPaths.Root, "permission-bar.png"))) encoder.Save(output);
            Console.WriteLine("Permission preview: " + Path.Combine(AppPaths.Root, "permission-bar.png"));

            Choose("PermissionDismiss"); await ScriptUntil(core, "window.permissionResult==='denied'", "Notification prompt can be temporarily declined");
            await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, "https://permissions.test", CoreWebView2PermissionState.Default);
            await Load(); await RequestMedia("{audio:true}"); await Until(() => bar.Visibility == Visibility.Visible, "Microphone permission reaches the browser bar");
            Check(Field<TextBlock>(window, "PermissionDescription").Text.Contains("microfon"), "Microphone requests are clearly identified before devices are accessed");
            Choose("PermissionDeny"); await ScriptUntil(core, "window.mediaResult==='NotAllowedError'", "Microphone can be denied without opening a device");
            await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Camera, "https://permissions.test", CoreWebView2PermissionState.Default);
            await Load(); await RequestMedia("{video:true}"); await Until(() => bar.Visibility == Visibility.Visible, "Camera permission reaches the browser bar");
            Check(Field<TextBlock>(window, "PermissionDescription").Text.Contains("camera"), "Camera requests are clearly identified before devices are accessed");
            Choose("PermissionDeny"); await ScriptUntil(core, "window.mediaResult==='NotAllowedError'", "Camera can be denied without opening a device");

            await Load(); await Request(); await Until(() => bar.Visibility == Visibility.Visible, "Notification fixture resumes after device permission checks");
            var other = await (Task<BrowserTab>)Invoke(window, "AddTabAsync", AppConstants.NewTabUrl, true, null)!;
            Check(bar.Visibility == Visibility.Collapsed, "A permission request stays attached to its tab instead of covering another tab");
            tab.LastActiveUtc = DateTime.UtcNow.AddHours(-1); await (Task)Invoke(window, "ApplyMemoryPolicyAsync")!;
            Check(tab.View is not null && !tab.IsSuspended, "An unanswered permission request protects its controller from automatic eviction");
            await (Task)Invoke(window, "ActivateTabAsync", tab)!;
            Check(bar.Visibility == Visibility.Visible, "Returning to a tab restores its pending permission bar");
            Choose("PermissionDeny");
            await ScriptUntil(core, "window.permissionResult==='denied'", "Explicit Block resolves the native permission request");
            Check((await core.Profile.GetNonDefaultPermissionSettingsAsync()).Any(p => SiteDataService.Origin(p.PermissionOrigin) == "https://permissions.test" && p.PermissionKind == CoreWebView2PermissionKind.Notifications && p.PermissionState == CoreWebView2PermissionState.Deny), "Block is persisted by the native profile");
            await Load(); await Request();
            await ScriptUntil(core, "window.permissionResult==='denied'", "A saved Block is reused on subsequent page loads");
            Check(bar.Visibility == Visibility.Collapsed, "Saved choices do not produce another permission bar");

            await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, "https://permissions.test", CoreWebView2PermissionState.Default);
            await Load(); await Request(); await Until(() => bar.Visibility == Visibility.Visible, "Revoked choice asks again");
            Choose("PermissionDismiss");
            await ScriptUntil(core, "window.permissionResult==='denied'", "Not now declines without an enduring grant");
            Check(!(await core.Profile.GetNonDefaultPermissionSettingsAsync()).Any(p => SiteDataService.Origin(p.PermissionOrigin) == "https://permissions.test" && p.PermissionKind == CoreWebView2PermissionKind.Notifications), "Not now does not persist a permission decision");
            await Request(); await ScriptUntil(core, "window.permissionResult==='denied'", "Repeated requests in a dismissed document remain declined");
            Check(bar.Visibility == Visibility.Collapsed, "A page cannot repeatedly reopen a dismissed request");

            await Load(); await Request(); await Until(() => bar.Visibility == Visibility.Visible, "New document can ask after Not now");
            Choose("PermissionAllow"); await ScriptUntil(core, "window.permissionResult==='granted'", "Explicit Allow resolves the native request");
            Check((await core.Profile.GetNonDefaultPermissionSettingsAsync()).Any(p => SiteDataService.Origin(p.PermissionOrigin) == "https://permissions.test" && p.PermissionKind == CoreWebView2PermissionKind.Notifications && p.PermissionState == CoreWebView2PermissionState.Allow), "Allow is persisted for the requesting site");
            await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, "https://permissions.test", CoreWebView2PermissionState.Default);
            await Load(); await Request(); await Until(() => bar.Visibility == Visibility.Visible, "Navigation cancellation fixture waits for a choice");
            core.Navigate(AppConstants.NewTabUrl);
            await ScriptUntil(core, "location.hostname==='vela.internal' && document.readyState==='complete'", "Navigating away completes outstanding deferrals without a modal dialog");
            Check(bar.Visibility == Visibility.Collapsed && !(bool)Invoke(window, "HasPendingPermission", tab)!, "Navigation removes the old document's permission UI and controller protection");
            await Load();
            CoreWebView2Frame? frame = null;
            core.FrameCreated += (_, e) => frame = e.Frame;
            await core.ExecuteScriptAsync("(()=>{const f=document.createElement('iframe');f.id='permission-frame';f.src='permission-test.html?frame=1';document.body.append(f)})()");
            await Until(() => frame is not null, "Permission frame created");
            await UntilAsync(async () => await frame!.ExecuteScriptAsync("location.search==='?frame=1' && document.readyState==='complete'") == "true", "Permission frame loads");
            await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = "document.getElementById('permission-frame').contentWindow.Notification.requestPermission()", userGesture = true }));
            await Until(() => bar.Visibility == Visibility.Visible, "Frame permission reaches the bar");
            Check(((System.Collections.IList)window.GetType().GetField("_permissionRequests", Private)!.GetValue(window)!).Count == 1, "An iframe request is handled once instead of duplicated by frame and core events");
            await core.ExecuteScriptAsync("document.getElementById('permission-frame').remove()");
            await Until(() => bar.Visibility == Visibility.Collapsed, "Destroyed requesting frame cancels its pending permission");
            Check(!(bool)Invoke(window, "HasPendingPermission", tab)! && await core.ExecuteScriptAsync("3+3") == "6", "Frame removal releases its deferral and leaves the parent renderer responsive");

            var privateWindow = new MainWindow(window.BrowserEnvironment, isPrivate: true); privateWindow.Show();
            try
            {
                var privateTabs = Field<ObservableCollection<BrowserTab>>(privateWindow, "_tabs");
                await Until(() => privateTabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Private permission engine starts", 30000);
                await (Task)Invoke(privateWindow, "WaitForViewInitializationAsync")!;
                var privateCore = privateTabs[0].View!.CoreWebView2;
                privateCore.SetVirtualHostNameToFolderMapping("permissions.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
                privateCore.Navigate(url); await ScriptUntil(privateCore, "location.hostname==='permissions.test' && document.readyState==='complete'", "Private permission fixture loads");
                async Task PrivateRequest() => await privateCore.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = "window.permissionResult='pending';Notification.requestPermission().then(value=>window.permissionResult=value)", userGesture = true }));
                await PrivateRequest(); var privateBar = Field<Border>(privateWindow, "PermissionBar");
                await Until(() => privateBar.Visibility == Visibility.Visible, "Private request is displayed");
                Field<Button>(privateWindow, "PermissionDeny").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await ScriptUntil(privateCore, "window.permissionResult==='denied'", "Private Block resolves its request");
                Check(!(await privateCore.Profile.GetNonDefaultPermissionSettingsAsync()).Any(p => SiteDataService.Origin(p.PermissionOrigin) == "https://permissions.test" && p.PermissionKind == CoreWebView2PermissionKind.Notifications), "Private choices do not enter the native persistent permission store");
                privateCore.Navigate(url + "?private=2"); await ScriptUntil(privateCore, "location.search==='?private=2' && document.readyState==='complete'", "Private fixture reloads");
                await PrivateRequest(); await ScriptUntil(privateCore, "window.permissionResult==='denied'", "Private choices are reused within the same window");
                Check(privateBar.Visibility == Visibility.Collapsed, "Private reload does not repeat its permission bar");
            }
            finally { await CloseWindowAsync(privateWindow); }

            await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Notifications, "https://permissions.test", CoreWebView2PermissionState.Default);
            await Load(); await Request(); await Until(() => bar.Visibility == Visibility.Visible, "Close fixture has an unresolved native request");
        }
        finally { await CloseWindowAsync(window); }
    }
}
