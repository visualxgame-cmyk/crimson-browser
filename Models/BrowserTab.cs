using System.ComponentModel;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using CefSharp;
using CrimsonBrowser.Services;

namespace CrimsonBrowser.Models;

public sealed class BrowserTab : INotifyPropertyChanged, IDisposable
{
    private string _title = "New Tab", _url = "crimson://start";
    private string? _faviconUrl, _discardedUrl, _discardedTitle;
    private bool _isLoading, _isActive, _canGoBack, _canGoForward, _isDiscarded;
    private bool _navigationComplete, _mediaActive, _mediaPermission;
    private bool _formDirty;
    private readonly ConcurrentDictionary<BrowserPermissionRequest, byte> _permissions = new();
    private string _findTerm = "";
    private TaskCompletionSource<(int, int)>? _findCompletion;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool IsIncognito { get; }
    public bool IsDisposed { get; private set; }
    public CefBrowserView WebView { get; }
    public bool FocusOmniboxOnStartPage { get; set; }
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
    public bool IsDiscarded => _isDiscarded;
    public bool IsDocumentPlayingAudio => _mediaActive || _mediaPermission;
    public bool IsStartPage => _url == "crimson://start" || IsStartAddress(_url);
    public string Title { get => _title; set => SetField(ref _title, value); }
    public string Url { get => _url; set => SetField(ref _url, IsStartAddress(value) ? "crimson://start" : value); }
    public string? FaviconUrl { get => _faviconUrl; set => SetField(ref _faviconUrl, value); }
    public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }
    public bool CanGoBack { get => _canGoBack; private set => SetField(ref _canGoBack, value); }
    public bool CanGoForward { get => _canGoForward; private set => SetField(ref _canGoForward, value); }
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetField(ref _isActive, value)) return;
            WebView.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value) { NoteActivity(); RestoreFromDiscard(); }
            ApplyMemoryPolicy(value);
        }
    }
    public event Action<BrowserTab, string>? RequestNewTab;
    public event Action<BrowserTab>? RequestClose;
    public event Action? RequestOpenSettings;
    public event Action<BrowserTab, string>? RequestShortcut;
    public event Action<bool>? FullscreenChanged;
    public event Action<BrowserTab, BrowserPermissionRequest>? PermissionRequested;
    public event Action<BrowserTab, string>? InitializationFailed;
    public event Action<BrowserTab>? StartPageReady;
    public event PropertyChangedEventHandler? PropertyChanged;

    public BrowserTab(IRequestContext context, bool isIncognito = false)
    {
        IsIncognito = isIncognito;
        WebView = new CefBrowserView { RequestContext = context, Visibility = Visibility.Collapsed };
        WebView.LifeSpanHandler = new TabLifeSpanHandler(this);
        WebView.DisplayHandler = new TabDisplayHandler(this);
        WebView.RequestHandler = new TabRequestHandler(this);
        WebView.PermissionHandler = new TabPermissionHandler(this);
        WebView.DownloadHandler = new TabDownloadHandler(this);
        WebView.KeyboardHandler = new TabKeyboardHandler(this);
        WebView.FindHandler = new TabFindHandler(this);
        WebView.RenderProcessMessageHandler = new TabRenderHandler();
        WebView.LoadingStateChanged += (_, args) => Ui(() =>
        {
            IsLoading = args.IsLoading;
            CanGoBack = args.CanGoBack;
            CanGoForward = args.CanGoForward;
            if (!args.IsLoading) ApplyMemoryPolicy(IsActive);
        });
        WebView.FrameLoadEnd += (_, args) =>
        {
            if (!args.Frame.IsMain) return;
            Ui(() =>
            {
                if (_isDiscarded) return;
                _navigationComplete = true;
                if (IsStartPage)
                {
                    Title = LocalizationService.Instance.Get("NewTab");
                    UpdateStartPageLanguage(LocalizationService.Instance.CurrentLanguage);
                    if (FocusOmniboxOnStartPage) StartPageReady?.Invoke(this);
                }
                else if (!IsIncognito) HistoryService.Instance.AddEntry(Title, Url);
            });
        };
        WebView.LoadError += (_, args) =>
        {
            if (args.Frame.IsMain && args.ErrorCode != CefErrorCode.Aborted)
                Ui(() => IsLoading = false);
        };
        WebView.JavascriptMessageReceived += (_, args) =>
        {
            string? message = args.Message as string;
            if (message is null) return;
            // Message frame wrappers have callback scope. Read WPF state on its dispatcher.
            // The bundled start page permits no frames or third-party scripts (CSP).
            if (message == "open-settings")
                Ui(() => { if (IsStartAddress(WebView.Address)) RequestOpenSettings?.Invoke(); });
            if (message == "crimson-media-active") Ui(() => _mediaActive = true);
            if (message == "crimson-form-dirty") Ui(() => _formDirty = true);
        };
        WebView.Address = "https://start.crimson/startpage.html";
    }

    internal void Ui(Action action)
    {
        if (IsDisposed || WebView.Dispatcher.HasShutdownStarted) return;
        WebView.Dispatcher.BeginInvoke(new Action(() => { if (!IsDisposed) action(); }));
    }
    internal void AddressChanged(string address)
    {
        if (_isDiscarded) return;
        Url = address;
        _mediaActive = _mediaPermission = false;
        _formDirty = false;
        _navigationComplete = false;
        if (IsStartPage) Title = LocalizationService.Instance.Get("NewTab");
    }
    internal void OpenPopup(string url) => Ui(() => RequestNewTab?.Invoke(this, url));
    internal void CloseRequested() => Ui(() => RequestClose?.Invoke(this));
    internal void Shortcut(string action) => Ui(() => RequestShortcut?.Invoke(this, action));
    internal void SetFullscreen(bool value) => Ui(() => FullscreenChanged?.Invoke(value));
    internal void EngineFailed(string message) => Ui(() => InitializationFailed?.Invoke(this, message));
    internal void AskPermission(BrowserPermissionRequest request)
    {
        if (IsDisposed || WebView.Dispatcher.HasShutdownStarted) { request.Complete(false); return; }
        _permissions.TryAdd(request, 0);
        WebView.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsDisposed) request.Complete(false);
            else if (PermissionRequested is { } handler) handler(this, request);
            else request.Complete(false);
            foreach (var completed in _permissions.Keys.Where(p => p.IsCompleted))
                _permissions.TryRemove(completed, out _);
        }));
    }
    internal void MarkMediaPermission() => Ui(() => _mediaPermission = true);
    internal void FindResult(int index, int count)
    {
        var completion = _findCompletion;
        _findCompletion = null;
        completion?.TrySetResult((index, count));
    }
    public void NoteActivity() => LastActivityUtc = DateTime.UtcNow;
    public void ApplyMemoryPolicy(bool active)
    {
        if (IsDisposed || !WebView.IsBrowserInitialized) return;
        // CEF throttles invisible OSR browsers. Keep media and forms intact until idle discard.
        WebView.GetBrowser().GetHost().WasHidden(!active);
    }
    public void SetWindowMinimized(bool minimized)
    {
        if (!IsDisposed && IsActive && WebView.IsBrowserInitialized)
            WebView.GetBrowser().GetHost().WasHidden(minimized);
    }
    public void Discard()
    {
        if (IsActive || _isDiscarded || IsStartPage || !_navigationComplete || IsDocumentPlayingAudio ||
            _formDirty || DownloadsService.Instance.ActiveCount > 0) return;
        _discardedUrl = Url;
        _discardedTitle = Title;
        _isDiscarded = true;
        WebView.Address = "about:blank";
    }
    private void RestoreFromDiscard()
    {
        if (!_isDiscarded) return;
        string target = _discardedUrl ?? "crimson://start";
        _isDiscarded = false;
        _discardedUrl = _discardedTitle = null;
        Navigate(target);
    }
    public void Navigate(string target)
    {
        if (IsDisposed) return;
        _isDiscarded = false;
        if (string.IsNullOrWhiteSpace(target) || target == "crimson://start") { LoadStartPage(); return; }
        string address = target.Trim().Trim('"');
        if (File.Exists(address)) address = new Uri(address).AbsoluteUri;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https" or "file" or "about" or "data"))
            address = address.Contains('.') && !address.Contains(' ')
                ? "https://" + address : SettingsService.Instance.BuildSearchUrl(address);
        // Hidden CEF controls initialize lazily. Session saving must retain their
        // requested URLs even before their first native AddressChanged callback.
        Url = address;
        _navigationComplete = false;
        WebView.Address = address;
    }
    public void LoadStartPage() { Url = "crimson://start"; WebView.Address = "https://start.crimson/startpage.html"; }
    public void GoBack() { if (WebView.CanGoBack) WebView.Back(); }
    public void GoForward() { if (WebView.CanGoForward) WebView.Forward(); }
    public void Reload() { if (_isDiscarded) RestoreFromDiscard(); else WebView.Reload(); }
    public void Stop() => WebView.Stop();
    public void UpdateHistoryState() { CanGoBack = WebView.CanGoBack; CanGoForward = WebView.CanGoForward; }
    public void ShowDevTools() { if (WebView.IsBrowserInitialized) WebView.ShowDevTools(); }
    public void ExitDocumentFullscreen()
    {
        if (WebView.IsBrowserInitialized)
            WebView.ExecuteScriptAsync("if(document.fullscreenElement) document.exitFullscreen().catch(()=>{});");
    }
    public void UpdateStartPageLanguage(string lang)
    {
        if (IsStartPage && WebView.IsBrowserInitialized)
            WebView.ExecuteScriptAsync("if(window.applyLanguage) applyLanguage(" + JsonSerializer.Serialize(lang) + ");");
    }
    public void RefreshStartPageWallpaper()
    {
        if (IsStartPage && WebView.IsBrowserInitialized) WebView.ExecuteScriptAsync("if(window.checkWallpaper) checkWallpaper();");
    }
    public async Task<(int Index, int Count)> FindAsync(string term, bool backward = false)
    {
        if (!WebView.IsBrowserInitialized) return (0, 0);
        _findCompletion?.TrySetResult((0, 0));
        var completion = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _findCompletion = completion;
        WebView.Find(term, forward: !backward, matchCase: false, findNext: term == _findTerm);
        _findTerm = term;
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { return (0, 0); }
        finally { if (ReferenceEquals(_findCompletion, completion)) _findCompletion = null; }
    }
    public void StopFind()
    {
        _findTerm = "";
        _findCompletion?.TrySetResult((0, 0));
        _findCompletion = null;
        if (WebView.IsBrowserInitialized) WebView.StopFinding(true);
    }
    public async Task ClearBrowsingDataAsync()
    {
        using var completion = new TaskCompletionCallback();
        WebView.RequestContext.ClearHttpCache(completion);
        await completion.Task;
        using var cookies = await WebView.RequestContext.GetCookieManagerAsync();
        await cookies.DeleteCookiesAsync("", "");
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _findCompletion?.TrySetResult((0, 0));
        foreach (var request in _permissions.Keys) request.Complete(false);
        _permissions.Clear();
        (WebView.DownloadHandler as IDisposable)?.Dispose();
        WebView.Dispose();
    }
    internal static bool IsStartAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "start.crimson";
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
