using System.Diagnostics;
using Avalonia.Threading;
using MacExplorer.Collections;
using MacExplorer.Models;
using MacExplorer.Performance;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    // View-owned anchoring: no visual-tree access from the data pipeline.
    internal event Action? SnapshotApplying;
    internal event Action? SnapshotApplied;

    private async Task<IReadOnlyList<FileSystemEntry>> StreamDirectorySnapshotsAsync(
        IFileService service, string path, DirectoryWork work, EntryLoadSelectionState initialSelection)
    {
        var query = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EnsureSnapshotWorkIsCurrent(work);
            return _sortFilter.CaptureQuery();
        });
        // Later batches can contain entries that sort before the first batch.
        // Publish only the complete order so visible rows never jump on entry.
        var pipeline = new FileListDataPipeline(publishIntermediateSnapshots: false);
        IReadOnlyList<FileSystemEntry> raw = Array.Empty<FileSystemEntry>();
        FileListSnapshot? lastApplied = null;
        var hasPublished = false;
        await foreach (var received in pipeline.LoadAsync(service, path, query, work.Token))
        {
            var candidate = received;
            // A user can change sort/filter while a snapshot is in flight. Re-project
            // off-thread, then compare again inside the same UI commit that applies it.
            while (true)
            {
                EnsureSnapshotWorkIsCurrent(work);
                var currentQuery = await Dispatcher.UIThread.InvokeAsync(() => _sortFilter.CaptureQuery());
                if (candidate.Query != currentQuery)
                    candidate = await Task.Run(() => FileListDataPipeline.Reproject(received, currentQuery, work.Token), work.Token);

                var committed = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    EnsureSnapshotWorkIsCurrent(work);
                    if (_sortFilter.CaptureQuery() != candidate.Query) return false;
                    var selection = hasPublished ? CaptureEntryLoadSelectionState() : initialSelection;
                    var membershipChanged = lastApplied == null
                        || lastApplied.Version != candidate.Version
                        || lastApplied.Query != candidate.Query;
                    if (membershipChanged)
                    {
                        if (!hasPublished)
                            ScrollBehaviorAfterLoad = initialSelection.ScrollBehavior;
                        else if (ScrollBehaviorAfterLoad is not ScrollMode.RestoreNavigation and not ScrollMode.ScrollToSelected)
                            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;

                        var started = Stopwatch.GetTimestamp();
                        SnapshotApplying?.Invoke();
                        _sortFilter.ApplySnapshotMetadata(candidate);
                        // One ItemsSource replacement; never N Insert/Add notifications.
                        // Preserve the public ObservableCollection contract for other views.
                        Entries = new RangeObservableCollection<FileSystemEntry>(candidate.Entries);
                        FinalizeEntriesLoad(selection, candidate.IsFinal);
                        SnapshotApplied?.Invoke();
                        FileListPerformanceMetrics.SnapshotCommitted(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                    else if (candidate.IsFinal)
                    {
                        // Completion with unchanged membership must not reset containers again.
                        FinalizeEntriesLoad(selection, completed: true);
                    }
                    // Subsequent batches restore the *current* selection, not the selection
                    // captured when navigation began. Clicks made during loading survive.
                    hasPublished = true;
                    lastApplied = candidate;
                    raw = candidate.RawEntries;
                    return true;
                }, DispatcherPriority.Background);
                if (committed) break;
            }
        }
        EnsureSnapshotWorkIsCurrent(work);
        return raw;
    }

    private void EnsureSnapshotWorkIsCurrent(DirectoryWork work)
    {
        work.Token.ThrowIfCancellationRequested();
        if (!IsCurrentDirectoryWork(work)) throw new OperationCanceledException(work.Token);
    }
}
