using System.Windows;
using Vela.Models;
using Vela.Services;

namespace Vela;

public partial class MainWindow
{
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingGroupHeaders;
    internal void SetTabGroup(BrowserTab tab, string name)
    {
        if (!_tabs.Contains(tab)) throw new ArgumentException("Tabul nu mai există în această fereastră.");
        name = name.Trim();
        if (name.Length > 40 || name.Any(char.IsControl)) throw new ArgumentException("Numele grupului poate avea maximum 40 de caractere.");
        if (tab.IsPinned && name.Length > 0) throw new InvalidOperationException("Anulează fixarea înainte de grupare.");
        var canonical = _tabs.FirstOrDefault(t => t.GroupName.Equals(name, StringComparison.OrdinalIgnoreCase))?.GroupName ?? name;
        tab.GroupName = canonical;
        var last = _tabs.LastOrDefault(t => t != tab && t.GroupName == canonical && canonical.Length > 0);
        if (last is not null)
        {
            var index = _tabs.IndexOf(last); if (_tabs.IndexOf(tab) > index) index++;
            _tabs.Move(_tabs.IndexOf(tab), index);
        }
        UpdateGroupHeaders(); ScheduleSessionSave();
    }
    internal void ToggleGroup(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_collapsedGroups.Add(name)) _collapsedGroups.Remove(name);
        UpdateGroupHeaders(); ScheduleSessionSave();
    }
    private void UpdateGroupHeaders()
    {
        if (_updatingGroupHeaders) return;
        _updatingGroupHeaders = true;
        try
        {
            var first = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tab in _tabs)
            {
                var leader = tab.GroupName.Length > 0 && first.Add(tab.GroupName);
                tab.HeaderVisible = tab.IsPinned || tab.GroupName.Length == 0 || !_collapsedGroups.Contains(tab.GroupName) || leader || tab.IsSelected;
                tab.GroupPrefix = tab.GroupName.Length > 0 && (leader || tab.IsSelected && _collapsedGroups.Contains(tab.GroupName)) ? tab.GroupName + " · " : "";
            }
        }
        finally { _updatingGroupHeaders = false; }
    }
    private WorkspaceWindow? _workspaceWindow;
    internal void RefreshProfileBadge() => ProfileButton.Content = ProfileContext.Name;
    private void Workspace_Click(object sender, RoutedEventArgs e) => OpenWorkspace((sender as FrameworkElement)?.Tag as string ?? "profiles");
    internal void OpenWorkspace(string mode)
    {
        MenuPopup.IsOpen = false;
        if (_workspaceWindow is { IsLoaded: true }) { _workspaceWindow.ShowMode(mode); _workspaceWindow.Activate(); return; }
        _workspaceWindow = new WorkspaceWindow(this, mode) { Owner = this };
        _workspaceWindow.Closed += (_, _) => _workspaceWindow = null;
        _workspaceWindow.Show();
    }
}
