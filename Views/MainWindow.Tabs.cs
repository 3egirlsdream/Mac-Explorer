using Avalonia.Controls;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class MainWindow
{
    private readonly TabStripInteraction _tabInteraction;
    private ContextMenu? _tabContextMenu;
    private bool _closingTabs;

    private void MoveTab(ExplorerTabViewModel tab, int index)
    {
        _vm?.MoveTab(tab, index);
        // ListBox regenerates the moved header. Release only the detached native AX nodes.
        Avalonia.Threading.Dispatcher.UIThread.Post(Platforms.MacOS.MacAutomationLifetime.ReleaseDetached,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    internal void ShowTabContextMenu(Control target, ExplorerTabViewModel tab)
    {
        if (_vm == null || !_vm.Tabs.Contains(tab)) return;
        _tabContextMenu?.Close();
        var menu = new ContextMenu();
        var index = _vm.Tabs.IndexOf(tab);
        void Add(string key, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = LocalizationText.Get(key), IsEnabled = enabled && !_closingTabs };
            item.Click += async (_, e) => { e.Handled = true; menu.Close(); await action(); };
            menu.Items.Add(item);
        }
        Add("tabs.new_right", () => OpenTabToRightAsync(tab));
        menu.Items.Add(new Separator());
        Add("tabs.close_left", () => CloseTabsAroundAsync(tab, TabCloseRange.Left), index > 0);
        Add("tabs.close_right", () => CloseTabsAroundAsync(tab, TabCloseRange.Right), index < _vm.Tabs.Count - 1);
        Add("tabs.close_others", () => CloseTabsAroundAsync(tab, TabCloseRange.Others), _vm.Tabs.Count > 1);
        _tabContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_tabContextMenu, menu)) _tabContextMenu = null; };
        menu.Open(target);
    }

    internal enum TabCloseRange { Left, Right, Others }

    internal async Task OpenTabToRightAsync(ExplorerTabViewModel anchor)
    {
        if (_vm == null || !_vm.Tabs.Contains(anchor)) return;
        var path = anchor.FileList.CurrentPath;
        var isDirectory = Path.IsPathFullyQualified(path) && await Task.Run(() => Directory.Exists(path));
        if (_shutdownStarted || _vm == null || !_vm.Tabs.Contains(anchor)) return;
        await AddTabAsync(directoryPath: isDirectory ? path : VirtualPath.Home,
            insertIndex: _vm.Tabs.IndexOf(anchor) + 1);
    }

    internal async Task CloseTabsAroundAsync(ExplorerTabViewModel anchor, TabCloseRange range)
    {
        if (_vm == null || _closingTabs || !_vm.Tabs.Contains(anchor)) return;
        var index = _vm.Tabs.IndexOf(anchor);
        var targets = _vm.Tabs.Where((tab, i) => range switch
        {
            TabCloseRange.Left => i < index,
            TabCloseRange.Right => i > index,
            _ => !ReferenceEquals(tab, anchor)
        }).ToArray();
        _closingTabs = true;
        try
        {
            if (_vm.SelectedTab is { } selected && targets.Contains(selected)) _vm.SelectedTab = anchor;
            foreach (var tab in targets) await CloseTabCoreAsync(tab);
        }
        finally { _closingTabs = false; }
    }
}
