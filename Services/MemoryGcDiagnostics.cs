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
            var generations = new GcGenerationSample[gc.GenerationInfo.Length];
            for (var g = 0; g < generations.Length; g++)
            {
                var info = gc.GenerationInfo[g];
                generations[g] = new GcGenerationSample(g switch { 3 => "loh", 4 => "poh", _ => "gen" + g },
                        info.SizeBeforeBytes, info.SizeAfterBytes, info.FragmentationBeforeBytes,
                        info.FragmentationAfterBytes);
            }
            MemoryDiagnostics.Record("memory.gc", new GcSample(sampleId, Kinds[i].ToString(), gc.Index != 0, gc.Index,
                    gc.Generation, gc.Concurrent, gc.Compacted, gc.HeapSizeBytes, gc.FragmentedBytes,
                    gc.TotalCommittedBytes, gc.HeapSizeBytes - gc.FragmentedBytes, gc.PromotedBytes,
                    gc.PinnedObjectsCount, gc.FinalizationPendingCount, gc.PauseTimePercentage,
                    gc.PauseDurations.ToArray().Select(pause => pause.TotalMilliseconds).ToArray(), gc.MemoryLoadBytes,
                    gc.HighMemoryLoadThresholdBytes, gc.TotalAvailableMemoryBytes, generations));
        }
    }
}
