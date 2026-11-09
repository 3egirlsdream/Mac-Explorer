namespace MacExplorer.Services;

internal sealed class MemoryGcDiagnostics
{
    private readonly long[] _lastIndices = [-1, -1, -1];
    private static readonly GCKind[] Kinds = [GCKind.Ephemeral, GCKind.FullBlocking, GCKind.Background];

    internal void Sample(long sampleId = 0)
    {
        for (var i = 0; i < Kinds.Length; i++)
        {
            var gc = GC.GetGCMemoryInfo(Kinds[i]);
            if (gc.Index == _lastIndices[i]) continue;
            _lastIndices[i] = gc.Index;
            var generations = new object[gc.GenerationInfo.Length];
            for (var g = 0; g < generations.Length; g++)
            {
                var info = gc.GenerationInfo[g];
                generations[g] = new { generation = g switch { 3 => "loh", 4 => "poh", _ => "gen" + g },
                    sizeBeforeBytes = info.SizeBeforeBytes, sizeAfterBytes = info.SizeAfterBytes,
                    fragmentationBeforeBytes = info.FragmentationBeforeBytes, fragmentationAfterBytes = info.FragmentationAfterBytes };
            }
            MemoryDiagnostics.Record("memory.gc", new { sampleId, kind = Kinds[i].ToString(), available = gc.Index != 0,
                index = gc.Index, generation = gc.Generation, concurrent = gc.Concurrent, compacted = gc.Compacted,
                heapBytes = gc.HeapSizeBytes, fragmentedBytes = gc.FragmentedBytes, committedBytes = gc.TotalCommittedBytes,
                liveAfterGcBytes = gc.HeapSizeBytes - gc.FragmentedBytes,
                promotedBytes = gc.PromotedBytes, pinnedObjects = gc.PinnedObjectsCount,
                finalizationPending = gc.FinalizationPendingCount, pauseTimePercentage = gc.PauseTimePercentage,
                pauseDurationsMs = gc.PauseDurations.ToArray().Select(pause => pause.TotalMilliseconds).ToArray(),
                memoryLoadBytes = gc.MemoryLoadBytes, highMemoryLoadThresholdBytes = gc.HighMemoryLoadThresholdBytes,
                totalAvailableMemoryBytes = gc.TotalAvailableMemoryBytes, generations });
        }
    }
}
