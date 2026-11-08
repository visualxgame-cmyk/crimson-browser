using CefSharp;
using CefSharp.Handler;
using CrimsonBrowser.Models;

namespace CrimsonBrowser.Services;

internal sealed class TabLifeSpanHandler(BrowserTab tab) : LifeSpanHandler
{
    protected override bool OnBeforePopup(IWebBrowser control, IBrowser browser, IFrame frame, string targetUrl,
        string targetFrameName, WindowOpenDisposition disposition, bool userGesture, IPopupFeatures features,
        IWindowInfo windowInfo, IBrowserSettings settings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        PopupNavigationHandler.Open(tab, control, frame, targetUrl, userGesture);
        return true;
    }
    protected override bool DoClose(IWebBrowser control, IBrowser browser)
    {
        if (!browser.IsPopup) tab.Ui(control, tab.CloseRequested);
        return false;
    }
}

internal static class PopupNavigationHandler
{
    public static void Open(BrowserTab tab, IWebBrowser control, IFrame frame, string targetUrl, bool userGesture)
    {
        bool protection = ShieldService.Instance.IsShieldsEnabled && ShieldService.Instance.BlockPopups;
        if (!protection)
            tab.OpenPopup(targetUrl);
        else if (PopupNavigationPolicy.ShouldBlock(targetUrl, userGesture))
            tab.Ui(control, ShieldService.Instance.IncrementBlockedCount);
        else if (PopupNavigationPolicy.IsAuthenticationTarget(targetUrl))
            tab.OpenPopup(targetUrl);
        else
        {
            // CEF labels an advertiser's onclick window.open as a user gesture too.
            // Require an explicit, trusted link in the source frame, without blocking its thread.
            var result = frame.EvaluateScriptAsync("window.__crimsonAllowPopup?.(" + System.Text.Json.JsonSerializer.Serialize(targetUrl) + ") === true",
                timeout: TimeSpan.FromSeconds(3));
            _ = CompleteVerifiedPopup(tab, control, targetUrl, result);
        }
    }
    private static async Task CompleteVerifiedPopup(BrowserTab tab, IWebBrowser source, string url, Task<JavascriptResponse> verification)
    {
        bool allowed = false;
        try { allowed = (await verification).Result is true; }
        catch (Exception error) when (error is ObjectDisposedException or TaskCanceledException or InvalidOperationException or TimeoutException)
        {
            // A closed source frame cannot authorize a pending popup.
        }
        tab.Ui(source, () =>
        {
            if (allowed) tab.OpenPopup(url);
            else ShieldService.Instance.IncrementBlockedCount();
        });
    }
}

internal sealed class TabDisplayHandler(BrowserTab tab) : DisplayHandler
{
    protected override void OnAddressChanged(IWebBrowser control, AddressChangedEventArgs args)
        => tab.Ui(control, () => tab.AddressChanged(args.Address));
    protected override void OnTitleChanged(IWebBrowser control, TitleChangedEventArgs args)
        => tab.Ui(control, () => { if (!tab.IsDiscarded && !tab.IsStartPage) tab.Title = args.Title; });
    protected override void OnFaviconUrlChange(IWebBrowser control, IBrowser browser, IList<string> urls)
        => tab.Ui(control, () => tab.FaviconUrl = urls.FirstOrDefault());
    protected override void OnFullscreenModeChange(IWebBrowser control, IBrowser browser, bool fullscreen)
        => tab.SetFullscreen(fullscreen);
}

internal sealed class TabRequestHandler(BrowserTab tab) : RequestHandler
{
    protected override bool OnOpenUrlFromTab(IWebBrowser control, IBrowser browser, IFrame frame, string targetUrl,
        WindowOpenDisposition disposition, bool userGesture)
    {
        if (disposition is not (WindowOpenDisposition.NewForegroundTab or WindowOpenDisposition.NewBackgroundTab or
            WindowOpenDisposition.NewWindow or WindowOpenDisposition.NewPopup)) return false;
        PopupNavigationHandler.Open(tab, control, frame, targetUrl, userGesture);
        return true;
    }
    protected override void OnRenderProcessTerminated(IWebBrowser control, IBrowser browser,
        CefTerminationStatus status, int errorCode, string errorMessage)
        => tab.Ui(control, () => tab.EngineFailed($"{status} ({errorCode}): {errorMessage}"));
    protected override bool OnBeforeBrowse(IWebBrowser control, IBrowser browser, IFrame frame,
        IRequest request, bool gesture, bool redirect)
    {
        if (frame.IsMain && ShieldService.Instance.IsShieldsEnabled && ShieldService.Instance.BlockPopups &&
            ShieldRequestPolicy.ShouldBlockUrl(request.Url))
        {
            tab.Ui(control, ShieldService.Instance.IncrementBlockedCount);
            return true;
        }
        if (frame.IsMain && ShieldService.Instance.IsShieldsEnabled && ShieldService.Instance.EnforceHttps &&
            Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Scheme == "http" && !uri.IsLoopback)
        {
            tab.Ui(control, () => tab.Navigate(new UriBuilder(uri) { Scheme = "https", Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri.AbsoluteUri));
            return true;
        }
        return false;
    }
    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser control, IBrowser browser,
        IFrame frame, IRequest request, bool navigation, bool download, string initiator, ref bool disableDefaultHandling)
        => new ShieldResourceHandler();
    private sealed class ShieldResourceHandler : ResourceRequestHandler
    {
        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser control, IBrowser browser,
            IFrame frame, IRequest request, IRequestCallback callback)
        {
            if (!ShieldService.Instance.ShouldBlockUrl(request.Url)) return CefReturnValue.Continue;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(ShieldService.Instance.IncrementBlockedCount);
            return CefReturnValue.Cancel;
        }
    }
}

internal sealed class TabPermissionHandler(BrowserTab tab) : PermissionHandler
{
    protected override bool OnRequestMediaAccessPermission(IWebBrowser control, IBrowser browser, IFrame frame,
        string origin, MediaAccessPermissionType permissions, IMediaAccessCallback callback)
    {
        tab.AskPermission(new BrowserPermissionRequest(origin, permissions.ToString(), allowed =>
        {
            Cef.UIThreadTaskFactory.StartNew(() =>
            {
                using (callback)
                {
                    callback.Continue(allowed ? permissions : MediaAccessPermissionType.None);
                    if (allowed) tab.MarkMediaPermission();
                }
            });
        }));
        return true;
    }
    protected override bool OnShowPermissionPrompt(IWebBrowser control, IBrowser browser, ulong promptId,
        string origin, PermissionRequestType permissions, IPermissionPromptCallback callback)
    {
        tab.AskPermission(new BrowserPermissionRequest(origin, permissions.ToString(), allowed =>
            Cef.UIThreadTaskFactory.StartNew(() =>
            {
                using (callback)
                    callback.Continue(allowed ? PermissionRequestResult.Accept : PermissionRequestResult.Deny);
            })));
        return true;
    }
}

internal sealed class TabDownloadHandler(BrowserTab tab,
    Func<string, System.Windows.Window?, string?>? selectSavePath = null) : DownloadHandler, IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IBeforeDownloadCallback, byte> _pending = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _active = new();
    internal bool HasActiveDownloads => !_pending.IsEmpty || !_active.IsEmpty;
    protected override bool CanDownload(IWebBrowser control, IBrowser browser, string url, string method) => true;
    protected override bool OnBeforeDownload(IWebBrowser control, IBrowser browser, CefSharp.DownloadItem item,
        IBeforeDownloadCallback callback)
    {
        string suggested = System.IO.Path.GetFileName(item.SuggestedFileName);
        int downloadId = item.Id;
        _active.TryAdd(downloadId, 0);
        _pending.TryAdd(callback, 0);
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            string? path = null;
            try
            {
                if (tab.IsDisposed || !_pending.ContainsKey(callback)) return;
                var owner = System.Windows.Window.GetWindow(tab.WebView);
                if (selectSavePath != null) path = selectSavePath(suggested, owner);
                else
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog { FileName = suggested, OverwritePrompt = true };
                    if (dialog.ShowDialog(owner) == true) path = dialog.FileName;
                }
            }
            finally
            {
                if (path == null || tab.IsDisposed) _active.TryRemove(downloadId, out _);
                Complete(callback, tab.IsDisposed ? null : path);
            }
        }));
        return true;
    }
    private void Complete(IBeforeDownloadCallback callback, string? path)
    {
        if (!_pending.TryRemove(callback, out _)) return;
        Cef.UIThreadTaskFactory.StartNew(() =>
        {
            using (callback)
                if (path != null && !callback.IsDisposed) callback.Continue(path, false);
        });
    }
    public void Dispose()
    {
        foreach (var callback in _pending.Keys) Complete(callback, null);
    }
    protected override void OnDownloadUpdated(IWebBrowser control, IBrowser browser, CefSharp.DownloadItem item,
        IDownloadItemCallback callback)
    {
        string key = browser.Identifier + ":" + item.Id;
        if (item.IsInProgress && !item.IsComplete && !item.IsCancelled) _active.TryAdd(item.Id, 0);
        else _active.TryRemove(item.Id, out _);
        // DownloadItem snapshots are invalid after this callback, so copy primitive values.
        string path = item.FullPath, url = item.Url;
        long total = item.TotalBytes, received = item.ReceivedBytes;
        var status = item.IsComplete ? DownloadStatus.Completed : item.IsCancelled ? DownloadStatus.Cancelled :
            item.IsInProgress ? DownloadStatus.InProgress : DownloadStatus.Failed;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            DownloadsService.Instance.Update(key, path, url, total, received, status, tab.IsIncognito)));
    }
}

internal sealed class TabKeyboardHandler(BrowserTab tab) : KeyboardHandler
{
    protected override bool OnPreKeyEvent(IWebBrowser control, IBrowser browser, KeyType type, int key,
        int nativeKeyCode, CefEventFlags modifiers, bool systemKey, ref bool shortcut)
    {
        if (type != KeyType.RawKeyDown) return false;
        bool ctrl = modifiers.HasFlag(CefEventFlags.ControlDown), shift = modifiers.HasFlag(CefEventFlags.ShiftDown);
        if (modifiers.HasFlag(CefEventFlags.AltDown)) return false;
        if (key == 123) { tab.Ui(control, tab.ShowDevTools); return true; }
        string? action = key switch { 122 => "fullscreen", 116 => "reload", 27 => "escape", _ => null };
        if (ctrl && key is 187 or 107) action = "zoom-in";
        if (ctrl && key is 189 or 109) action = "zoom-out";
        if (ctrl && shift) action = key switch { 9 => "prev-tab", 84 => "reopen-tab", 78 => "incognito", 66 => "bookmarks-bar", _ => action };
        else if (ctrl) action = key switch
        {
            9 => "next-tab", 84 => "new-tab", 78 => "new-window", 87 => "close-tab", 76 => "focus-omnibox",
            82 => "reload", 68 => "bookmark", 72 => "history", 70 => "find", 48 => "zoom-reset",
            187 or 107 => "zoom-in", 189 or 109 => "zoom-out", >= 49 and <= 57 => "tab-" + (key - 48), _ => action
        };
        if (action is null) return false;
        tab.Shortcut(action);
        return true;
    }
}

internal sealed class TabFindHandler(BrowserTab tab) : FindHandler
{
    protected override void OnFindResult(IWebBrowser control, IBrowser browser, int id, int count,
        CefSharp.Structs.Rect selection, int activeMatchOrdinal, bool finalUpdate)
    {
        if (finalUpdate) tab.Ui(control, () => tab.FindResult(activeMatchOrdinal, count));
    }
}

internal sealed class TabRenderHandler(BrowserTab tab) : IRenderProcessMessageHandler
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _mediaTokens = new();
    public void OnContextCreated(IWebBrowser control, IBrowser browser, IFrame frame)
    {
        bool protectPopups = ShieldService.Instance.IsShieldsEnabled && ShieldService.Instance.BlockPopups;
        frame.ExecuteJavaScriptAsync(PopupProtectionScript.Source + "\nwindow.__crimsonSetPopupProtection(" +
            (protectPopups ? "true" : "false") + ");");
        if (!ShieldRequestPolicy.IsAuthenticationPage(frame.Url))
            frame.ExecuteJavaScriptAsync(BrowserScripts.ConfigureAdProtection(
                ShieldService.Instance.IsShieldsEnabled && ShieldService.Instance.BlockAdsAndTrackers));
        string token = Guid.NewGuid().ToString("N");
        _mediaTokens[frame.Identifier.ToString()] = token;
        frame.ExecuteJavaScriptAsync("(()=>{const report=()=>CefSharp.PostMessage('crimson-media:" + token +
            ":'+(Array.from(document.querySelectorAll('audio,video')).some(m=>!m.paused&&!m.ended)?'1':'0'));" +
            "['play','pause','ended','emptied'].forEach(name=>document.addEventListener(name,report,true));" +
            "document.addEventListener('input',()=>CefSharp.PostMessage('crimson-form-dirty'),{once:true,capture:true});report();})();");
    }
    // No managed per-context resources are retained; callback ownership belongs to CEF.
    public void OnContextReleased(IWebBrowser control, IBrowser browser, IFrame frame)
    {
        if (_mediaTokens.TryRemove(frame.Identifier.ToString(), out var token))
            tab.Ui(control, () => tab.SetMediaFrame(token, false));
    }
    public void OnFocusedNodeChanged(IWebBrowser control, IBrowser browser, IFrame frame, IDomNode node) { }
    public void OnUncaughtException(IWebBrowser control, IBrowser browser, IFrame frame, JavascriptException exception)
        => System.Diagnostics.Debug.WriteLine("CEF JavaScript: " + exception.Message);
}
