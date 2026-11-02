using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    public bool IsPrivate { get; }
    private readonly bool _suppressStartupTab;
    private Point _tabDragOrigin;
    private readonly Dictionary<Guid, PopupGuard> _popupGuards = [];
    private bool _openLinksInBackground => !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) &&
        (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Mouse.MiddleButton == MouseButtonState.Pressed);
    internal CoreWebView2Environment? BrowserEnvironment => _env;
    private static AdBlocker CreateSharedBlocker() { var blocker = new AdBlocker(); blocker.Load(); return blocker; }
    private static readonly Lazy<string> LinkGestures = new(() => System.IO.File.ReadAllText(System.IO.Path.Combine(AppPaths.Assets, "link-gestures.js")));
    private async Task InstallLinkGesturesAsync(BrowserTab tab, CoreWebView2 core)
    {
        void Read(object? sender, CoreWebView2WebMessageReceivedEventArgs message)
        {
            try
            {
                if (message.WebMessageAsJson.Length > 10000) return;
                using var json = System.Text.Json.JsonDocument.Parse(message.WebMessageAsJson);
                var root = json.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String ||
                    type.GetString() != "vela-background-link" || !root.TryGetProperty("url", out var target) || target.ValueKind != System.Text.Json.JsonValueKind.String || target.GetString() is not { Length: <= 8192 } url ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file") ||
                    uri.Host is AppConstants.InternalHost or AppConstants.BackgroundHost or ShortcutStore.IconHost ||
                    !Uri.TryCreate(message.Source, UriKind.Absolute, out var source) || (uri.IsFile && !source.IsFile)) return;
                // This bridge can only perform a normal background navigation, never internal-page commands.
                var owner = tab.Owner;
                if (owner is not null && !owner._shuttingDown) _ = owner.AddTabAsync(url, select: false);
            }
            catch (System.Text.Json.JsonException) { /* Unrelated page messages have their own handlers. */ }
        }
        void AttachFrame(CoreWebView2Frame frame)
        {
            frame.WebMessageReceived += Read;
            frame.FrameCreated += (_, args) => AttachFrame(args.Frame);
            // Native publishers release these handlers with their frame; no global frame collection is retained.
        }
        core.WebMessageReceived += Read;
        core.FrameCreated += (_, args) => AttachFrame(args.Frame);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(LinkGestures.Value);
    }
    private async void OnNewWindowRequested(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!AllowPopup(opener, e))
        {
            e.Handled = true;
            Status("Vela a blocat popupuri automate repetate de la acest tab.");
            return;
        }
        using var deferral = e.GetDeferral();
        e.Handled = true;
        var select = !_openLinksInBackground;
        BrowserTab? tab = null;
        try
        {
            // Native window routing retains window.opener, POST bodies and authentication callbacks.
            // The receiving control must share the environment/profile and must never have navigated.
            await Dispatcher.Yield(DispatcherPriority.Normal);
            if (_shuttingDown) return;
            tab = CreateTabModel(string.IsNullOrWhiteSpace(e.Uri) ? "about:blank" : e.Uri);
            tab.OpenerTabId = opener.Id;
            var view = await CreateViewAsync(tab, navigate: false) ?? throw new InvalidOperationException("Motorul ferestrei noi nu a putut fi inițializat.");
            e.NewWindow = view.CoreWebView2;
            AttachBlocker(tab, view.CoreWebView2);
            if (select) await ActivateTabAsync(tab);
            else EnforceLiveTabLimit();
        }
        catch (Exception ex)
        {
            Log.Write("Open native popup: " + ex); Status("Fereastra solicitată nu a putut fi deschisă.");
            if (tab is not null) { tab.FormTracker?.Dispose(); ContentHost.Children.Remove(tab.View); tab.View?.Dispose(); tab.PropertyChanged -= OnTabPropertyChanged; _tabs.Remove(tab); }
        }
    }
    private bool AllowPopup(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs request)
    {
        if (!_popupGuards.TryGetValue(opener.Id, out var guard)) _popupGuards[opener.Id] = guard = new PopupGuard();
        return guard.Allow(request.IsUserInitiated, DateTime.UtcNow);
    }
    private CoreWebView2ControllerOptions ControllerOptions()
    {
        var options = _env!.CreateCoreWebView2ControllerOptions();
        options.ProfileName = IsPrivate ? "VelaPrivate" : "Default";
        options.IsInPrivateModeEnabled = IsPrivate;
        return options;
    }
    private void InitializeWorkspaceFeatures()
    {
        PrivateBadge.Visibility = IsPrivate ? Visibility.Visible : Visibility.Collapsed;
        ProfileButton.Content = ProfileContext.Name;
        Title = IsPrivate ? "Vela — Privat" : "Vela";
        if (!IsPrivate) SourceInitialized += (_, _) => WindowPlacementStore.Restore(this);
        _bookmarks.Changed += WorkspaceBookmarksChanged;
        Closed += (_, _) => _bookmarks.Changed -= WorkspaceBookmarksChanged;
        RootGrid.AllowDrop = true;
        RootGrid.Drop += Tab_Drop;
        RootGrid.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent("Vela.Tab")) { e.Effects = DragDropEffects.Move; e.Handled = true; }
        };
        // A modifier only influences a link-opening gesture; no timer or per-frame keyboard polling.
    }
    private void SyncTabAddress(BrowserTab tab)
    {
        if (ReferenceEquals(tab, _active) && !Omnibox.IsKeyboardFocusWithin) SetOmnibox(tab.Url);
    }
    private void WorkspaceBookmarksChanged() => Dispatcher.InvokeAsync(() => { if (!_shuttingDown) { UpdateChrome(); if (MenuPopup.IsOpen) PopulateMenu(); } });
    private void PrivateWindow_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        new MainWindow(_env, isPrivate: true).Show();
    }
    private void ToggleTabMute(BrowserTab tab)
    {
        tab.IsMuted = !tab.IsMuted;
        if (tab.View?.CoreWebView2 is { } core) core.IsMuted = tab.IsMuted;
    }
    private void Audio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab }) ToggleTabMute(tab);
        e.Handled = true;
    }
    internal void PinTab(BrowserTab tab, bool pin)
    {
        if (pin) tab.GroupName = "";
        if (!_tabs.Contains(tab) || tab.IsPinned == pin) return;
        tab.IsPinned = pin;
        var pinned = _tabs.Count(t => t.IsPinned);
        var index = pin ? pinned - 1 : pinned;
        _tabs.Move(_tabs.IndexOf(tab), Math.Clamp(index, 0, _tabs.Count - 1));
        ScheduleSessionSave();
    }
    private void Tab_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab } element) return;
        var menu = new ContextMenu();
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(tab.IsPinned ? "Anulează fixarea" : "Fixează tabul", () => PinTab(tab, !tab.IsPinned));
        Add("Duplică tabul", () => DuplicateTab(tab));
        Add(tab.IsMuted ? "Activează sunetul" : "Oprește sunetul", () => ToggleTabMute(tab));
        Add("Mută într-o fereastră nouă", () => _ = MoveTabToNewWindowAsync(tab));
        Add("Grupuri de taburi…", () => OpenWorkspace("groups"));
        if (tab.GroupName.Length > 0) Add("Restrânge / extinde grupul " + tab.GroupName, () => ToggleGroup(tab.GroupName));
        menu.Items.Add(new Separator());
        Add("Închide tabul", () => CloseTab(tab));
        element.ContextMenu = menu;
    }
    private void Tab_DragMouseDown(object sender, MouseButtonEventArgs e) => _tabDragOrigin = e.GetPosition(this);
    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: BrowserTab tab } element) return;
        var delta = e.GetPosition(this) - _tabDragOrigin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(element, new DataObject("Vela.Tab", tab.Id.ToString()), DragDropEffects.Move);
    }
    private async void Tab_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("Vela.Tab") || !Guid.TryParse(e.Data.GetData("Vela.Tab") as string, out var id)) return;
        e.Handled = true;
        var source = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w._tabs.Any(t => t.Id == id));
        var tab = source?._tabs.FirstOrDefault(t => t.Id == id);
        if (source is null || tab is null || source.IsPrivate != IsPrivate) return;
        try
        {
            if (!ReferenceEquals(source, this)) await source.TransferTabAsync(tab, this);
            var target = (e.OriginalSource as FrameworkElement)?.DataContext as BrowserTab;
            var targetIndex = target is not null && _tabs.Contains(target) ? _tabs.IndexOf(target) : _tabs.Count - 1;
            var pinned = _tabs.Count(t => t.IsPinned);
            targetIndex = tab.IsPinned ? Math.Clamp(targetIndex, 0, Math.Max(0, pinned - 1)) : Math.Clamp(targetIndex, pinned, _tabs.Count - 1);
            _tabs.Move(_tabs.IndexOf(tab), targetIndex);
            ScheduleSessionSave();
        }
        catch (Exception ex) { Log.Write("Move browser tab: " + ex); Status("Tabul nu a putut fi mutat."); }
    }
    private async Task MoveTabToNewWindowAsync(BrowserTab tab)
    {
        var window = new MainWindow(_env, IsPrivate, suppressStartupTab: true);
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        try { await TransferTabAsync(tab, window); }
        catch (Exception ex) { Log.Write("Move tab to window: " + ex); Status("Tabul nu a putut fi mutat."); window.Close(); }
    }
    internal async Task TransferTabAsync(BrowserTab tab, MainWindow destination)
    {
        if (!_tabs.Contains(tab) || ReferenceEquals(this, destination) || IsPrivate != destination.IsPrivate)
            throw new InvalidOperationException("Mutarea trebuie făcută între ferestre din același mod de navigare.");
        if (_shuttingDown || destination._shuttingDown || _windowClosePending || destination._windowClosePending)
            throw new InvalidOperationException("O fereastră se închide.");
        await WaitForViewInitializationAsync();
        if (!_tabs.Contains(tab) || _closingTabs.Contains(tab.Id) || _shuttingDown || destination._shuttingDown || _windowClosePending || destination._windowClosePending)
            throw new InvalidOperationException("Tabul sau fereastra se închide.");
        var index = _tabs.IndexOf(tab);
        var wasActive = ReferenceEquals(_active, tab);
        CancelPermissions(tab);
        if (tab.View is { } view)
        {
            WebViewWindowLifetime.Retarget(view, this, destination);
            try { ContentHost.Children.Remove(view); destination.ContentHost.Children.Add(view); }
            catch
            {
                destination.ContentHost.Children.Remove(view);
                if (!ContentHost.Children.Contains(view)) ContentHost.Children.Add(view);
                WebViewWindowLifetime.Retarget(view, destination, this);
                throw;
            }
        }
        tab.PropertyChanged -= OnTabPropertyChanged;
        foreach (var operation in _downloads.Where(p => ReferenceEquals(p.Value.Tab, tab)).Select(p => p.Key).ToArray())
        {
            DetachDownload(operation);
            destination.TrackDownload(tab, operation);
        }
        _tabs.Remove(tab);
        UpdateDownloadIndicator();
        tab.Owner = destination;
        tab.IsSelected = false;
        tab.PropertyChanged += destination.OnTabPropertyChanged;
        var insert = tab.IsPinned ? destination._tabs.Count(t => t.IsPinned) : destination._tabs.Count;
        destination._tabs.Insert(insert, tab);
        if (wasActive)
        {
            _active = null;
            if (_tabs.Count > 0) await ActivateTabAsync(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        await destination.ActivateTabAsync(tab);
        ScheduleSessionSave();
        destination.ScheduleSessionSave();
        if (_tabs.Count == 0) Close();
    }
}
