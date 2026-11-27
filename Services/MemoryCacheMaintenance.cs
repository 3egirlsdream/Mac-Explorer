using Avalonia.Threading;

namespace MacExplorer.Services;

internal interface IIdleMemoryCache
{
    void TrimMemory(DateTime unusedBefore);
}

internal static class MemoryCacheMaintenance
{
    private static readonly List<WeakReference<IIdleMemoryCache>> Caches = [];
    private static readonly Timer Timer = CreateTimer();
    private static int _pending;

    private static Timer CreateTimer()
    {
        // The process-wide timer must not capture the first caller's task/AsyncLocal state.
        if (ExecutionContext.IsFlowSuppressed()) return new(_ => ScheduleTrim(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        using (ExecutionContext.SuppressFlow())
            return new(_ => ScheduleTrim(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    internal static void Register(IIdleMemoryCache cache)
    {
        lock (Caches) Caches.Add(new(cache));
        GC.KeepAlive(Timer);
    }

    private static void ScheduleTrim()
    {
        lock (Caches)
        {
            if (Caches.Count == 0) return;
        }
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { TrimNow(); }
                finally { Volatile.Write(ref _pending, 0); }
            }, DispatcherPriority.Background);
        }
        catch { Volatile.Write(ref _pending, 0); }
    }

    internal static void TrimNow(DateTime? cutoff = null)
    {
        List<IIdleMemoryCache>? alive = null;
        lock (Caches)
        {
            if (Caches.Count == 0) return;
            var write = 0;
            for (var i = 0; i < Caches.Count; i++)
            {
                var reference = Caches[i];
                if (reference.TryGetTarget(out var cache))
                {
                    Caches[write++] = reference;
                    (alive ??= new List<IIdleMemoryCache>(Caches.Count)).Add(cache);
                }
            }
            if (write < Caches.Count) Caches.RemoveRange(write, Caches.Count - write);
        }
        if (alive == null || alive.Count == 0) return;
        var effectiveCutoff = cutoff ?? DateTime.UtcNow.AddMinutes(-1);
        for (var i = 0; i < alive.Count; i++)
        {
            var cache = alive[i];
            try { cache.TrimMemory(effectiveCutoff); }
            catch (Exception ex)
            {
                if (MemoryDiagnostics.IsEnabled)
                    MemoryDiagnostics.Record("memory.trim_failed",
                        new MemorySourceFailure(cache.GetType().Name, ex.GetType().Name));
            }
        }
    }
}
