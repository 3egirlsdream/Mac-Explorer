using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Search;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Search")]
public sealed class SearchSelectionRefreshViewModelTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(ViewMode.List)]
    [InlineData(ViewMode.Tree)]
    public async Task IndexRefreshKeepsSearchResultsClickableAndPreservesSelection(ViewMode mode)
    {
        var files = new FakeFileService("/private/tmp/fk-search-selection");
        var search = new RefreshingSearch();
        using var vm = CreateViewModel(files, sortFilter: new SortFilterViewModel { ViewMode = mode }, searchService: search);
        using var tab = new ExplorerTabViewModel(vm) { IsActive = true };
        using var workspace = new ExplorerWorkspaceView { DataContext = tab };
        var view = workspace.FindControl<FileListView>("FileListControl")!;
        var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 1280, Height = 800, Content = workspace };
        window.Styles.Add(new FluentTheme());
        window.Show();
        var applied = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var snapshotIndex = 0;
        vm.SnapshotApplied += () => applied[snapshotIndex++].TrySetResult();
        var running = vm.SearchAsync("result");
        FileSystemEntry Entry(int index) => new()
        {
            Name = $"result-{index}.txt", FullPath = $"{files.HomeDirectory}/nested/result-{index}.txt",
            Extension = ".txt", IconKey = "file-text"
        };
        async Task Publish(int index, params FileSystemEntry[] entries)
        {
            search.Snapshots.Writer.TryWrite(new SearchSnapshot(entries,
                new SearchIndexStatus(files.HomeDirectory, SearchIndexPhase.Building, index + 1), false));
            await applied[index].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            await Publish(0, Entry(0), Entry(1));
            Assert.True(vm.IsLoading);
            Assert.False(list.IsLoading);
            var origin = list.TranslatePoint(default, window)!.Value;
            for (var index = 0; index < 2; index++)
            {
                var point = new Point(origin.X + 100, origin.Y + list.RowBounds(index).Center.Y);
                var modifiers = RawInputModifiers.LeftMouseButton | (index == 1 ? RawInputModifiers.Meta : RawInputModifiers.None);
                window.MouseDown(point, MouseButton.Left, modifiers);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(index + 1, vm.SelectedEntries.Count);
            }
            var refreshed = new[] { Entry(0), Entry(1) };
            await Publish(1, refreshed);
            Assert.Equal(refreshed, vm.SelectedEntries);
            Assert.All(refreshed, entry => Assert.True(entry.IsSelected));
            await Publish(2, refreshed[1]);
            Assert.Same(refreshed[1], Assert.Single(vm.SelectedEntries));
            Assert.False(refreshed[0].IsSelected);
        }
        finally
        {
            search.Snapshots.Writer.TryComplete();
            await running;
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(vm.IsLoading);
    }

}

public abstract partial class FileListViewModelTestBase
{
    protected sealed class RefreshingSearch : ISearchService, ISearchSessionService
    {
        public Channel<SearchSnapshot> Snapshots { get; } = Channel.CreateUnbounded<SearchSnapshot>();
        public async IAsyncEnumerable<SearchSnapshot> SearchSnapshotsAsync(string directory, string input, int limit = 500,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var snapshot in Snapshots.Reader.ReadAllAsync(cancellationToken)) yield return snapshot;
        }
        public async IAsyncEnumerable<FileSystemEntry> SearchAsync(string directory, string pattern, int maxResults = 500,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var snapshot in SearchSnapshotsAsync(directory, pattern, maxResults, cancellationToken))
                foreach (var entry in snapshot.Entries) yield return entry;
        }
    }

}
