using Microsoft.Extensions.DependencyInjection;
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

    private async Task<bool> RestoreFolderTabSessionAsync()
    {
        if (_vm?.SelectedTab is not { } initialTab) return false;
        var model = _vm;
        var expectedTabs = model.Tabs.ToList();
        var expectedPath = initialTab.FileList.CurrentPath;
        bool Interrupted() => _shutdownStarted || !ReferenceEquals(_vm, model)
            || !ReferenceEquals(model.SelectedTab, initialTab) || !model.Tabs.SequenceEqual(expectedTabs)
            || initialTab.FileList.CurrentPath != expectedPath;

        var settings = App.Services.GetRequiredService<ISettingsService>();
        if (!settings.Get(FolderTabSession.EnabledKey, false)) return false;
        var session = await Task.Run(() => FolderTabSession.Read(settings, CanRestoreFolder));
        if (Interrupted()) return true; // A newer user action wins over startup, including the legacy fallback.
        if (session == null) return false;

        ExplorerTabViewModel? selected = null;
        var restored = 0;
        for (var i = 0; i < session.Paths.Length; i++)
        {
            var path = session.Paths[i];
            var available = await Task.Run(() => CanRestoreFolder(path));
            if (Interrupted()) return true;
            if (!available) continue;
            ExplorerTabViewModel restoredTab;
            if (restored == 0)
            {
                restoredTab = initialTab;
                await initialTab.FileList.NavigateToAsync(path);
                expectedPath = path;
            }
            else
            {
                var added = await AddTabAsync(select: false, directoryPath: path);
                if (added == null) continue;
                restoredTab = added;
                expectedTabs.Add(added);
            }
            if (Interrupted()) return true;
            if (i == session.SelectedIndex) selected = restoredTab;
            restored++;
        }
        if (selected != null) model.SelectedTab = selected;
        return restored > 0;
    }

    private static bool CanRestoreFolder(string path)
        => (!DistributionChannel.IsAppStore || DirectoryAccess.Current.CanAccess(path)) && Directory.Exists(path);

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
