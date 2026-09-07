using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public class FileListDataPipelineSnapshotTests
{
    [Theory]
    [InlineData(SortField.Name, true)]
    [InlineData(SortField.Name, false)]
    [InlineData(SortField.Modified, true)]
    [InlineData(SortField.Modified, false)]
    [InlineData(SortField.Size, true)]
    [InlineData(SortField.Size, false)]
    [InlineData(SortField.Type, true)]
    [InlineData(SortField.Type, false)]
    public async Task StreamingOrderAndGroupsMatchExistingStableSorter(SortField field, bool ascending)
    {
        var input = Enumerable.Range(0, 600).Select(i => new FileSystemEntry
        {
            FullPath = $"/root/{i}", Name = $"{600 - i:0000}", Extension = i % 2 == 0 ? ".txt" : ".jpg",
            Size = i % 5, LastModified = new DateTime(2026, 9, 3).AddMinutes(i % 3), IsDirectory = i % 9 == 0
        }).ToArray();
        var query = Query(field, ascending, GroupField.Type);
        var clock = new ManualClock();
        var pipeline = new FileListDataPipeline(TimeSpan.FromMilliseconds(100), clock);
        var snapshots = new List<FileListSnapshot>();
        await foreach (var snapshot in pipeline.LoadAsync(Batches(input, 37, () => clock.Advance(TimeSpan.FromMilliseconds(101))), query))
            snapshots.Add(snapshot);

        foreach (var snapshot in snapshots)
        {
            var sorter = new SortFilterViewModel
            {
                SortField = field, SortAscending = ascending, GroupField = GroupField.Type,
                HideDotFiles = false, HideDotFolders = false, HideSystemFiles = false
            };
            sorter.SetRawEntries(snapshot.RawEntries);
            ObservableCollection<FileSystemEntry>? expected = null;
            sorter.ApplySortAndGroup(entries => expected = entries);
            Assert.NotNull(expected);
            Assert.Equal(expected!.Select(e => e.FullPath), snapshot.Entries.Select(e => e.FullPath));
            Assert.Equal(sorter.Groups.Select(g => g.Name), snapshot.Groups.Select(g => g.Name));
            for (var group = 0; group < sorter.Groups.Count; group++)
                Assert.Equal(sorter.Groups[group].Entries.Select(e => e.FullPath), snapshot.Groups[group].Entries.Select(e => e.FullPath));
        }
        Assert.True(snapshots[^1].IsFinal);
        Assert.Equal(input.Length, snapshots[^1].Entries.Count);
    }

    [Fact]
    public async Task TenThousandFastItemsPublishFirstAndFinalInsteadOfPerFile()
    {
        var input = Enumerable.Range(0, 10_000).Select(i => Entry($"/root/{i:00000}")).ToArray();
        var pipeline = new FileListDataPipeline(timeProvider: new ManualClock());
        var snapshots = new List<FileListSnapshot>();
        await foreach (var snapshot in pipeline.LoadAsync(Batches(input, 256), Query())) snapshots.Add(snapshot);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(256, snapshots[0].Entries.Count);
        Assert.Equal(10_000, snapshots[^1].Entries.Count);
        Assert.Equal(256, snapshots[0].RawEntries.Count); // not an alias of a growing List
    }

    [Fact]
    public async Task CompletionWithUnchangedMembershipReusesVersionAndArrays()
    {
        var snapshots = new List<FileListSnapshot>();
        var pipeline = new FileListDataPipeline(timeProvider: new ManualClock());
        await foreach (var snapshot in pipeline.LoadAsync(Batches([Entry("/a")], 10), Query())) snapshots.Add(snapshot);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(snapshots[0].Version, snapshots[1].Version);
        Assert.Same(snapshots[0].Entries, snapshots[1].Entries);
        Assert.False(snapshots[0].IsFinal);
        Assert.True(snapshots[1].IsFinal);
    }

    [Fact]
    public async Task EmptyDirectoryPublishesOneFinalEmptySnapshot()
    {
        var snapshots = new List<FileListSnapshot>();
        await foreach (var snapshot in new FileListDataPipeline().LoadAsync(Batches([], 256), Query())) snapshots.Add(snapshot);
        Assert.Single(snapshots);
        Assert.True(snapshots[0].IsFinal);
        Assert.Empty(snapshots[0].Entries);
    }

    [Fact]
    public async Task FilteringRetainsRawMetadataForLaterReprojectionAndDeduplicatesPaths()
    {
        var input = new[] { Entry("/root/a.txt"), Entry("/root/.hidden"), Entry("/root/a.txt"), Entry("/root/a.fkfinder-tmp") };
        var hiddenQuery = Query() with { HideDotFiles = true };
        FileListSnapshot? final = null;
        await foreach (var snapshot in new FileListDataPipeline().LoadAsync(Batches(input, 1), hiddenQuery)) final = snapshot;
        Assert.NotNull(final);
        Assert.Single(final!.Entries);
        Assert.Equal(3, final.RawEntries.Count);
        var visible = FileListDataPipeline.Reproject(final, hiddenQuery with { HideDotFiles = false });
        Assert.Equal(2, visible.Entries.Count); // temporary files always remain excluded
        Assert.Contains(visible.Entries, e => e.Name == ".hidden");
    }

    [Fact]
    public async Task CancellationDiscardsAlreadyQueuedSnapshots()
    {
        using var cts = new CancellationTokenSource();
        var count = 0;
        var input = Enumerable.Range(0, 10_000).Select(i => Entry($"/{i}")).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var snapshot in new FileListDataPipeline().LoadAsync(Batches(input, 1), Query(), cts.Token))
            {
                count++;
                cts.Cancel();
            }
        });
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EarlyConsumerDisposalStopsAndObservesProducer()
    {
        var disposed = false;
        var clock = new ManualClock();
        async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> Infinite([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    clock.Advance(TimeSpan.FromMilliseconds(200));
                    yield return new[] { Entry($"/{i}") };
                    await Task.Yield();
                }
            }
            finally { disposed = true; }
        }
        await foreach (var snapshot in new FileListDataPipeline(timeProvider: clock).LoadAsync(Infinite(), Query())) break;
        Assert.True(disposed);
    }

    [Fact]
    public async Task ProducerFailureIsDeliveredToConsumer()
    {
        async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> Failure()
        {
            yield return new[] { Entry("/a") };
            await Task.Yield();
            throw new IOException("directory disappeared");
        }
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var snapshot in new FileListDataPipeline().LoadAsync(Failure(), Query())) { }
        });
    }

    [Fact]
    public void MergePreservesLeftBeforeRightForEqualSortKeys()
    {
        var first = Entry("/left");
        var second = Entry("/right");
        var merged = FileListDataPipeline.MergeSorted([first], [second], Comparer<FileSystemEntry>.Create((_, _) => 0));
        Assert.Same(first, merged[0]);
        Assert.Same(second, merged[1]);
    }

    private static FileListQuery Query(SortField field = SortField.Name, bool ascending = true, GroupField groups = GroupField.None)
        => new(field, ascending, groups, false, false, false);

    private static FileSystemEntry Entry(string path) => new()
    {
        FullPath = path, Name = Path.GetFileName(path), LastModified = new DateTime(2026, 9, 3)
    };

    private static async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> Batches(
        IReadOnlyList<FileSystemEntry> entries, int size, Action? beforeBatch = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var offset = 0; offset < entries.Count; offset += size)
        {
            ct.ThrowIfCancellationRequested();
            beforeBatch?.Invoke();
            yield return entries.Skip(offset).Take(size).ToArray();
            await Task.Yield();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan value) => Interlocked.Add(ref _ticks, value.Ticks);
    }
}
