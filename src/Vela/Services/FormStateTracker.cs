using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Vela.Services;

/// <summary>Tracks document/frame edits and media activity through events, without polling or retaining form data.</summary>
internal sealed class FormStateTracker : IDisposable
{
    private sealed class DocumentState
    {
        public string? Token;
        public bool Dirty;
        public bool Active;
        public bool Authentication;
        public EventHandler<CoreWebView2WebMessageReceivedEventArgs>? Message;
        public EventHandler<CoreWebView2ContentLoadingEventArgs>? Loading;
        public EventHandler<object>? Destroyed;
    }
    private static readonly Lazy<string> Script = new(() => SignInProtection.WrapScript(File.ReadAllText(Path.Combine(AppPaths.Assets, "form-protection.js"))));
    private readonly CoreWebView2 _core;
    private readonly Action<bool> _changed;
    private readonly Action<bool>? _activityChanged;
    private readonly Action<bool>? _authenticationChanged;
    private readonly DocumentState _document = new();
    private readonly Dictionary<CoreWebView2Frame, DocumentState> _frames = new();
    private bool _disposed;
    private bool _activity;
    private bool _authentication;
    public bool HasChanges => _document.Dirty || _frames.Values.Any(s => s.Dirty);
    private bool HasActivity => _document.Active || _frames.Values.Any(s => s.Active);

    public FormStateTracker(CoreWebView2 core, Action<bool> changed, Action<bool>? activityChanged = null, Action<bool>? authenticationChanged = null)
    {
        _core = core;
        _changed = changed;
        _activityChanged = activityChanged;
        _authenticationChanged = authenticationChanged;
        core.WebMessageReceived += OnMessage;
        core.ContentLoading += OnContentLoading;
        core.FrameCreated += OnFrameCreated;
    }

    public Task<string> InstallAsync() => _core.AddScriptToExecuteOnDocumentCreatedAsync(Script.Value);
    private void Update()
    {
        if (_disposed) return;
        _changed(HasChanges);
        var active = HasActivity;
        if (active != _activity) { _activity = active; _activityChanged?.Invoke(active); }
        var authentication = _document.Authentication || _frames.Values.Any(s => s.Authentication);
        if (authentication != _authentication) { _authentication = authentication; _authenticationChanged?.Invoke(authentication); }
    }
    private void OnContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e)
    { _document.Token = null; _document.Dirty = false; _document.Active = false; _document.Authentication = false; Update(); }
    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e) => Read(_document, e);
    private void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
    {
        if (_disposed) return;
        var frame = e.Frame;
        var state = new DocumentState();
        _frames.Add(frame, state);
        // Capture the registered wrapper: WebView2 can supply a different wrapper as event sender.
        state.Message = (_, args) => Read(state, args);
        state.Loading = (_, _) => { state.Token = null; state.Dirty = false; state.Active = false; state.Authentication = false; Update(); };
        state.Destroyed = (_, _) =>
        {
            // The native publisher tears down its subscriptions when destroyed. Calling event
            // removal on a destroyed frame can assert inside the WebView2 runtime.
            _frames.Remove(frame);
            Update();
        };
        frame.WebMessageReceived += state.Message;
        frame.ContentLoading += state.Loading;
        frame.FrameCreated += OnFrameCreated;
        frame.Destroyed += state.Destroyed;
    }
    private void Read(DocumentState state, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var kind) ||
                kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("vela-form-ready" or "vela-form-state" or "vela-activity-state") ||
                !root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String) return;
            var activity = kind.GetString() == "vela-activity-state";
            if (!root.TryGetProperty(activity ? "active" : "dirty", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
            if (kind.GetString() == "vela-form-ready") state.Token = token.GetString();
            if (state.Token != token.GetString()) return;
            state.Authentication = SignInProtection.IsInteractiveFrame(e.Source);
            if (activity) state.Active = value.GetBoolean(); else state.Dirty = value.GetBoolean();
            Update();
        }
        catch (JsonException) { /* Other pages can send arbitrary non-form messages. */ }
    }
    private void Detach(CoreWebView2Frame frame, DocumentState state)
    {
        frame.WebMessageReceived -= state.Message;
        frame.ContentLoading -= state.Loading;
        frame.FrameCreated -= OnFrameCreated;
        frame.Destroyed -= state.Destroyed;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.WebMessageReceived -= OnMessage;
        _core.ContentLoading -= OnContentLoading;
        _core.FrameCreated -= OnFrameCreated;
        foreach (var (frame, state) in _frames) Detach(frame, state);
        _frames.Clear();
    }

    /// <summary>Check native page unload listeners before destroying a renderer. Does not execute page handlers.</summary>
    public static async Task<bool> HasUnloadHandlerAsync(CoreWebView2 core)
    {
        string? objectId = null;
        try
        {
            // Keep the default empty object group: Chromium then returns listener
            // metadata without creating remote objects for the handler functions.
            using var window = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"window\"}"));
            objectId = window.RootElement.GetProperty("result").GetProperty("objectId").GetString();
            using var listeners = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOMDebugger.getEventListeners",
                JsonSerializer.Serialize(new { objectId })));
            return listeners.RootElement.GetProperty("listeners").EnumerateArray().Any(l => l.GetProperty("type").GetString() == "beforeunload");
        }
        finally
        {
            if (objectId is not null)
                await core.CallDevToolsProtocolMethodAsync("Runtime.releaseObject", JsonSerializer.Serialize(new { objectId }));
        }
    }
}
