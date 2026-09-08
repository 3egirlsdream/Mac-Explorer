using System.Diagnostics.Metrics;

namespace MacExplorer.Performance;

/// <summary>Low-cost counters usable in Release through System.Diagnostics.Metrics.</summary>
public static class FileListPerformanceMetrics
{
    public const string MeterName = "MacExplorer.FileList";
    private static readonly Meter Meter = new(MeterName, "1.0");
    private static readonly Counter<long> Batches = Meter.CreateCounter<long>("filelist.directory.batch.received");
    private static readonly Counter<long> Snapshots = Meter.CreateCounter<long>("filelist.directory.snapshot.published");
    private static readonly Histogram<int> SnapshotItems = Meter.CreateHistogram<int>("filelist.directory.snapshot.items");
    private static readonly Counter<long> Commits = Meter.CreateCounter<long>("filelist.collection.snapshot.committed");
    private static readonly Histogram<double> CommitDuration = Meter.CreateHistogram<double>("filelist.ui.commit.duration_ms", "ms");
    private static readonly Counter<long> GitUpdates = Meter.CreateCounter<long>("filelist.git.items.updated");
    private static readonly Histogram<double> FastRenderDuration = Meter.CreateHistogram<double>("filelist.fast.render.duration_ms", "ms");
    private static readonly Histogram<int> FastRenderRows = Meter.CreateHistogram<int>("filelist.fast.render.rows");

    public static void BatchReceived() => Batches.Add(1);
    public static void SnapshotPublished(int count) { Snapshots.Add(1); SnapshotItems.Record(count); }
    public static void SnapshotCommitted(double milliseconds) { Commits.Add(1); CommitDuration.Record(milliseconds); }
    public static void GitItemsUpdated(int count) => GitUpdates.Add(count);
    public static void FastListRendered(double milliseconds, int rows)
    {
        FastRenderDuration.Record(milliseconds);
        FastRenderRows.Record(rows);
    }
}
