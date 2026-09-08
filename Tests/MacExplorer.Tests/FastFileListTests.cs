using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FastFileListTests
{
    [AvaloniaFact]
    public void LoadingDrawsViewportSkeletonWithoutTextOrStaleHitTargets()
    {
        var list = new FastFileList { IsLoading = true };
        list.SetRows(Enumerable.Range(0, 1000).Select(Entry).ToArray());
        list.Measure(new Size(900, 315));
        list.Arrange(new Rect(0, 0, 900, 315));
        list.Offset = new Vector(0, 15_000);
        using (var drawing = new DrawingGroup().Open()) list.Render(drawing);
        Assert.Equal(11, list.LastRenderedSkeletonRowCount);
        Assert.Equal(0, list.LastRenderedRowCount);
        Assert.Equal(0, list.CachedTextCount);
        Assert.Null(list.EntryAt(new Point(50, 15)));
        Assert.Empty(list.RowsWithCentersIn(15_000, 15_300));
        Assert.Empty(list.GetVisualChildren());

        list.IsLoading = false;
        using (var drawing = new DrawingGroup().Open()) list.Render(drawing);
        Assert.Equal(0, list.LastRenderedSkeletonRowCount);
        Assert.Equal(11, list.LastRenderedRowCount);
        Assert.Same(list.Rows[500], list.EntryAt(new Point(50, 15)));
    }

    internal static FileSystemEntry Entry(int i) => new()
    {
        Name = $"文件-{i:D6}.txt", FullPath = $"/tmp/FastListTests/文件-{i:D6}.txt",
        Extension = ".txt", LastModified = new DateTime(2026, 9, 8), Size = i
    };

    [AvaloniaFact]
    public void HundredThousandRowsJumpAndResizeKeepViewportFilledAndCachesBounded()
    {
        using var theme = new FastListTestTheme();
        var list = new FastFileList();
        var host = new ScrollViewer { Content = list };
        var window = new Window { Width = 900, Height = 610, Content = host };
        try
        {
            list.SetRows(Enumerable.Range(0, 100_000).Select(Entry).ToArray());
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var presenter = host.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().FirstOrDefault();
            Assert.True(host.Extent.Height == 3_000_000,
                $"host extent={host.Extent}, viewport={host.Viewport}; list={list.Extent}, bounds={list.Bounds}; presenter={presenter?.Extent}, child={presenter?.Child?.GetType()}, owner={presenter?.FindAncestorOfType<ScrollViewer>() == host}");
            Assert.True(list.Viewport.Height > 0);
            using var target = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 610));
            foreach (var index in Enumerable.Range(0, 80).Select(i => i * 1249).Append(99_999))
            {
                list.ScrollToEntry(list.Rows[index]);
                Dispatcher.UIThread.RunJobs();
                target.Render(list);
                Assert.Equal(host.Offset.Y, list.Offset.Y);
                Assert.InRange(list.LastRenderedRowCount, 1, (int)Math.Ceiling(list.Viewport.Height / 30) + 1);
                Assert.NotNull(list.EntryAt(new Point(50, list.Viewport.Height - 1)));
                Assert.InRange(list.ObservedRowCount, 1, list.LastRenderedRowCount);
                Assert.InRange(list.CachedTextCount, 1, 1024);
            }
            Assert.Empty(list.GetVisualChildren());
            window.Height = 800;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(host.Extent.Height - host.Viewport.Height, list.Offset.Y);
            list.SetRows([Entry(1), Entry(2)]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, host.Offset.Y);
            Assert.Equal(60, host.Extent.Height);
            Assert.Equal(1, list.RowIndexAt(new Point(20, 59)));
            Assert.Equal(-1, list.RowIndexAt(new Point(20, 60)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReorderKeepsTextByPathAndAppearanceChangesInvalidateCache()
    {
        var list = new FastFileList();
        list.Measure(new Size(900, 300));
        list.Arrange(new Rect(0, 0, 900, 300));
        var entries = Enumerable.Range(0, 10).Select(Entry).ToArray();
        list.SetRows(entries);
        using (var drawing = new DrawingGroup().Open()) list.Render(drawing);
        Assert.Equal(10, list.CachedTextCount);
        list.SetRows(entries.Reverse().ToArray());
        Assert.Same(entries[9], list.EntryAt(new Point(15, 15)));
        Assert.Equal(9, list.IndexOf(entries[0]));
        Assert.Null(list.EntryAt(new Point(880, 15), contentOnly: true));
        Assert.Equal(10, list.CachedTextCount);
        list.Foreground = Brushes.Blue;
        Assert.Equal(0, list.CachedTextCount);
        using (var drawing = new DrawingGroup().Open()) list.Render(drawing);
        list.FontSize = 18;
        Assert.Equal(0, list.CachedTextCount);
        Assert.Equal(entries.Reverse().Take(2), list.RowsWithCentersIn(15, 45));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScrollingReusesShapedGlyphsAndThemeChangesReplaceThem(bool grid)
    {
        var list = new FastFileList { IsGrid = grid };
        list.SetRows(Enumerable.Range(0, 50).Select(Entry).ToArray());
        list.Measure(new Size(900, 280));
        list.Arrange(new Rect(0, 0, 900, 280));
        var first = RenderGlyphs();
        Assert.NotEmpty(first);
        var range = list.VisibleRange;

        list.Offset = new Vector(0, 1);
        Assert.Equal(range, list.VisibleRange);
        var scrolled = RenderGlyphs();
        Assert.Equal(first.Length, scrolled.Length);
        for (var i = 0; i < first.Length; i++) Assert.Same(first[i], scrolled[i]);

        list.Foreground = Brushes.Blue;
        var themed = RenderGlyphs();
        Assert.Equal(first.Length, themed.Length);
        for (var i = 0; i < first.Length; i++) Assert.NotSame(first[i], themed[i]);

        GlyphRun[] RenderGlyphs()
        {
            var drawing = new DrawingGroup();
            using (var context = drawing.Open()) list.Render(context);
            return Glyphs(drawing).ToArray();
        }

        static IEnumerable<GlyphRun> Glyphs(Drawing drawing)
        {
            if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun!;
            if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                foreach (var run in Glyphs(child)) yield return run;
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HidingRowsOrShowingSkeletonCancelsThumbnailRequests(bool loading)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = new FastFileList
        {
            ThumbnailProvider = async (_, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); }
                return null;
            }
        };
        list.SetRows([Entry(0)]);
        var parent = new Grid { Children = { list } };
        var window = new Window { Width = 900, Height = 300, Content = parent };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, list.ObservedRowCount);
            if (loading) list.IsLoading = true;
            else parent.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, list.ObservedRowCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LateThumbnailCannotFillReplacementWithSamePathAndNewMetadata()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult = new TaskCompletionSource<ThumbnailResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResult = new TaskCompletionSource<ThumbnailResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var images = new FastFileListImages
        {
            ThumbnailProvider = (entry, _, _) =>
            {
                if (entry.Size == 0) { firstStarted.TrySetResult(); return oldResult.Task; }
                return secondResult.Task;
            }
        };
        var old = Entry(0);
        var replacement = new FileSystemEntry { FullPath = old.FullPath, Name = old.Name, Size = 10 };
        try
        {
            images.UpdateVisible([old], 64);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            images.UpdateVisible([replacement], 64);
            oldResult.SetResult(new ThumbnailResult(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), "old"));
            await Task.Delay(200);
            Assert.Equal(0, images.CachedBytes);
            Assert.Equal(1, images.PendingCount);
        }
        finally { images.Clear(); secondResult.TrySetResult(null); }
    }
}

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task SlowBreadcrumbLocalizationDoesNotBlockNavigationOrOverwriteNewPath()
    {
        var names = new DelayedDisplayNames();
        var navigation = new NavigationViewModel(new FakeFileService("/tmp"), displayNameService: names);
        var localized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationViewModel.Breadcrumbs)
                && navigation.Breadcrumbs.LastOrDefault()?.DisplayName == "名称-second")
                localized.TrySetResult();
        };
        try
        {
            await navigation.NavigateToAsync("/tmp/first");
            await names.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("first", navigation.Breadcrumbs[^1].DisplayName);
            Assert.False(names.CalledOnUiThread);
            await navigation.NavigateToAsync("/tmp/second");
            Assert.Equal("/tmp/second", navigation.CurrentPath);
            Assert.Equal("second", navigation.Breadcrumbs[^1].DisplayName);
            names.Release.Set();
            await localized.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("/tmp/second", navigation.Breadcrumbs[^1].FullPath);
            Assert.Equal("名称-second", navigation.Breadcrumbs[^1].DisplayName);
        }
        finally { names.Release.Set(); }
    }

    private sealed class DelayedDisplayNames : IDisplayNameService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool CalledOnUiThread { get; private set; }
        public string GetUserName() => "Test";
        public string GetDisplayName(string path)
        {
            CalledOnUiThread |= Dispatcher.UIThread.CheckAccess();
            Started.TrySetResult();
            if (!Dispatcher.UIThread.CheckAccess()) Release.Wait(TimeSpan.FromSeconds(5));
            return "名称-" + Path.GetFileName(path);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void RowWhitespaceSelectionNeverTemporarilyDisablesToolbar(bool fast)
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/FastListTests"));
        vm.UseFastFileList = fast;
        vm.Entries = new ObservableCollection<FileSystemEntry>(Enumerable.Range(0, 10).Select(FastFileListTests.Entry));
        var view = new FileListView { DataContext = vm };
        var toolbar = new FinderToolbar { DataContext = vm };
        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        layout.Children.Add(view);
        var window = new Window { Width = 900, Height = 600, Content = layout };
        try
        {
            window.Show();
            vm.SelectEntry(vm.Entries[0]);
            Dispatcher.UIThread.RunJobs();
            var buttons = new[] { "CutButton", "CopyButton", "DeleteButton", "CutOverflowButton", "CopyOverflowButton", "DeleteOverflowButton" }
                .Select(name => toolbar.FindControl<Button>(name)!).ToArray();
            Assert.All(buttons, button => Assert.True(button.IsEnabled));
            var becameDisabled = false;
            foreach (var button in buttons)
                button.PropertyChanged += (_, e) => becameDisabled |= e.Property == InputElement.IsEnabledProperty && !button.IsEnabled;
            Control rows = fast ? view.FindControl<FastFileList>("FastList")! : view.FindControl<ListBox>("FileItemsList")!;
            var origin = rows.TranslatePoint(default, window)!.Value;
            var whitespace = origin + new Vector(rows.Bounds.Width - 30, 45);
            window.MouseDown(whitespace, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.All(buttons, button => Assert.True(button.IsEnabled));
            window.MouseUp(whitespace, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(vm.Entries[1], Assert.Single(vm.SelectedEntries));
            Assert.False(becameDisabled);
            Assert.All(buttons, button => Assert.True(button.IsEnabled));

            vm.ClearSelection();
            Dispatcher.UIThread.RunJobs();
            Assert.All(buttons, button => Assert.False(button.IsEnabled));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelledNavigationCannotDismissNewSkeletonAndMissingDirectoryShowsError()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("fkfinder-skeleton-");
        var first = Directory.CreateDirectory(Path.Combine(root.FullName, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root.FullName, "second")).FullName;
        var startedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new FakeFileService(root.FullName)
        {
            BeforeEnumerate = async (path, token) =>
            {
                if (path == first)
                {
                    startedFirst.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                else
                {
                    startedSecond.TrySetResult();
                    await releaseSecond.Task.WaitAsync(token);
                }
            }
        };
        files.Seed(new FileSystemEntry { Name = "ready.txt", FullPath = Path.Combine(second, "ready.txt") });
        using var vm = CreateViewModel(files);
        vm.UseFastFileList = true;
        vm.Entries.Add(FastFileListTests.Entry(0));
        var view = new FileListView { DataContext = vm };
        var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            var firstLoad = vm.NavigateToAsync(first);
            await startedFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondLoad = vm.NavigateToAsync(second);
            await startedSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstLoad.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(second, vm.CurrentPath);
            Assert.True(vm.IsDirectoryLoading);
            Assert.True(list.IsLoading);
            Assert.False(view.FindControl<Grid>("FileScroll")!.IsHitTestVisible);
            list.Focus();
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Empty(vm.SelectedEntries);
            releaseSecond.TrySetResult();
            await secondLoad.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(list.IsLoading);
            Assert.Same(vm.Entries, list.Rows);
            Assert.Equal("ready.txt", Assert.Single(vm.Entries).Name);

            await vm.NavigateToAsync(Path.Combine(root.FullName, "missing"));
            Assert.False(vm.IsDirectoryLoading);
            Assert.False(vm.IsLoading);
            Assert.Empty(vm.Entries);
            Assert.Contains("路径不存在", vm.ReadErrorMessage);
            Assert.True(view.FindControl<StackPanel>("EmptyState")!.IsVisible);
            await vm.RefreshAsync();
            Assert.Contains("路径不存在", vm.ReadErrorMessage);
            Assert.False(vm.IsDirectoryLoading);
        }
        finally { window.Close(); root.Delete(true); }
    }

    [AvaloniaFact]
    public void FastListFullSelectionIsOneNotificationAndShiftAnchorSurvivesReverseRange()
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/FastListTests"));
        vm.Entries = new ObservableCollection<FileSystemEntry>(Enumerable.Range(0, 100_000).Select(FastFileListTests.Entry));
        var changes = 0;
        vm.SelectedEntries.CollectionChanged += (_, _) => changes++;
        vm.SelectAll();
        Assert.Equal(1, changes);
        Assert.Equal(100_000, vm.SelectedEntries.Count);
        Assert.All(vm.Entries, entry => Assert.True(vm.IsEntrySelected(entry) && entry.IsSelected));
        vm.SelectEntry(vm.Entries[8]);
        vm.SelectEntry(vm.Entries[5], shiftKey: true);
        vm.SelectEntry(vm.Entries[6], shiftKey: true);
        Assert.Equal(vm.Entries.Skip(6).Take(3), vm.SelectedEntries);
        Assert.Equal(vm.Entries[8].FullPath, vm.SelectionAnchorPath);
        vm.ClearSelection();
        Assert.All(vm.Entries, entry => Assert.False(vm.IsEntrySelected(entry) || entry.IsSelected));
    }

    [AvaloniaFact]
    public void FastListBindsInitialReplacementMutationAndViewModesAndCanBeDisabled()
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/FastListTests"));
        Assert.True(vm.UseFastFileList);
        vm.Entries = new ObservableCollection<FileSystemEntry>(Enumerable.Range(0, 100).Select(FastFileListTests.Entry));
        var view = new FileListView { DataContext = vm };
        var list = view.FindControl<FastFileList>("FastList")!;
        var legacy = view.FindControl<ListBox>("FileItemsList")!;
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(list.IsEffectivelyVisible);
            Assert.Null(legacy.ItemsSource);
            Assert.Equal(100, list.Rows.Count);
            vm.Entries = new ObservableCollection<FileSystemEntry>(vm.Entries.Reverse());
            Assert.Equal(99, list.Rows[0].Size);
            vm.Entries.RemoveAt(0);
            Assert.Equal(98, list.EntryAt(new Point(20, 15))!.Size);
            Assert.Equal(0, list.IndexOf(list.Rows[0]));
            vm.SetViewMode(ViewMode.Grid);
            Assert.True(list.IsEffectivelyVisible);
            Assert.True(list.IsGrid);
            Assert.Equal(99, list.Rows.Count);
            vm.SetViewMode(ViewMode.List);
            vm.UseFastFileList = false;
            Assert.True(legacy.IsVisible);
            Assert.Same(vm.Entries, legacy.ItemsSource);
            vm.UseFastFileList = true;
            vm.GroupField = GroupField.Type;
            Assert.True(list.IsEffectivelyVisible);
            Assert.False(view.FindControl<ListBox>("GroupedListItems")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FastListWhitespaceMarqueeKeyboardAndRenameKeepExistingGestures()
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/FastListTests"));
        vm.UseFastFileList = true;
        vm.Entries = new ObservableCollection<FileSystemEntry>(Enumerable.Range(0, 100).Select(FastFileListTests.Entry));
        var view = new FileListView { DataContext = vm };
        var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var origin = list.TranslatePoint(default, window)!.Value;
            var start = origin + new Vector(list.Bounds.Width - 25, 5);
            var end = origin + new Vector(list.Bounds.Width - 50, 89);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(vm.Entries.Take(3), vm.SelectedEntries.OrderBy(entry => entry.Size));
            var rightClick = origin + new Vector(list.Bounds.Width - 25, 45);
            window.MouseDown(rightClick, MouseButton.Right);
            Assert.Equal(3, vm.SelectedEntries.Count);
            window.MouseUp(rightClick, MouseButton.Right);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            list.Focus();
            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(vm.Entries[99], Assert.Single(vm.SelectedEntries));
            Assert.Equal(99, list.RowIndexAt(new Point(20, list.Bounds.Height - 1)));
            window.KeyPress(Key.Up, RawInputModifiers.Shift, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, vm.SelectedEntries.Count);
            window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            var overlay = view.FindControl<Canvas>("FastRenameOverlay")!;
            var editor = Assert.IsType<TextBox>(Assert.Single(overlay.Children));
            Assert.Equal(vm.Entries[0].Name, editor.Text);
            Assert.Equal(vm.Entries[0].FullPath, list.EditingPath);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(overlay.Children);
            Assert.Null(list.EditingPath);
            list.ScrollToOffset(0);
            var whitespace = origin + new Vector(list.Bounds.Width - 25, 5 * 30 + 15);
            window.MouseDown(whitespace, MouseButton.Left);
            window.MouseUp(whitespace, MouseButton.Left);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Same(vm.Entries[6], Assert.Single(vm.SelectedEntries));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FastListRefreshShortcutPreservesTwoSelectedPathsAndViewport()
    {
        using var theme = new FastListTestTheme();
        var files = new FakeFileService("/tmp/FastListTests");
        foreach (var entry in Enumerable.Range(0, 1000).Select(FastFileListTests.Entry)) files.Seed(entry);
        using var vm = CreateViewModel(files);
        vm.UseFastFileList = true;
        await vm.RefreshAsync();
        var view = new FileListView { DataContext = vm };
        var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            vm.SetSelection(vm.Entries.TakeLast(2), vm.Entries[^1]);
            list.ScrollToEntry(vm.Entries[^1]);
            Dispatcher.UIThread.RunJobs();
            var paths = vm.SelectedEntries.Select(e => e.FullPath).ToArray();
            var offset = list.Offset;
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.SnapshotApplied += () => applied.TrySetResult();
            list.Focus();
            window.KeyPress(Key.R, RawInputModifiers.Meta, PhysicalKey.R, "r");
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(paths, vm.SelectedEntries.Select(e => e.FullPath));
            Assert.All(vm.SelectedEntries, e => Assert.True(e.IsSelected && vm.IsEntrySelected(e)));
            Assert.Equal(offset, list.Offset);
            Assert.Same(vm.Entries, list.Rows);
        }
        finally { window.Close(); }
    }
}

internal sealed class FastListTestTheme : IDisposable
{
    private readonly FluentTheme _theme = new();
    public FastListTestTheme() => Application.Current!.Styles.Insert(0, _theme);
    public void Dispose() => Application.Current!.Styles.Remove(_theme);
}
