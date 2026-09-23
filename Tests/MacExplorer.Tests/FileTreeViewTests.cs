using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task TreeExpandsOneLevelAtATimeAndCollapseReleasesDescendants()
    {
        const string root = "/tmp/FKFinderTreeTests";
        var files = new FakeFileService(root);
        files.Seed(TreeEntry(root, "parent", folder: true));
        files.Seed(TreeEntry(root + "/parent", "child", folder: true));
        files.Seed(TreeEntry(root + "/parent/child", "leaf.txt"));
        files.Seed(TreeEntry(root, "link", folder: true, symlink: true));
        files.Seed(TreeEntry(root, "bundle.app", folder: true));
        using var vm = CreateViewModel(files);
        vm.SetViewMode(ViewMode.Tree);
        await vm.RefreshAsync();

        Assert.Equal(3, vm.TreeRows.Count);
        Assert.True(vm.TreeRows.Single(row => row.Entry.Name == "parent").CanExpand);
        Assert.False(vm.TreeRows.Single(row => row.Entry.Name == "link").CanExpand);
        Assert.False(vm.TreeRows.Single(row => row.Entry.Name == "bundle.app").CanExpand);

        await vm.ToggleTreeDirectoryAsync(vm.TreeRows.Single(row => row.Entry.Name == "parent").Entry);
        Assert.Equal(3, vm.TreeRows.Count(row => row.Depth == 0));
        Assert.Equal(1, vm.TreeRows.Count(row => row.Depth == 1));
        Assert.DoesNotContain(vm.TreeRows, row => row.Entry.Name == "leaf.txt");
        await vm.ToggleTreeDirectoryAsync(vm.TreeRows.Single(row => row.Entry.Name == "child").Entry);
        Assert.Contains(vm.TreeRows, row => row.Entry.Name == "leaf.txt" && row.Depth == 2);

        vm.SetViewMode(ViewMode.List);
        vm.SetViewMode(ViewMode.Tree);
        await WaitForTreeRowsAsync(vm, 5);
        Assert.True(vm.TreeRows.Single(row => row.Entry.Name == "child").IsExpanded);
        vm.SetDirectoryNotificationsPaused(true);
        vm.SetDirectoryNotificationsPaused(false);
        await WaitForTreeRowsAsync(vm, 5);
        Assert.True(vm.TreeRows.Single(row => row.Entry.Name == "child").IsExpanded);

        vm.SelectEntry(vm.TreeRows.Single(row => row.Entry.Name == "leaf.txt").Entry);
        vm.SetViewMode(ViewMode.List);
        Assert.Empty(vm.SelectedEntries);
        vm.SetViewMode(ViewMode.Tree);
        await WaitForTreeRowsAsync(vm, 5);
        vm.SelectEntry(vm.TreeRows.Single(row => row.Entry.Name == "leaf.txt").Entry);
        vm.CollapseTreeDirectory(vm.TreeRows.Single(row => row.Entry.Name == "parent").Entry);
        Assert.Equal(3, vm.TreeRows.Count);
        Assert.Equal("parent", Assert.Single(vm.SelectedEntries).Name);
        Assert.Equal(3, vm.Entries.Count);
    }

    [AvaloniaFact]
    public async Task TreeRetriesFailedBranchAndKeepsExpansionAcrossViewModeChanges()
    {
        const string root = "/tmp/FKFinderTreeRetryTests";
        var files = new FakeFileService(root);
        files.Seed(TreeEntry(root, "restricted", folder: true, readable: false));
        files.Seed(TreeEntry(root + "/restricted", "ready.txt"));
        var fail = true;
        files.BeforeEnumerate = (path, _) => path == root + "/restricted" && fail
            ? Task.FromException(new UnauthorizedAccessException("denied")) : Task.CompletedTask;
        using var vm = CreateViewModel(files);
        vm.SetViewMode(ViewMode.Tree);
        await vm.RefreshAsync();
        var folder = Assert.Single(vm.TreeRows).Entry;
        Assert.True(Assert.Single(vm.TreeRows).CanExpand);

        await vm.ToggleTreeDirectoryAsync(folder);
        Assert.True(Assert.Single(vm.TreeRows).HasError);
        fail = false;
        await vm.RetryTreeDirectoryAsync(folder);
        Assert.Equal(2, vm.TreeRows.Count);
        Assert.False(vm.TreeRows[0].HasError);
        Assert.DoesNotContain("无法展开文件夹", vm.StatusText);

        vm.SetViewMode(ViewMode.List);
        Assert.Empty(vm.TreeRows);
        vm.SetViewMode(ViewMode.Tree);
        await WaitForTreeRowsAsync(vm, 2);
        Assert.True(vm.TreeRows[0].IsExpanded);
        Assert.Equal("ready.txt", vm.TreeRows[1].Entry.Name);
    }

    [AvaloniaFact]
    public async Task TreeBranchRefreshChangesOnlyItsVisibleChildren()
    {
        const string root = "/tmp/FKFinderTreeRefreshTests";
        var files = new FakeFileService(root);
        files.Seed(TreeEntry(root, "parent", folder: true));
        files.Seed(TreeEntry(root, "sibling.txt"));
        files.Seed(TreeEntry(root + "/parent", "old.txt"));
        using var vm = CreateViewModel(files);
        vm.SetViewMode(ViewMode.Tree);
        await vm.RefreshAsync();
        await vm.ToggleTreeDirectoryAsync(vm.TreeRows.Single(row => row.Entry.Name == "parent").Entry);
        var rootCalls = files.EnumerateDirectoryCallCount;

        files.Seed(TreeEntry(root + "/parent", "new.txt"));
        await vm.RefreshExpandedDirectoryFromNotificationAsync(root + "/parent");
        Assert.Equal(rootCalls + 1, files.EnumerateDirectoryCallCount);
        Assert.Contains(vm.TreeRows, row => row.Entry.Name == "new.txt" && row.Depth == 1);
        Assert.Contains(vm.TreeRows, row => row.Entry.Name == "sibling.txt" && row.Depth == 0);
        Assert.Equal(2, vm.Entries.Count);

        await files.DeleteAsync(root + "/parent");
        await vm.RefreshAsync();
        Assert.DoesNotContain(vm.TreeRows, row => row.Entry.FullPath.StartsWith(root + "/parent", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task TreeNavigationCancelsPendingBranchAndRejectsOldResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "FKFinderTreeNavigationTests-" + Guid.NewGuid().ToString("N"));
        var next = Path.Combine(root, "next");
        Directory.CreateDirectory(next);
        try
        {
            var files = new FakeFileService(root);
            files.Seed(TreeEntry(root, "slow", folder: true));
            files.Seed(TreeEntry(root + "/slow", "stale.txt"));
            files.Seed(TreeEntry(next, "fresh.txt"));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            files.BeforeEnumerate = async (path, token) =>
            {
                if (path != root + "/slow") return;
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            };
            using var vm = CreateViewModel(files);
            vm.SetViewMode(ViewMode.Tree);
            await vm.RefreshAsync();
            var expanding = vm.ToggleTreeDirectoryAsync(Assert.Single(vm.TreeRows).Entry);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await vm.NavigateToAsync(next);
            release.TrySetResult();
            await expanding;

            Assert.Equal(next, vm.CurrentPath);
            Assert.Equal("fresh.txt", Assert.Single(vm.TreeRows).Entry.Name);
            Assert.DoesNotContain(vm.TreeRows, row => row.Entry.Name == "stale.txt");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task TenThousandChildEntriesLoadOffTheUiThreadAndCommitSortedOnce()
    {
        const string root = "/tmp/FKFinderTreeLargeBranchTests";
        var files = new FakeFileService(root);
        files.Seed(TreeEntry(root, "large", folder: true));
        for (var i = 9_999; i >= 0; i--)
            files.Seed(TreeEntry(root + "/large", $"{i:D5}.txt"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeEnumerate = async (path, token) =>
        {
            if (path != root + "/large") return;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var vm = CreateViewModel(files);
        vm.SetViewMode(ViewMode.Tree);
        await vm.RefreshAsync();
        var changes = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FileListViewModel.TreeRows)) changes++;
        };

        var loading = vm.ToggleTreeDirectoryAsync(Assert.Single(vm.TreeRows).Entry);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(loading.IsCompleted);
        Assert.True(Assert.Single(vm.TreeRows).IsLoading);
        var uiRan = false;
        await Dispatcher.UIThread.InvokeAsync(() => uiRan = true);
        Assert.True(uiRan);
        release.TrySetResult();
        await loading.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(10_001, vm.TreeRows.Count);
        Assert.Equal("00000.txt", vm.TreeRows[1].Entry.Name);
        Assert.Equal("09999.txt", vm.TreeRows[^1].Entry.Name);
        Assert.Equal(2, changes);
    }

    [AvaloniaFact]
    public async Task TreeAppliesSortFilterAndRootGroupsWithoutChildHeaders()
    {
        const string root = "/tmp/FKFinderTreeQueryTests";
        var files = new FakeFileService(root);
        files.Seed(TreeEntry(root, "alpha", folder: true));
        files.Seed(TreeEntry(root, "beta.txt"));
        files.Seed(TreeEntry(root + "/alpha", "a.txt"));
        files.Seed(TreeEntry(root + "/alpha", "b.txt"));
        using var vm = CreateViewModel(files);
        vm.SetViewMode(ViewMode.Tree);
        await vm.RefreshAsync();
        await vm.ToggleTreeDirectoryAsync(vm.TreeRows.Single(row => row.Entry.Name == "alpha").Entry);

        vm.SetSort(SortField.Name, ascending: false);
        await WaitForTreeOrderAsync(vm, "b.txt", "a.txt");
        vm.GroupField = GroupField.Type;
        await WaitForTreeGroupsAsync(vm);
        Assert.Equal(2, vm.TreeGroups.Sum(group => group.DirectCount));
        Assert.Equal(vm.TreeRows.Count, vm.TreeGroups.Sum(group => group.VisibleCount));

        vm.FileNameFilter = "a";
        await WaitForTreeRowsAsync(vm, 3);
        Assert.DoesNotContain(vm.TreeRows, row => row.Entry.Name == "b.txt");
        Assert.Contains(vm.TreeRows, row => row.Entry.Name == "a.txt" && row.Depth == 1);
    }

    [AvaloniaFact]
    public async Task DeliveryViewMenuSelectsTreeWithoutGrantingFileOperations()
    {
        using var fixture = new FileDeliveryTests.Fixture();
        using var delivery = new FileDeliveryService(fixture.Settings, fixture.Tags);
        foreach (var entry in delivery.Preferences.Entries.ToArray()) delivery.Remove(entry.Id);
        delivery.AddFolder(fixture.Root);
        using var vm = CreateViewModel(new FakeFileService(fixture.Root), browseOnly: true);
        var window = new FileDeliveryWindow(delivery, fixture.Tags, vm);
        try
        {
            window.Show();
            await window.ResumeAsync();
            var button = window.FindControl<Button>("ViewModeButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = button.ContextMenu!;
            Assert.True(menu.IsOpen);
            Assert.Equal(["图标视图", "列表视图", "树形列表"],
                menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()));
            menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(ViewMode.Tree, vm.ViewMode);
            Assert.True(vm.IsBrowseOnly);
            window.Suspend();
            Assert.False(menu.IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SearchLocationKeepsOrdinaryFirstLevelSelectionInTreeMode()
    {
        const string root = "/tmp/FKFinderTreeSearchFallbackTests";
        var files = new FakeFileService(root);
        var navigation = new NavigationViewModel(files)
        {
            CurrentPath = root, IsHomePage = false, IsSearchMode = true
        };
        using var vm = CreateViewModel(files, navigation: navigation);
        vm.SetViewMode(ViewMode.Tree);
        var folder = TreeEntry(root, "folder", folder: true);
        vm.Entries.Add(folder);

        Assert.False(vm.IsTreeExpansionEnabled);
        Assert.Empty(vm.TreeRows);
        vm.SelectAll();
        Assert.Same(folder, Assert.Single(vm.SelectedEntries));
    }

    private static FileSystemEntry TreeEntry(string parent, string name, bool folder = false,
        bool symlink = false, bool readable = true) => new()
    {
        FullPath = Path.Combine(parent, name), Name = name, IsDirectory = folder,
        IsSymbolicLink = symlink, IsReadable = readable,
        IconKey = folder ? "folder" : "file-generic", Extension = folder ? "" : Path.GetExtension(name)
    };

    private static async Task WaitForTreeRowsAsync(FileListViewModel vm, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (vm.TreeRows.Count != count)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitForTreeOrderAsync(FileListViewModel vm, string first, string second)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var children = vm.TreeRows.Where(row => row.Depth == 1).Select(row => row.Entry.Name).ToArray();
            if (children.SequenceEqual([first, second])) return;
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitForTreeGroupsAsync(FileListViewModel vm)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (vm.TreeGroups.Count == 0 || vm.TreeGroups.Sum(group => group.VisibleCount) != vm.TreeRows.Count)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }
}
