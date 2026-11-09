using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using MacExplorer.Services;
using SkiaSharp;

internal static class MemoryRetentionProbe
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("MACEXPLORER_MEMORY_RETENTION_PROBE") == "1";

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "malloc_zone_pressure_relief")]
    private static extern nuint RelieveMalloc(IntPtr zone, nuint goal);

    internal static void Collect(bool compactLoh = false, bool aggressive = false)
    {
        if (compactLoh) GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, aggressive ? GCCollectionMode.Aggressive : GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        if (compactLoh) GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, aggressive ? GCCollectionMode.Aggressive : GCCollectionMode.Forced, true, true);
    }

    internal static void Record(string stage, ulong releasedBytes = 0)
    {
        var gc = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        MemoryDiagnostics.Record("retention.state", new
        {
            stage, releasedBytes, footprintBytes = MemoryDiagnostics.ReadPhysicalFootprint(),
            managedBytes = GC.GetTotalMemory(false), currentGcIndex = GC.GetGCMemoryInfo().Index,
            blockingGcIndex = gc.Index, gc.Compacted, gc.Concurrent, committedBytes = gc.TotalCommittedBytes,
            heapBytes = gc.HeapSizeBytes, fragmentationBytes = gc.FragmentedBytes,
            liveAfterBlockingGcBytes = gc.HeapSizeBytes - gc.FragmentedBytes,
            pinnedObjects = gc.PinnedObjectsCount, lohCompactionMode = GCSettings.LargeObjectHeapCompactionMode.ToString(),
            generations = gc.GenerationInfo.ToArray().Select((g, index) => new
                { index, sizeBytes = g.SizeAfterBytes, fragmentationBytes = g.FragmentationAfterBytes }).ToArray(),
            skiaResourceBytes = SKGraphics.GetResourceCacheTotalBytesUsed(), skiaFontBytes = SKGraphics.GetFontCacheUsed(),
            compiledIlBytes = JitInfo.GetCompiledILBytes(), compiledMethods = JitInfo.GetCompiledMethodCount(),
            assemblies = AppDomain.CurrentDomain.GetAssemblies().Length, sources = MemoryDiagnostics.ReadSources(),
            jitCode = RetentionJitListener.Statistics, serverGc = GCSettings.IsServerGC,
            gcConfiguration = GC.GetConfigurationVariables().Where(pair => pair.Value is not string).ToDictionary()
        });
    }

    internal static async Task RunAsync(Action<string> checkpoint, Func<Task> detach)
    {
        async Task Capture(string stage, ulong released = 0)
        {
            Record(stage, released);
            checkpoint("retention." + stage);
            // Keep this exact state alive long enough for numeric vmmap/heap sampling.
            await Task.Delay(10000);
        }

        await Capture("after-standard-gc");
        for (var index = 0; index < 12; index++)
        {
            await Task.Delay(10000);
            Record("natural-idle");
            checkpoint("retention.natural-idle");
        }
        await Capture("idle-end");
        Collect();
        await Capture("idle-standard-gc");
        Collect(compactLoh: true);
        await Capture("loh-compact");
        Collect(compactLoh: true, aggressive: true);
        await Capture("aggressive-gc");
        await Capture("malloc-relief", (ulong)RelieveMalloc(IntPtr.Zero, 0));
        SKGraphics.PurgeAllCaches();
        await Capture("skia-cpu-purge");
        await Capture("skia-malloc-relief", (ulong)RelieveMalloc(IntPtr.Zero, 0));
        await detach();
        Collect(compactLoh: true, aggressive: true);
        await Capture("detached", (ulong)RelieveMalloc(IntPtr.Zero, 0));
        if (Environment.GetEnvironmentVariable("MACEXPLORER_MEMORY_HEAP_GRAPH_HOLD") == "1")
        {
            checkpoint("retention.heap-graph-hold");
            await Task.Delay(30000);
        }
    }
}
