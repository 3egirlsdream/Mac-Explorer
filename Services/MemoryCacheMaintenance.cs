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
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                IIdleMemoryCache[] alive;
                lock (Caches)
                {
                    Caches.RemoveAll(reference => !reference.TryGetTarget(out _));
                    alive = Caches.Select(reference => reference.TryGetTarget(out var cache) ? cache : null)
                        .OfType<IIdleMemoryCache>().ToArray();
                }
                var cutoff = DateTime.UtcNow.AddMinutes(-1);
                foreach (var cache in alive) cache.TrimMemory(cutoff);
            }
            finally { Volatile.Write(ref _pending, 0); }
        }, DispatcherPriority.Background);
    }
}
