using System.Reflection;
using Avalonia.Headless.XUnit;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "TabStartup")]
public sealed class FolderTabRestoreTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task FirstShowHasAllSavedHeadersAndSelectionBeforeDirectoryEnumeration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        var files = new FakeFileService(root.Path);
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Enumerate(string _, CancellationToken token)
        {
            Assert.True(firstFrame.Task.IsCompletedSuccessfully);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }
        files.BeforeEnumerate = Enumerate;
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(files)));
        fixture.Window.Hide();
        fixture.Model.FileList.GoHome();
        fixture.Model.FileList.PrepareInitialDirectoryView();
        fixture.Files.BeforeEnumerate = Enumerate;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetField("_initialized", flags)!.SetValue(fixture.Window, false);
        typeof(MainWindow).GetField("_initialFrameCommit", flags)!.SetValue(fixture.Window, firstFrame.Task);
        typeof(MainWindow).GetProperty("InitialFolderTabSession", flags)!.SetValue(fixture.Window,
            new FolderTabSession([root.First, root.Second, root.Third], 2));
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[]? shownPaths = null;
        var shownSelection = -1;
        fixture.Window.Opened += (_, _) =>
        {
            shownPaths = fixture.Model.Tabs.Select(t => t.FileList.CurrentPath).ToArray();
            shownSelection = fixture.Model.Tabs.IndexOf(fixture.Model.SelectedTab!);
            shown.TrySetResult();
        };
        try
        {
            fixture.Window.ShowAndActivate();
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(new[] { root.First, root.Second, root.Third }, shownPaths);
            Assert.Equal(2, shownSelection);
            Assert.False(entered.Task.IsCompleted);
            firstFrame.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            firstFrame.TrySetResult();
            release.TrySetResult();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (fixture.Window.IsRestoringFolderTabs) await Task.Delay(10, timeout.Token);
        Assert.Same(fixture.Model.Tabs[2], fixture.Model.SelectedTab);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SelectsSavedTabBeforeLoadingAndKeepsUserSelectionDuringBackgroundLoads()
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        var files = new FakeFileService(root.Path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeEnumerate = async (path, token) =>
        {
            if (path != root.Second) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(files)));
        var restoring = fixture.Window.RestoreFolderTabSessionAsync(new([root.First, root.Second, root.First], 1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(3, fixture.Model.Tabs.Count);
            Assert.Same(fixture.Model.Tabs[1], fixture.Model.SelectedTab);
            Assert.Equal(new[] { root.First, root.Second, root.First }, fixture.Model.Tabs.Select(t => t.FileList.CurrentPath));
            fixture.Model.SelectedTab = fixture.Model.Tabs[2];
        }
        finally { release.TrySetResult(); }
        await restoring;
        Assert.Same(fixture.Model.Tabs[2], fixture.Model.SelectedTab);
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ClosingSelectedLoadingOrQueuedTabReleasesItWithoutStoppingOtherTabs(int closeIndex)
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        var files = new FakeFileService(root.Path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeEnumerate = async (path, token) =>
        {
            if (path != root.Second) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(files)));
        // Both loading slots are held, leaving the last tab queued.
        fixture.Files.BeforeEnumerate = async (_, token) => await release.Task.WaitAsync(token);
        var restoring = fixture.Window.RestoreFolderTabSessionAsync(new([root.First, root.Second, root.Third], 0));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var closed = fixture.Model.Tabs[closeIndex];
            await (Task)typeof(MainWindow).GetMethod("CloseTabCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Window, [closed])!;
            Assert.DoesNotContain(closed, fixture.Model.Tabs);
            Assert.True((bool)typeof(FileListViewModel).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(closed.FileList)!);
        }
        finally { release.TrySetResult(); }
        await restoring;
        Assert.Equal(2, fixture.Model.Tabs.Count);
        Assert.Same(fixture.Model.Tabs[0], fixture.Model.SelectedTab);
        Assert.All(fixture.Model.Tabs, tab => Assert.False(tab.FileList.IsLoading));
        Assert.Equal(closeIndex == 2 ? root.Second : root.Third, fixture.Model.Tabs[1].FileList.CurrentPath);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SearchingDuringRestoreKeepsSearchSelectedWhileOtherTabsLoad()
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(new FakeFileService(root.Path))));
        fixture.Files.BeforeEnumerate = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var restoring = fixture.Window.RestoreFolderTabSessionAsync(new([root.First, root.Second], 0));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await fixture.Model.FileList.SearchAsync("user search");
        }
        finally { release.TrySetResult(); }
        await restoring;
        Assert.Equal(2, fixture.Model.Tabs.Count);
        Assert.Same(fixture.Model.Tabs[0], fixture.Model.SelectedTab);
        Assert.True(fixture.Model.FileList.IsSearchMode);
        Assert.Equal("user search", fixture.Model.FileList.SearchQuery);
    }

    private sealed class Directories : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "folder-restore-" + Guid.NewGuid().ToString("N"));
        public string First => System.IO.Path.Combine(Path, "first");
        public string Second => System.IO.Path.Combine(Path, "second");
        public string Third => System.IO.Path.Combine(Path, "third");
        public Directories() { Directory.CreateDirectory(First); Directory.CreateDirectory(Second); Directory.CreateDirectory(Third); }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
