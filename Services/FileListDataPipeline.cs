using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MacExplorer.Models;
using MacExplorer.Performance;
using MacExplorer.ViewModels;

namespace MacExplorer.Services;

/// <summary>Capture on the UI thread. Never read live sort settings on a worker.</summary>
public sealed record FileListQuery(
    SortField SortField,
    bool SortAscending,
    GroupField GroupField,
    bool HideSystemFiles,
    bool HideDotFiles,
    bool HideDotFolders)
{
    public FileListFilterState ColumnFilters { get; init; } = FileListFilterState.Empty;
    public DateTime FilterDate { get; init; } = DateTime.Today;
}

public sealed record FileListGroupSnapshot(string Name, IReadOnlyList<FileSystemEntry> Entries);

/// <summary>
/// Membership/order is read-only and detached from the enumerator. Entry presentation
/// properties remain UI-owned; the producer only reads immutable file metadata.
/// Version changes only when membership changes. A final marker may reuse a version.
/// </summary>
public sealed record FileListSnapshot(
    long Version,
    FileListQuery Query,
    IReadOnlyList<FileSystemEntry> RawEntries,
    IReadOnlyList<FileSystemEntry> Entries,
    IReadOnlyList<FileListGroupSnapshot> Groups,
    bool IsFinal);

public sealed class FileListDataPipeline
{
    private readonly TimeSpan _publishInterval;
    private readonly TimeProvider _timeProvider;
    private readonly bool _publishIntermediateSnapshots;

    public FileListDataPipeline(TimeSpan? publishInterval = null, TimeProvider? timeProvider = null,
        bool publishIntermediateSnapshots = true)
    {
        _publishInterval = publishInterval ?? TimeSpan.FromMilliseconds(100);
        if (_publishInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(publishInterval));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _publishIntermediateSnapshots = publishIntermediateSnapshots;
    }

    public IAsyncEnumerable<FileListSnapshot> LoadAsync(
        IFileService service, string path, FileListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(path);
        return LoadAsync(service.EnumerateDirectoryBatchesAsync(path, 256, cancellationToken), query, cancellationToken);
    }

    /// <summary>
    /// One queued snapshot and one consumer commit provide backpressure. All enumeration,
    /// filtering, sorting, merging and grouping run on a worker, including synchronous
    /// continuations of an enumerator. Disposing the consumer cancels/observes the producer.
    /// </summary>
    public async IAsyncEnumerable<FileListSnapshot> LoadAsync(
        IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> batches,
        FileListQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<FileListSnapshot>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        var producer = Task.Run(async () =>
        {
            try
            {
                await ProduceAsync(batches, query, channel.Writer, lifetime.Token).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception error)
            {
                // Completion forwards enumeration errors to the consumer without an
                // unobserved fire-and-forget task or a reader waiting forever.
                channel.Writer.TryComplete(error);
            }
        }, CancellationToken.None);
        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                // Queued snapshots must not leak after navigation has been cancelled.
                lifetime.Token.ThrowIfCancellationRequested();
                yield return snapshot;
            }
        }
        finally
        {
            lifetime.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(
        IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> batches,
        FileListQuery query,
        ChannelWriter<FileListSnapshot> writer,
        CancellationToken ct)
    {
        var processor = SortFilterViewModel.CreateSnapshotProcessor(query);
        var comparer = processor.BuildEntriesComparer();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var raw = new List<FileSystemEntry>();
        var pending = new List<FileSystemEntry>();
        FileSystemEntry[] sorted = [];
        FileListSnapshot? previous = null;
        long version = 0;
        var lastPublish = _timeProvider.GetTimestamp();
        var publishedRawCount = -1;

        FileListSnapshot Flush(bool final)
        {
            ct.ThrowIfCancellationRequested();
            // LINQ OrderBy is stable. Array.Sort would reorder equal sizes/dates and
            // would not match the application's existing stable sorting contract.
            var right = pending.OrderBy(entry => entry, comparer).ToArray();
            ct.ThrowIfCancellationRequested();
            sorted = MergeSorted(sorted, right, comparer, ct);
            pending.Clear();
            var entries = Array.AsReadOnly(sorted);
            var snapshot = new FileListSnapshot(++version, query,
                Array.AsReadOnly(raw.ToArray()), entries,
                processor.BuildSnapshotGroups(entries), final);
            ct.ThrowIfCancellationRequested();
            publishedRawCount = raw.Count;
            previous = snapshot;
            return snapshot;
        }

        await foreach (var batch in batches.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            FileListPerformanceMetrics.BatchReceived();
            foreach (var entry in batch)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(entry.FullPath)) continue;
                raw.Add(entry);
                if (processor.PassesSnapshotFilter(entry)) pending.Add(entry);
            }
            if (!_publishIntermediateSnapshots || raw.Count == publishedRawCount || raw.Count == 0) continue;
            if (previous != null && _timeProvider.GetElapsedTime(lastPublish) < _publishInterval) continue;

            var snapshot = Flush(final: false);
            await writer.WriteAsync(snapshot, ct).ConfigureAwait(false);
            FileListPerformanceMetrics.SnapshotPublished(snapshot.Entries.Count);
            // Include sorting/commit backpressure in the interval: never catch up by
            // emitting a burst of snapshots after a slow consumer resumes.
            lastPublish = _timeProvider.GetTimestamp();
        }

        ct.ThrowIfCancellationRequested();
        var finalHasChanges = previous == null || publishedRawCount != raw.Count;
        var finalSnapshot = !finalHasChanges && previous != null
            ? previous with { IsFinal = true }
            : Flush(final: true);
        await writer.WriteAsync(finalSnapshot, ct).ConfigureAwait(false);
        if (finalHasChanges)
            FileListPerformanceMetrics.SnapshotPublished(finalSnapshot.Entries.Count);
    }

    /// <summary>Stable O(left + right) merge. Left wins ties (earlier arrival).</summary>
    public static FileSystemEntry[] MergeSorted(
        IReadOnlyList<FileSystemEntry> left,
        IReadOnlyList<FileSystemEntry> right,
        IComparer<FileSystemEntry> comparer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(comparer);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new FileSystemEntry[checked(left.Count + right.Count)];
        int i = 0, j = 0, k = 0;
        while (i < left.Count || j < right.Count)
        {
            if ((k & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            result[k++] = j >= right.Count || i < left.Count && comparer.Compare(left[i], right[j]) <= 0
                ? left[i++] : right[j++];
        }
        return result;
    }

    /// <summary>Re-project a detached raw snapshot when the user changes sort/filter mid-load.</summary>
    public static FileListSnapshot Reproject(FileListSnapshot snapshot, FileListQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var processor = SortFilterViewModel.CreateSnapshotProcessor(query);
        var sorted = snapshot.RawEntries.Where(processor.PassesSnapshotFilter)
            .OrderBy(entry => entry, processor.BuildEntriesComparer()).ToArray();
        ct.ThrowIfCancellationRequested();
        var entries = Array.AsReadOnly(sorted);
        var result = snapshot with { Query = query, Entries = entries, Groups = processor.BuildSnapshotGroups(entries) };
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
