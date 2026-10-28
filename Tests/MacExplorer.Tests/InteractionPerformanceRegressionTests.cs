using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "InteractionPerformance")]
public sealed class InteractionPerformanceRegressionTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task TagMembershipLoadsOnlyWhenTreeIsAttachedAndOncePerSelectedFile()
    {
        var root = Directory.CreateTempSubdirectory("tag-lazy-");
        var store = new CountingStore();
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")), store: store);
        using var vm = CreateViewModel(new FakeFileService(root.FullName), fileTagService: service);
        var entries = new[] { "a.txt", "b.txt" }.Select(name => new FileSystemEntry { Name = name, FullPath = Path.Combine(root.FullName, name) }).ToArray();
        foreach (var entry in entries) await service.GetFileTagsAsync(entry.FullPath);
        Dispatcher.UIThread.RunJobs();
        store.Reads = 0;
        vm.SetSelection(entries);
        var actions = await vm.LoadCompleteFileContextMenuAsync(entries[0]);
        var action = Assert.Single(actions, a => a.TagTreePaths != null);
        Assert.Null(action.SubItems);
        Assert.Equal(0, store.Reads);
        var tree = new TagTreePanel(vm, action.TagTreePaths);
        var window = new Window { Content = tree };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, store.Reads);
            Assert.Single(tree.GetVisualDescendants().OfType<Border>(), b => b.Tag is FileTag { Name: "Project" });
        }
        finally { window.Close(); root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task HiddenTagTabKeepsAliasCurrentAndDefersOneDirectoryRefreshUntilShown()
    {
        var root = Directory.CreateTempSubdirectory("tag-hidden-");
        var query = new CountingQuery();
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")), query);
        using var vm = CreateViewModel(new FakeFileService(root.FullName), fileTagService: service);
        try
        {
            var red = FileTagCatalog.FinderColors[0];
            await vm.NavigateToTagAsync(red); Dispatcher.UIThread.RunJobs();
            query.Calls = 0;
            vm.SetWorkspaceVisible(false);
            await service.RenameTagAsync(red, "First");
            await service.RenameTagAsync(red, "Second");
            await service.RenameTagAsync(red, "Latest");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Latest", vm.CurrentLocationTitle);
            Assert.Equal(red.VirtualPath, vm.CurrentPath);
            Assert.Equal(0, query.Calls);
            vm.SetWorkspaceVisible(true); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, query.Calls);
            vm.SetWorkspaceVisible(true); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, query.Calls);
            await service.RenameTagAsync(red, "A");
            await service.RenameTagAsync(red, "B");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, query.Calls);
            Assert.Equal("B", vm.CurrentLocationTitle);
        }
        finally { root.Delete(true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteMenuRetainsStaticControlsFocusAndUsesLatestActions(bool dark)
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/menu-reuse"));
        var view = new FileListView { DataContext = vm };
        var window = InputAppearanceTests.CreateWindow(view, dark);
        var menu = new ContextMenu();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var fill = typeof(FileListView).GetMethod("FillMenu", flags)!;
        var merge = typeof(FileListView).GetMethod("MergeCompleteMenu", flags)!;
        var executed = "";
        ContextMenuAction Leaf(string label, string result) => new() { Label = label, Execute = () => { executed = result; return Task.CompletedTask; } };
        ContextMenuAction[] Actions(string result, bool complete) =>
        [
            new() { Label = "Copy", IsQuickAction = true, Execute = () => { executed = result; return Task.CompletedTask; } },
            Leaf("Open", result), ContextMenuAction.Separator,
            new() { Label = "Group", SubItems = [Leaf("Child", result)] },
            new() { Label = "Open with", SubItems = complete ? [Leaf("New app", result)] : [Leaf("Loading", result)] },
            new() { Label = "Tags", TagTreePaths = ["/tmp/menu-reuse/a"] }
        ];
        try
        {
            fill.Invoke(view, [menu, Actions("old", false), 0, null]);
            menu.Open(view); Dispatcher.UIThread.RunJobs();
            var quick = (MenuItem)menu.Items[0]!;
            var copy = Assert.Single(quick.GetVisualDescendants().OfType<Button>());
            var open = (MenuItem)menu.Items[2]!;
            var group = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Group"));
            var child = (MenuItem)group.Items[0]!;
            var tags = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Tags"));
            open.Focus();
            merge.Invoke(view, [menu, Actions("new", true), 0]);
            Assert.Same(quick, menu.Items[0]); Assert.Same(open, menu.Items[2]);
            Assert.True(open.IsFocused);
            Assert.Contains(group, menu.Items); Assert.Same(child, group.Items[0]);
            Assert.Contains(tags, menu.Items);
            Assert.Equal("New app", ((MenuItem)menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Open with")).Items[0]!).Header);
            child.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.Equal("new", executed);
            executed = "";
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("new", executed);
        }
        finally { menu.Close(); view.DismissContextMenu(); window.Close(); }
    }

    private sealed class CountingStore : IFileTagStore
    {
        public int Reads;
        public Task<IReadOnlyList<NativeFileTag>> ReadAsync(string path, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult<IReadOnlyList<NativeFileTag>>([new("Project")]); }
        public Task WriteAsync(string path, IReadOnlyList<NativeFileTag> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingQuery : IFinderTagQueryService
    {
        public int Calls;
        public Task<IReadOnlyList<string>> FindFilePathsAsync(FileTag tag, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<string>>([]); }
    }
}
