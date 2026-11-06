using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Views;

public partial class MainWindow
{
    private readonly TabStripInteraction _tabInteraction;
    private ContextMenu? _tabContextMenu;
    private bool _closingTabs;

    private readonly Dictionary<Guid, (ExplorerTabViewModel Tab, string Path)> _restoringFolderTabs = [];
    private Queue<Guid>? _preparedFolderTabs;
    private FolderWindowSession? _initialFolderWindows;

    internal bool IsRestoringFolderTabs => _restoringFolderTabs.Count > 0;

    internal FolderTabSession CaptureFolderTabs()
    {
        if (_vm == null) return new FolderTabSession([], 0);
        _tabInteraction.FinishGroupMotion();
        return FolderTabSession.Capture(_vm.Tabs.Select(tab =>
            tab.FileList.IsSearchMode || tab.FileList.IsArchiveView ? null : tab.FileList.CurrentPath),
            _vm.Tabs.IndexOf(_vm.SelectedTab!), _vm.CaptureGroups());
    }

    internal bool CanStartFolderRestore => !_shutdownStarted && _vm?.Tabs.Count == 1
        && _vm.SelectedTab is { } tab && string.IsNullOrEmpty(tab.FileList.CurrentPath)
        && !tab.FileList.IsSearchMode;

    internal async Task RestoreFolderTabSessionAsync(FolderTabSession session)
    {
        var ordered = await PrepareRestoredFolderTabsAsync(session);
        await LoadRestoredFolderTabsAsync(ordered);
    }

    private bool ShouldRestoreFolderWindows => RestoreFolderTabsOnOpen
        && string.IsNullOrEmpty(InitialNavigationPath ?? _navigationBridge.PendingNavigationPath)
        && !_navigationBridge.PendingQuickAccessFocus;

    private async Task PrepareInitialFolderTabsAsync()
    {
        if (!CanStartFolderRestore) return;
        var tabs = InitialFolderTabSession;
        if (tabs == null && ShouldRestoreFolderWindows)
        {
            var windows = await App.ReadFolderWindowsAsync();
            if (!CanStartFolderRestore || !ShouldRestoreFolderWindows
                || !App.Services.GetRequiredService<ISettingsService>().Get(FolderTabSession.EnabledKey, false)) return;
            if (windows == null) return;
            _initialFolderWindows = windows;
            tabs = windows.Windows[windows.SelectedWindowIndex];
        }
        if (tabs != null) _preparedFolderTabs = await PrepareRestoredFolderTabsAsync(tabs);
    }

    private async Task LoadRestoredFolderTabsAsync(Queue<Guid> ordered)
    {
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
        var restored = new Dictionary<int, ExplorerTabViewModel>();
        var selected = initialTab;
        for (var i = 0; i < session.Paths.Length; i++)
        {
            var path = session.Paths[i];
            var tab = i == 0 ? initialTab
                : await AddTabAsync(select: false, directoryPath: path, loadDirectory: false, inheritGroup: false);
            if (tab == null) continue;
            if (i == 0) await tab.FileList.PrepareRestoredDirectoryViewAsync(path);
            _restoringFolderTabs[tab.Id] = (tab, path);
            tabs.Add(tab);
            restored[i] = tab;
            if (i == session.SelectedIndex) selected = tab;
        }
        if (tabs.Count == 0) return new Queue<Guid>();
        // Select once, before any directory enumeration. Background completion never changes focus.
        model.RestoreGroups(session.Groups, restored);
        model.SelectedTab = selected;
        model.RefreshSplitAccess();
        SchedulePaneLayoutRebuild();
        // The queue retains IDs so a closed pending tab can be collected while other folders are loading.
        return new Queue<Guid>(new[] { selected.Id }.Concat(model.VisiblePanes.Select(tab => tab.Id))
            .Concat(tabs.Select(tab => tab.Id)).Distinct());
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

    internal void ShowTabContextMenu(Control target, ExplorerTabViewModel tab)
    {
        if (_vm == null || !_vm.Tabs.Contains(tab)) return;
        _tabContextMenu?.Close();
        var menu = new ContextMenu();
        var index = _vm.Tabs.IndexOf(tab);
        void Add(string key, Func<Task> action, bool enabled = true, string? icon = null)
        {
            var item = new MenuItem
            {
                Header = LocalizationText.Get(key),
                IsEnabled = enabled && !_closingTabs,
                Icon = CreateTabMenuIcon(icon)
            };
            item.Click += async (_, e) => { e.Handled = true; menu.Close(); await action(); };
            menu.Items.Add(item);
        }
        Add("tabs.new_right", () => OpenTabToRightAsync(tab), icon: Icons.TabAdd);
        menu.Items.Add(new Separator());
        Add("tabs.close_left", () => CloseTabsAroundAsync(tab, TabCloseRange.Left), index > 0);
        Add("tabs.close_right", () => CloseTabsAroundAsync(tab, TabCloseRange.Right), index < _vm.Tabs.Count - 1);
        Add("tabs.close_others", () => CloseTabsAroundAsync(tab, TabCloseRange.Others), _vm.Tabs.Count > 1, Icons.Close);
        menu.Items.Add(new Separator());
        Add("tabs.group.create", () => { _vm.CreateGroup([tab]); return Task.CompletedTask; }, icon: Icons.TabGroup);
        var move = new MenuItem { Header = LocalizationText.Get("tabs.group.move"), IsEnabled = !_closingTabs && _vm.Groups.Any(group => group != tab.Group) };
        foreach (var group in _vm.Groups.Where(group => group != tab.Group).ToArray())
        {
            var item = new MenuItem { Header = group.Name };
            item.Click += (_, e) => { e.Handled = true; menu.Close(); _vm?.MoveTabToGroup(tab, group); };
            move.Items.Add(item);
        }
        menu.Items.Add(move);
        if (tab.Group != null)
            Add("tabs.group.remove", () => { _vm.MoveTabToGroup(tab, null); return Task.CompletedTask; });
        OpenTabMenu(menu, target);
    }

    private void OpenTabMenu(ContextMenu menu, Control target)
    {
        Grid.SetIsSharedSizeScope(menu, true);
        _tabContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_tabContextMenu, menu)) _tabContextMenu = null; };
        menu.Open(target);
    }

    private static PathIcon? CreateTabMenuIcon(string? geometry)
    {
        if (geometry == null) return null;
        const double slotSize = 16;
        var glyphSize = geometry == Icons.Close ? 12 : slotSize;
        return new PathIcon
        {
            Data = Geometry.Parse(geometry),
            Width = glyphSize,
            Height = glyphSize,
            Margin = new Thickness((slotSize - glyphSize) / 2)
        };
    }

    internal void ShowGroupContextMenu(Control target, TabGroupViewModel group)
    {
        if (_vm == null || !_vm.Groups.Contains(group)) return;
        _tabContextMenu?.Close();
        var menu = new ContextMenu();
        void Add(string key, Func<Task> action, string? icon = null)
        {
            var item = new MenuItem
            {
                Header = LocalizationText.Get(key),
                IsEnabled = !_closingTabs,
                Icon = CreateTabMenuIcon(icon)
            };
            item.Click += async (_, e) => { e.Handled = true; menu.Close(); await action(); };
            menu.Items.Add(item);
        }
        Add("tabs.group.rename", async () =>
        {
            var dialog = new Dialogs.TabGroupNameDialog(group.Name);
            using var modal = BlockModalParentInteraction();
            if (await dialog.ShowDialog<string?>(this) is { } name && _vm?.Groups.Contains(group) == true)
                group.Name = name;
        }, Icons.Rename);
        Add("tabs.group.new_tab", async () =>
        {
            if (_vm?.Groups.Contains(group) != true) return;
            var source = group.LastSelectedTab ?? _vm.Members(group).First();
            if (group.IsCollapsed) _vm.ToggleGroup(group);
            await AddTabAsync(directoryPath: source.FileList.CurrentPath, group: group, inheritGroup: false);
        }, Icons.TabAdd);
        menu.Items.Add(new Separator());
        Add("tabs.group.ungroup", () => { _vm?.Ungroup(group); return Task.CompletedTask; });
        Add("tabs.group.close", () => CloseGroupAsync(group), Icons.Close);
        OpenTabMenu(menu, target);
    }

    internal async Task CloseGroupAsync(TabGroupViewModel group)
    {
        if (_vm == null || _closingTabs || !_vm.Groups.Contains(group)) return;
        var targets = _vm.Members(group).ToArray();
        if (targets.Length == _vm.Tabs.Count)
        {
            if (await AddTabAsync(select: false, directoryPath: VirtualPath.Home, inheritGroup: false) == null) return;
        }
        _closingTabs = true;
        try { foreach (var tab in targets) await CloseTabCoreAsync(tab); }
        finally { _closingTabs = false; }
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
