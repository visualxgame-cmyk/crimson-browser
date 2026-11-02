using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private sealed class PermissionOperation(CoreWebView2PermissionRequestedEventArgs args)
    {
        private readonly CoreWebView2Deferral _deferral = args.GetDeferral();
        private bool _completed;
        internal void Complete(CoreWebView2PermissionState state, bool remember)
        {
            if (_completed) return;
            _completed = true;
            try { args.SavesInProfile = remember; args.State = state; }
            catch (Exception ex) { Log.Write("Resolve permission: " + ex.Message); }
            finally
            {
                // Dispose completes this SDK's deferral; do not call Complete twice.
                try { _deferral.Dispose(); }
                catch (Exception ex) { Log.Write("Complete permission: " + ex.Message); }
            }
        }
    }
    private sealed class PermissionRequest(BrowserTab tab, CoreWebView2 core, CoreWebView2Frame? frame, string origin, CoreWebView2PermissionKind kind)
    {
        internal BrowserTab Tab { get; } = tab;
        internal CoreWebView2 Core { get; } = core;
        internal CoreWebView2Frame? Frame { get; } = frame;
        internal string Origin { get; } = origin;
        internal CoreWebView2PermissionKind Kind { get; } = kind;
        internal List<PermissionOperation> Operations { get; } = [];
        internal bool Ready;
    }
    private readonly List<PermissionRequest> _permissionRequests = [];
    private readonly HashSet<(Guid Tab, CoreWebView2Frame? Frame, string Origin, CoreWebView2PermissionKind Kind)> _dismissedPermissions = [];
    private readonly Dictionary<(string Origin, CoreWebView2PermissionKind Kind), CoreWebView2PermissionState> _privatePermissionChoices = [];
    private PermissionRequest? _shownPermission;
    private bool HasPendingPermission(BrowserTab tab) => _permissionRequests.Any(p => ReferenceEquals(p.Tab, tab));

    private void OnPermissionRequested(BrowserTab tab, CoreWebView2PermissionRequestedEventArgs args, CoreWebView2Frame? frame = null)
    {
        args.Handled = true;
        var origin = SiteDataService.Origin(args.Uri);
        if (_shuttingDown || !_tabs.Contains(tab) || origin is null || tab.View?.CoreWebView2 is not { } core ||
            _dismissedPermissions.Contains((tab.Id, frame, origin, args.PermissionKind)) || _permissionRequests.Sum(p => p.Operations.Count) >= 32)
        { args.SavesInProfile = false; args.State = CoreWebView2PermissionState.Deny; return; }
        var request = _permissionRequests.FirstOrDefault(p => ReferenceEquals(p.Tab, tab) && ReferenceEquals(p.Frame, frame) && p.Origin == origin && p.Kind == args.PermissionKind);
        var operation = new PermissionOperation(args);
        if (request is not null) { request.Operations.Add(operation); return; }
        request = new(tab, core, frame, origin, args.PermissionKind);
        request.Operations.Add(operation); _permissionRequests.Add(request);
        // Leave the native callback before querying the profile or changing WPF layout.
        Dispatcher.BeginInvoke(() => _ = InitializePermissionAsync(request));
    }
    private async Task InitializePermissionAsync(PermissionRequest request)
    {
        try
        {
            if (!_permissionRequests.Contains(request)) return;
            var saved = IsPrivate && _privatePermissionChoices.TryGetValue((request.Origin, request.Kind), out var sessionChoice)
                ? sessionChoice
                : (await request.Core.Profile.GetNonDefaultPermissionSettingsAsync()).FirstOrDefault(p =>
                    SiteDataService.Origin(p.PermissionOrigin) == request.Origin && p.PermissionKind == request.Kind)?.PermissionState ?? CoreWebView2PermissionState.Default;
            if (!_permissionRequests.Contains(request)) return;
            if (saved != CoreWebView2PermissionState.Default) { ResolvePermission(request, saved, false); return; }
            request.Ready = true; RefreshPermissionBar();
        }
        catch (Exception ex)
        {
            Log.Write("Read permission choice: " + ex.Message);
            ResolvePermission(request, CoreWebView2PermissionState.Deny, false);
            Status("Permisiunea nu a putut fi verificată. Poți reîncărca pagina pentru a încerca din nou.");
        }
    }
    private void ResolvePermission(PermissionRequest request, CoreWebView2PermissionState state, bool remember)
    {
        if (!_permissionRequests.Remove(request)) return;
        if (remember && IsPrivate) _privatePermissionChoices[(request.Origin, request.Kind)] = state;
        foreach (var operation in request.Operations) operation.Complete(state, remember && !IsPrivate);
        RefreshPermissionBar();
    }
    private void CancelPermissions(BrowserTab tab, CoreWebView2Frame? frame = null, bool frameOnly = false)
    {
        foreach (var request in _permissionRequests.Where(p => ReferenceEquals(p.Tab, tab) && (!frameOnly || ReferenceEquals(p.Frame, frame))).ToArray())
            ResolvePermission(request, CoreWebView2PermissionState.Deny, false);
        _dismissedPermissions.RemoveWhere(p => p.Tab == tab.Id && (!frameOnly || ReferenceEquals(p.Frame, frame)));
    }
    private void AttachPermissionFrame(BrowserTab tab, CoreWebView2Frame frame)
    {
        frame.PermissionRequested += (_, args) => tab.Owner?.OnPermissionRequested(tab, args, frame);
        frame.NavigationStarting += (_, _) => tab.Owner?.CancelPermissions(tab, frame, frameOnly: true);
        frame.Destroyed += (_, _) => tab.Owner?.CancelPermissions(tab, frame, frameOnly: true);
        frame.FrameCreated += (_, args) => tab.Owner?.AttachPermissionFrame(tab, args.Frame);
    }
    private void RefreshPermissionBar()
    {
        _shownPermission = _permissionRequests.FirstOrDefault(p => p.Ready && ReferenceEquals(p.Tab, _active));
        PermissionBar.Visibility = _shownPermission is null ? Visibility.Collapsed : Visibility.Visible;
        if (_shownPermission is not { } request) return;
        PermissionOrigin.Text = request.Origin;
        var embedded = SiteDataService.Origin(request.Tab.Url) != request.Origin ? "Cadru încorporat: " : "";
        PermissionDescription.Text = embedded + "Solicită acces la " + Describe(request.Kind) + ".";
        PermissionHint.Text = IsPrivate ? "În privat, alegerea se păstrează doar în această fereastră." : "Poți modifica permisiunile salvate din meniul site-ului.";
    }
    private void PermissionChoice_Click(object sender, RoutedEventArgs e)
    {
        if (_shownPermission is not { } request || !ReferenceEquals(request.Tab, _active)) return;
        var action = (sender as FrameworkElement)?.Tag as string;
        var remember = action is "allow" or "deny";
        var state = action == "allow" ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        if (!remember) _dismissedPermissions.Add((request.Tab.Id, request.Frame, request.Origin, request.Kind));
        // Coalesce an explicit choice across pending requests for this origin in this window.
        foreach (var pending in _permissionRequests.Where(p => ReferenceEquals(p, request) || remember && p.Origin == request.Origin && p.Kind == request.Kind).ToArray())
            ResolvePermission(pending, state, remember);
        if (remember && !IsPrivate)
            foreach (var window in Application.Current.Windows.OfType<MainWindow>().Where(w => w != this && !w.IsPrivate))
                foreach (var pending in window._permissionRequests.Where(p => p.Origin == request.Origin && p.Kind == request.Kind).ToArray())
                    window.ResolvePermission(pending, state, true);
        request.Tab.View?.Focus();
    }
    private void PermissionBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true; PermissionChoice_Click(PermissionDismiss, new RoutedEventArgs());
    }
}
