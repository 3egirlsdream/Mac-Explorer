using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TabCacheReusesAttachedListAndScrollWithoutReadingDirectory(bool fast)
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(fast);
        var first = fixture.Model.SelectedTab!;
        var original = fixture.Workspaces[first];
        var list = original.FileListView.FindControl<FastFileList>("FastList")!;
        Control scroll = fast ? original.FileListView.FindControl<ScrollViewer>("FastListHost")!
            : original.FileListView.FindControl<ListBox>("FileItemsList")!;
        var detached = 0;
        original.DetachedFromVisualTree += (_, _) => detached++;
        first.FileList.SetSelection([first.FileList.Entries[30]]);
        if (fast) list.ScrollToOffset(420);
        Dispatcher.UIThread.RunJobs();
        var offset = list.Offset.Y;
        if (fast) Assert.True(offset > 0);
        var reads = fixture.Files.EnumerateDirectoryCallCount;
        var second = fixture.AddTab();
        fixture.Model.SelectedTab = second;
        Dispatcher.UIThread.RunJobs();
        Assert.False(original.IsVisible);
        Assert.False(((ILivePreviewWorkspace)original).CanActivateLivePreview);
        fixture.Model.SelectedTab = first;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(original, fixture.Workspaces[first]);
        Assert.Same(list, original.FileListView.FindControl<FastFileList>("FastList"));
        Assert.Same(scroll, fast ? original.FileListView.FindControl<ScrollViewer>("FastListHost")
            : (Control?)original.FileListView.FindControl<ListBox>("FileItemsList"));
        Assert.Equal(0, detached);
        Assert.Equal(reads, fixture.Files.EnumerateDirectoryCallCount);
        Assert.Equal(first.FileList.Entries[30].FullPath, Assert.Single(first.FileList.SelectedEntries).FullPath);
        if (fast) Assert.Equal(offset, list.Offset.Y);
    }

    [AvaloniaFact]
    public async Task TabCacheRetainsLoadedThumbnailsAndReleasesThemOnEviction()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(true);
        var first = fixture.Model.SelectedTab!;
        var list = fixture.Workspaces[first].FileListView.FindControl<FastFileList>("FastList")!;
        var images = (FastFileListImages)typeof(FastFileList)
            .GetField("_images", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(list)!;
        images.Clear();
        list.ThumbnailProvider = null;
        // Seed a decoded bitmap: this test concerns workspace ownership, not the platform decoder.
        var keyType = typeof(FastFileListImages).GetNestedType("ImageKey", BindingFlags.NonPublic)!;
        var pixels = typeof(FastFileListImages).GetField("_pixelSize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(images);
        var entry = list.Rows[list.VisibleRange.First];
        var key = keyType.GetMethod("For")!.Invoke(null, [entry, pixels]);
        var bitmap = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")));
        typeof(FastFileListImages).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(images, [key, bitmap]);
        var bytes = images.CachedBytes;
        Assert.True(bytes > 0);
        var other = fixture.AddTab();
        fixture.Model.SelectedTab = other;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(bytes, images.CachedBytes);
        fixture.Model.SelectedTab = first;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(bitmap, images.Get(entry));
        Assert.Equal(bytes, images.CachedBytes);
        for (var i = 0; i < 4; i++) fixture.Model.SelectedTab = fixture.AddTab();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, images.CachedBytes);
        Assert.Equal(0, images.PendingCount);
    }

    [AvaloniaFact]
    public async Task TabCacheLargeDirectorySwitchKeepsRowsAndDoesNotEnumerate()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(true, 10000);
        var first = fixture.Model.SelectedTab!;
        var other = fixture.AddTab();
        var original = fixture.Workspaces[first];
        var reads = fixture.Files.EnumerateDirectoryCallCount;
        var durations = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            fixture.Model.SelectedTab = other;
            Dispatcher.UIThread.RunJobs();
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            fixture.Model.SelectedTab = first;
            Dispatcher.UIThread.RunJobs();
            durations.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Assert.Same(original, fixture.Workspaces[first]);
            Assert.Equal(10000, original.FileListView.FindControl<FastFileList>("FastList")!.Rows.Count);
        }
        Assert.Equal(reads, fixture.Files.EnumerateDirectoryCallCount);
        durations.Sort();
        Console.WriteLine($"Tab cache: 10000 entries, 20 warm switches, headless layout median={durations[10]:F2}ms p95={durations[18]:F2}ms");
    }

    [AvaloniaFact]
    public async Task TabCacheEvictsLeastRecentAndRestoresItsAnchor()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(true);
        var first = fixture.Model.SelectedTab!;
        var original = fixture.Workspaces[first];
        var list = original.FileListView.FindControl<FastFileList>("FastList")!;
        list.ScrollToOffset(420);
        first.FileList.SetSelection([first.FileList.Entries[30]]);
        Dispatcher.UIThread.RunJobs();
        var offset = list.Offset.Y;
        for (var i = 0; i < 4; i++)
        {
            fixture.Model.SelectedTab = fixture.AddTab();
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(4, fixture.Workspaces.Count);
        Assert.False(fixture.Workspaces.ContainsKey(first));
        Assert.True(original.IsDisposed);
        var reads = fixture.Files.EnumerateDirectoryCallCount;
        fixture.Model.SelectedTab = first;
        Dispatcher.UIThread.RunJobs();
        var restored = fixture.Workspaces[first];
        Assert.NotSame(original, restored);
        Assert.Equal(offset, restored.FileListView.FindControl<FastFileList>("FastList")!.Offset.Y);
        Assert.Equal(reads, fixture.Files.EnumerateDirectoryCallCount);
        Assert.Equal(first.FileList.Entries[30].FullPath, Assert.Single(first.FileList.SelectedEntries).FullPath);
    }

    [AvaloniaFact]
    public async Task TabCacheProtectsVisiblePanesAndHonorsRecentActivation()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(true);
        var first = fixture.Model.SelectedTab!;
        var second = fixture.AddTab();
        fixture.Model.SelectedTab = second;
        fixture.Model.SelectedTab = first;
        for (var i = 0; i < 3; i++) fixture.Model.SelectedTab = fixture.AddTab();
        Dispatcher.UIThread.RunJobs();
        Assert.True(fixture.Workspaces.ContainsKey(first));
        Assert.False(fixture.Workspaces.ContainsKey(second));
        fixture.Model.SetPaneLayout(PaneLayout.TwoColumns);
        Dispatcher.UIThread.RunJobs();
        var pinned = fixture.Model.VisiblePanes.First(tab => tab != fixture.Model.SelectedTab);
        var pinnedView = fixture.Workspaces[pinned];
        for (var i = 0; i < 5; i++) fixture.Model.SelectedTab = fixture.AddTab();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5, fixture.Workspaces.Count);
        Assert.Same(pinnedView, fixture.Workspaces[pinned]);
        Assert.True(pinnedView.IsVisible);
        Assert.Equal(2, fixture.Workspaces.Values.Count(view => view.IsVisible));
    }

    [AvaloniaFact]
    public async Task TabCacheShowsTargetBeforeOldPreviewReleaseAndClosesCachedTabs()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(true);
        var first = fixture.Model.SelectedTab!;
        var old = fixture.Workspaces[first];
        var preview = old.FindControl<InfoPanelView>("InfoPanelControl")!;
        var release = new TaskCompletionSource();
        typeof(InfoPanelView).GetField("_previewLoadTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(preview, release.Task);
        try
        {
            fixture.Model.SelectedTab = fixture.AddTab();
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Workspaces[fixture.Model.SelectedTab!].IsVisible);
            Assert.False(release.Task.IsCompleted);
            Assert.False(old.IsVisible);
            var close = (Task)typeof(MainWindow).GetMethod("CloseTabCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Window, [first])!;
            Assert.False(fixture.Model.Tabs.Contains(first));
            Assert.False(close.IsCompleted);
            release.SetResult();
            await close;
            Assert.True(old.IsDisposed);
        }
        finally { release.TrySetResult(); }
    }

    public static IEnumerable<object[]> CachedPaneLayouts()
    {
        foreach (var layout in Enum.GetValues<PaneLayout>().Where(layout => layout != PaneLayout.Single))
        {
            yield return [layout, 0];
            yield return [layout, MainWindowViewModel.GetPaneCount(layout) - 1];
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(CachedPaneLayouts))]
    public async Task TabCacheReturningToSinglePaneFillsWindowAndRestoresSidebar(PaneLayout layout, int activeIndex)
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(true);
        while (fixture.Model.Tabs.Count < MainWindowViewModel.GetPaneCount(layout)) fixture.AddTab();
        var root = fixture.Window.FindControl<Grid>("PaneLayoutRoot")!;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            fixture.Model.SetPaneLayout(layout);
            Dispatcher.UIThread.RunJobs();
            fixture.Model.SelectedTab = fixture.Model.VisiblePanes[activeIndex];
            var tab = fixture.Model.SelectedTab!;
            var workspace = fixture.Workspaces[tab];
            Assert.True(workspace.IsCompact);
            fixture.Model.SetPaneLayout(PaneLayout.Single);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(workspace, fixture.Workspaces[tab]);
            Assert.Single(fixture.Workspaces.Values, view => view.IsVisible);
            Assert.Equal(root.Bounds.Width, workspace.Bounds.Width, 1);
            Assert.Equal(root.Bounds.Height, workspace.Bounds.Height, 1);
            Assert.False(workspace.IsCompact);
            Assert.True(workspace.FindControl<SplitView>("SidebarSplitView")!.IsPaneOpen);
            Assert.False(workspace.FindControl<FinderSidebarView>("SidebarControl")!.IsRailMode);
        }
    }

    private sealed class TabCacheFixture : IAsyncDisposable
    {
        private readonly IServiceProvider _previousServices;
        private readonly ServiceProvider _services;
        public FakeFileService Files { get; }
        public MainWindow Window { get; }
        public MainWindowViewModel Model { get; }
        public Dictionary<ExplorerTabViewModel, ExplorerWorkspaceView> Workspaces =>
            (Dictionary<ExplorerTabViewModel, ExplorerWorkspaceView>)typeof(MainWindow)
                .GetField("_workspaceViews", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;

        private TabCacheFixture(FakeFileService files, FileListViewModel first)
        {
            Files = files;
            _previousServices = App.Services;
            var settings = new StartupSettings();
            _services = new ServiceCollection()
                .AddSingleton<NavigationBridge>()
                .AddSingleton<ISettingsService>(settings)
                .AddSingleton<IBackgroundTaskManager, BackgroundTaskManager>()
                .AddSingleton<IGlobalSearchScopeService, GlobalSearchScopeService>()
                .AddSingleton<IDragDropBridge>(new MacDragDropBridge(files, new DirectoryChangeNotifier()))
                .BuildServiceProvider();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, _services);
            Model = new MainWindowViewModel(first);
            Window = new MainWindow { Width = 1280, Height = 800, DataContext = Model };
            typeof(MainWindow).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, true);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public static async Task<TabCacheFixture> CreateAsync(bool fast, int entries = 200)
        {
            var files = new FakeFileService("/tmp/tab-cache-tests");
            for (var i = 0; i < entries; i++) files.Seed(new FileSystemEntry
                { FullPath = $"{files.HomeDirectory}/{i:D4}.txt", Name = $"{i:D4}.txt" });
            var first = CreateViewModel(files);
            first.UseFastFileList = fast;
            first.SetViewMode(ViewMode.List);
            await first.RefreshAsync();
            return new TabCacheFixture(files, first);
        }

        public ExplorerTabViewModel AddTab()
        {
            var vm = CreateViewModel(Files);
            return Model.AddTab(vm, select: false);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            while (Window.IsVisible)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Yield();
            }
            foreach (var tab in Model.Tabs) tab.FileList.Dispose();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, _previousServices);
            _services.Dispose();
        }
    }
}
