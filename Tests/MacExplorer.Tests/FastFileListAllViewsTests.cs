using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData("remote")]
    [InlineData("archive")]
    [InlineData("trash")]
    [InlineData("ai")]
    [InlineData("tag")]
    public void SpecialLocationsUseFastDetailsGroupsAndGridAndPreserveContextSelection(string source)
    {
        using var theme = new FastListTestTheme();
        var files = new FakeFileService("/tmp/FastAllViews");
        var navigation = new NavigationViewModel(files)
        {
            IsHomePage = false,
            IsRemoteView = source == "remote", IsArchiveView = source == "archive",
            IsAiView = source == "ai",
            CurrentPath = source switch
            {
                "remote" => VirtualPath.BuildRemotePath("test", "/files"),
                "archive" => ArchivePathHelper.Build("/tmp/demo.zip", ""),
                "trash" => files.TrashDirectory,
                "ai" => AiPathHelper.GetTopLevelPath(AiViewMode.People),
                "tag" => TagPathHelper.Build("Test", FileTagKind.Custom),
                _ => ""
            }
        };
        var sort = new SortFilterViewModel();
        sort.SetRawEntries(Enumerable.Range(0, 50).Select(i => new FileSystemEntry
        {
            Name = $"file-{i:D4}.txt", Extension = ".txt", Size = i,
            FullPath = source switch
            {
                "remote" => VirtualPath.BuildRemotePath("test", $"/files/file-{i:D4}.txt"),
                "archive" => ArchivePathHelper.Build("/tmp/demo.zip", $"file-{i:D4}.txt"),
                "trash" => Path.Combine(files.TrashDirectory, $"file-{i:D4}.txt"),
                _ => $"/tmp/FastAllViews/file-{i:D4}.txt"
            }
        }).ToArray());
        using var vm = CreateViewModel(files, navigation: navigation, sortFilter: sort);
        vm.UseFastFileList = true;
        vm.SetSort(SortField.Size);
        var view = new FileListView { DataContext = vm };
        var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            foreach (var grid in new[] { false, true })
            foreach (var grouped in new[] { false, true })
            {
                vm.SetViewMode(grid ? ViewMode.Grid : ViewMode.List);
                vm.GroupField = grouped ? GroupField.Type : GroupField.None;
                Dispatcher.UIThread.RunJobs();
                Assert.True(list.IsEffectivelyVisible);
                Assert.Equal(50, list.Rows.Count);
                Assert.Equal(grid, list.IsGrid);
                Assert.False(view.FindControl<ListBox>("GroupedListItems")!.IsVisible);
                Assert.False(view.FindControl<ListBox>("GridViewItems")!.IsVisible);
                Assert.Null(view.FindControl<ListBox>("FileItemsList")!.ItemsSource);
                vm.SetSelection(list.Rows.Take(2));
                var origin = list.TranslatePoint(default, window)!.Value;
                var rightClick = origin + (Vector)list.RowBounds(1).Center;
                window.MouseDown(rightClick, MouseButton.Right);
                Assert.Equal(2, vm.SelectedEntries.Count);
                window.MouseUp(rightClick, MouseButton.Right);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            }
            vm.UseFastFileList = false;
            Assert.False(list.IsEffectivelyVisible);
            Assert.True(view.FindControl<ListBox>("GridViewItems")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupedRefreshKeepsVisibleFileAndSelectionAndKeyboardSkipsHeaders(bool grid)
    {
        using var theme = new FastListTestTheme();
        var files = new FakeFileService("/tmp/FastListTests");
        foreach (var entry in Enumerable.Range(0, 200).Select(FastFileListTests.Entry)) files.Seed(entry);
        using var vm = CreateViewModel(files);
        vm.UseFastFileList = true;
        vm.GroupField = GroupField.Type;
        vm.SetViewMode(grid ? ViewMode.Grid : ViewMode.List);
        await vm.RefreshAsync();
        var view = new FileListView { DataContext = vm };
        var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            vm.SelectEntry(list.Rows[95]);
            list.ScrollToEntry(list.Rows[95], 0);
            Dispatcher.UIThread.RunJobs();
            var anchor = list.Rows[list.VisibleRange.First];
            var y = list.RowBounds(list.IndexOf(anchor)).Y;
            var selected = Assert.Single(vm.SelectedEntries).FullPath;
            files.Seed(new FileSystemEntry { FullPath = "/tmp/FastListTests/000-first.txt", Name = "000-first.txt", Extension = ".txt" });
            await vm.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(selected, Assert.Single(vm.SelectedEntries).FullPath);
            Assert.Equal(y, list.RowBounds(list.IndexOfPath(anchor.FullPath)).Y);
            Assert.False(list.IsLoading);
            list.Focus();
            window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Same(list.Rows[grid ? list.GridColumns : 1], Assert.Single(vm.SelectedEntries));
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            var overlay = view.FindControl<Canvas>("FastRenameOverlay")!;
            var editor = Assert.IsType<TextBox>(Assert.Single(overlay.Children));
            var index = list.IndexOf(Assert.Single(vm.SelectedEntries));
            if (grid)
            {
                Assert.Equal(100, editor.Width);
                Assert.True(Canvas.GetTop(editor) + editor.Height <= list.RowBounds(index).Bottom);
            }
            else Assert.InRange(editor.Width, 60, list.Bounds.Width);
            Assert.True(Canvas.GetTop(editor) >= list.RowBounds(index).Y);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Empty(overlay.Children);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task ArchiveNavigationShowsSkeletonImmediatelyAndDiscardsResultsAfterLeaving()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("FastArchive-");
        var archive = new DelayedArchiveService();
        var files = new FakeFileService(root.FullName);
        files.Seed(new FileSystemEntry { FullPath = Path.Combine(root.FullName, "local.txt"), Name = "local.txt" });
        using var vm = CreateViewModel(files, archiveService: archive);
        vm.UseFastFileList = true;
        var view = new FileListView { DataContext = vm };
        var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            await vm.RefreshAsync();
            var path = ArchivePathHelper.Build(Path.Combine(root.FullName, "slow.zip"), "");
            var pending = vm.NavigateToAsync(path);
            Assert.Equal(path, vm.CurrentPath);
            Assert.True(list.IsLoading);
            await archive.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.NavigateToAsync(root.FullName);
            Assert.False(list.IsLoading);
            archive.Release.SetResult([new FileSystemEntry { FullPath = path + "/old.txt", Name = "old.txt" }]);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(root.FullName, vm.CurrentPath);
            Assert.Equal("local.txt", Assert.Single(list.Rows).Name);
            Assert.False(vm.IsArchiveView);
            Assert.False(vm.IsDirectoryLoading);
        }
        finally { archive.Release.TrySetResult([]); window.Close(); root.Delete(true); }
    }

    private sealed class DelayedArchiveService : IArchiveService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<FileSystemEntry>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsArchiveFile(string filePath) => filePath.EndsWith(".zip", StringComparison.Ordinal);
        public bool IsEncrypted(string archivePath)
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            return false;
        }
        public Task<IReadOnlyList<FileSystemEntry>> GetArchiveContentsAsync(string archivePath, string internalPath = "", string? password = null)
        {
            Started.TrySetResult();
            return Release.Task;
        }
        public Task ExtractAsync(string archivePath, string destinationPath, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default, string? password = null) => throw new NotSupportedException();
        public Task<string> ExtractEntryToTempAsync(string archivePath, string entryKey, string? password = null) => throw new NotSupportedException();
        public Task<string> CompressAsync(CompressOptions options, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

}
