using System.IO;
using System.Windows;
using CrimsonBrowser.Models;

namespace CrimsonBrowser.Services;

public sealed class BrowserPermissionRequest(string origin, string kind, Action<bool> resolve)
{
    private int _completed;
    public string Origin { get; } = origin;
    public string Kind { get; } = kind;
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;
    public void Complete(bool allow)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0) resolve(allow);
    }
}

internal sealed class PermissionBroker
{
    private readonly Window _owner;
    private readonly bool _persist;
    private readonly Dictionary<string, bool> _choices;
    private readonly Queue<(BrowserTab Tab, BrowserPermissionRequest Request, string Page)> _queue = new();
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrimsonBrowser", "cef-permissions.json");
    private bool _pumping, _closed;
    public PermissionBroker(Window owner, bool persistChoices)
    {
        _owner = owner;
        _persist = persistChoices;
        _choices = _persist ? AtomicFileStore.Read<Dictionary<string, bool>>(_path) ?? new() : new();
        owner.Closed += (_, _) =>
        {
            _closed = true;
            while (_queue.TryDequeue(out var pending)) pending.Request.Complete(false);
        };
    }
    public void Handle(BrowserTab tab, BrowserPermissionRequest request)
    {
        if (_closed || tab.IsDisposed) { request.Complete(false); return; }
        _queue.Enqueue((tab, request, tab.Url));
        if (!_pumping) _owner.Dispatcher.BeginInvoke(Pump);
    }
    private void Pump()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_queue.TryDequeue(out var pending))
            {
                var request = pending.Request;
                if (request.IsCompleted || _closed || pending.Tab.IsDisposed || pending.Tab.Url != pending.Page ||
                    !Uri.TryCreate(request.Origin, UriKind.Absolute, out var origin) ||
                    origin.Scheme is not ("http" or "https"))
                {
                    request.Complete(false);
                    continue;
                }
                string site = origin.GetLeftPart(UriPartial.Authority);
                string key = site + "|" + request.Kind;
                if (_choices.TryGetValue(key, out bool saved)) { request.Complete(saved); continue; }
                var decision = CrimsonPermissionDialog.Show(_owner, site, Describe(request.Kind), _persist);
                bool stillValid = !_closed && !pending.Tab.IsDisposed && pending.Tab.Url == pending.Page;
                if (decision.Remember && stillValid)
                {
                    _choices[key] = decision.Allowed;
                    if (_persist)
                    {
                        try { AtomicFileStore.Write(_path, _choices); }
                        catch (IOException ex) { System.Diagnostics.Debug.WriteLine("Permission persistence: " + ex.Message); }
                    }
                }
                request.Complete(stillValid && decision.Allowed);
            }
        }
        finally { _pumping = false; }
    }
    private static string Describe(string kind)
    {
        var loc = LocalizationService.Instance;
        if (kind.Contains("Video", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("Camera", StringComparison.OrdinalIgnoreCase))
            return loc.Get("PermissionCamera") + (kind.Contains("Audio", StringComparison.OrdinalIgnoreCase)
                ? " / " + loc.Get("PermissionMicrophone") : "");
        if (kind.Contains("Audio", StringComparison.OrdinalIgnoreCase)) return loc.Get("PermissionMicrophone");
        if (kind.Contains("Notification", StringComparison.OrdinalIgnoreCase)) return loc.Get("PermissionNotifications");
        if (kind.Contains("Location", StringComparison.OrdinalIgnoreCase) || kind.Contains("Geolocation", StringComparison.OrdinalIgnoreCase))
            return loc.Get("PermissionLocation");
        if (kind.Contains("Clipboard", StringComparison.OrdinalIgnoreCase)) return loc.Get("PermissionClipboard");
        return loc.Get("PermissionGeneric");
    }
}
