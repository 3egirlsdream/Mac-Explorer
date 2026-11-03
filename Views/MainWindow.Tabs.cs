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

    private readonly Dictionary<Guid, (ExplorerTabViewModel Tab, string Path)> _restoringFolderTabs = [];

    internal bool IsRestoringFolderTabs => _restoringFolderTabs.Count > 0;

    internal FolderTabSession CaptureFolderTabs()
    {
        if (_vm == null) return new FolderTabSession([], 0);
        return FolderTabSession.Capture(_vm.Tabs.Select(tab =>
            tab.FileList.IsSearchMode || tab.FileList.IsArchiveView ? null : tab.FileList.CurrentPath),
            _vm.Tabs.IndexOf(_vm.SelectedTab!));
    }

    internal bool CanStartFolderRestore => !_shutdownStarted && _vm?.Tabs.Count == 1
        && _vm.SelectedTab is { } tab && string.IsNullOrEmpty(tab.FileList.CurrentPath)
        && !tab.FileList.IsSearchMode;

    internal async Task RestoreFolderTabSessionAsync(FolderTabSession session)
    {
        var ordered = await PrepareRestoredFolderTabsAsync(session);
        async Task LoadNextAsync()
        {
            while (!_shutdownStarted && ordered.TryDequeue(out var id))
            {
                if (!_restoringFolderTabs.TryGetValue(id, out var pending) || _vm?.Tabs.Contains(pending.Tab) != true) continue;
                var fileList = pending.Tab.FileList;
                try
                {
                    if (fileList.CurrentPath == pending.Path && !fileList.IsSearchMode)
                        await fileList.NavigateToAsync(pending.Path);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Folder tab restore failed: {ex}");
                }
                finally { _restoringFolderTabs.Remove(id); }
            }
        }
        // Bound directory work without serializing the selected folder behind inactive tabs.
        await Task.WhenAll(LoadNextAsync(), LoadNextAsync());
        _restoringFolderTabs.Clear();
    }

    private async Task<Queue<Guid>> PrepareRestoredFolderTabsAsync(FolderTabSession session)
    {
        if (_shutdownStarted || _vm?.SelectedTab is not { } initialTab) return new Queue<Guid>();
        var model = _vm;
        var tabs = new List<ExplorerTabViewModel>();
        var selected = initialTab;
        for (var i = 0; i < session.Paths.Length; i++)
        {
            var path = session.Paths[i];
            var tab = i == 0 ? initialTab
                : await AddTabAsync(select: false, directoryPath: path, loadDirectory: false);
            if (tab == null) continue;
            if (i == 0) await tab.FileList.PrepareRestoredDirectoryViewAsync(path);
            _restoringFolderTabs[tab.Id] = (tab, path);
            tabs.Add(tab);
            if (i == session.SelectedIndex) selected = tab;
        }
        if (tabs.Count == 0) return new Queue<Guid>();
        // Select once, before any directory enumeration. Background completion never changes focus.
        model.SelectedTab = selected;
        SchedulePaneLayoutRebuild();
        // The queue retains IDs so a closed pending tab can be collected while other folders are loading.
        return new Queue<Guid>(new[] { selected.Id }.Concat(tabs.Where(tab => tab != selected).Select(tab => tab.Id)));
    }

    private void StopRestoringFolderTab(ExplorerTabViewModel tab)
    {
        if (_restoringFolderTabs.Remove(tab.Id)) tab.FileList.StopDirectoryWork();
    }

    private void StopRestoringFolderTabs()
    {
        foreach (var pending in _restoringFolderTabs.Values.ToArray()) StopRestoringFolderTab(pending.Tab);
    }

    internal static bool CanRestoreFolder(string path)
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
