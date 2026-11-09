using System.Diagnostics;

namespace MacExplorer.Services;

internal enum DiagnosticObjectKind { Window, Tab, Workspace, FileList, Navigation, Preview, InfoPanel, DocumentPreview }
internal enum DiagnosticObjectState { Created, Attached, Detached, Released, Disposed, Closed }
internal enum DiagnosticAction
{
    WindowOpened, WindowVisibility, TabSelected, Navigate, LocationChanged, DirectoryLoad, ViewMode,
    WorkspaceVisibility, InfoPanelVisibility, TreeExpand, TreeCollapse, PreviewOpen, PreviewClose, PreviewSelection
}
internal enum DiagnosticOutcome { Completed, Cancelled, Failed, Interrupted }

/// <summary>Contains only anonymous IDs and a weak owner; observing a lifetime cannot extend it.</summary>
internal sealed class MemoryDiagnosticLifetime
{
    private readonly MemoryDiagnosticLifetimes _registry;
    internal long Id { get; }
    internal DiagnosticObjectKind Kind { get; }
    internal WeakReference<object> Owner { get; }
    internal long RelatedId;
    internal DiagnosticObjectState State;
    internal long StateAt = Stopwatch.GetTimestamp();
    internal int EndGen2;
    internal bool Ended => State is DiagnosticObjectState.Released or DiagnosticObjectState.Disposed or DiagnosticObjectState.Closed;

    internal MemoryDiagnosticLifetime(MemoryDiagnosticLifetimes registry, long id, object owner, DiagnosticObjectKind kind, long relatedId)
    {
        _registry = registry;
        Id = id; Kind = kind; RelatedId = relatedId; Owner = new(owner);
    }

    internal void SetState(DiagnosticObjectState state) => _registry.SetState(this, state);
    internal void Link(long relatedId) => _registry.Link(this, relatedId);
}

internal sealed class MemoryDiagnosticLifetimes
{
    internal const int Capacity = 2048;
    internal static readonly TimeSpan EndedRetention = TimeSpan.FromHours(24);
    private readonly object _sync = new();
    private readonly SortedDictionary<long, MemoryDiagnosticLifetime> _entries = [];
    private long _nextId, _evicted, _expired;

    internal MemoryDiagnosticLifetime Track(object owner, DiagnosticObjectKind kind, long relatedId = 0)
    {
        MemoryDiagnosticLifetime entry;
        lock (_sync)
        {
            if (_entries.Count >= Capacity)
            {
                _entries.Remove(_entries.Keys.First());
                _evicted++;
            }
            entry = new(this, ++_nextId, owner, kind, relatedId);
            _entries.Add(entry.Id, entry);
        }
        Record(entry, "created");
        return entry;
    }

    internal void SetState(MemoryDiagnosticLifetime entry, DiagnosticObjectState state)
    {
        object data;
        lock (_sync)
        {
            if (entry.State == state || entry.Ended) return;
            entry.State = state;
            entry.StateAt = Stopwatch.GetTimestamp();
            if (entry.Ended) entry.EndGen2 = GC.CollectionCount(2);
            data = Snapshot(entry, "state");
        }
        MemoryDiagnostics.Record("lifetime.object", data);
    }

    internal void Link(MemoryDiagnosticLifetime entry, long relatedId)
    {
        object data;
        lock (_sync)
        {
            if (entry.Ended || relatedId == 0 || entry.RelatedId == relatedId) return;
            entry.RelatedId = relatedId;
            data = Snapshot(entry, "linked");
        }
        MemoryDiagnostics.Record("lifetime.object", data);
    }

    private static void Record(MemoryDiagnosticLifetime entry, string change)
        => MemoryDiagnostics.Record("lifetime.object", Snapshot(entry, change));

    private static object Snapshot(MemoryDiagnosticLifetime entry, string change)
        => new { objectId = entry.Id, kind = entry.Kind.ToString(), relatedId = entry.RelatedId, change, state = entry.State.ToString() };

    internal void Sample(long sampleId = 0)
    {
        object summary;
        object? candidates = null;
        lock (_sync)
        {
            var gen2 = GC.CollectionCount(2);
            var summaries = new Dictionary<DiagnosticObjectKind, LifetimeCounts>();
            var retained = new List<object>(16);
            foreach (var (id, entry) in _entries.ToArray())
            {
                if (!summaries.TryGetValue(entry.Kind, out var counts)) summaries[entry.Kind] = counts = new();
                if (!entry.Owner.TryGetTarget(out _))
                {
                    counts.Collected++;
                    _entries.Remove(id);
                    continue;
                }
                if (entry.Ended && Stopwatch.GetElapsedTime(entry.StateAt) >= EndedRetention)
                { _entries.Remove(id); _expired++; continue; }
                counts.Alive++;
                if (entry.State == DiagnosticObjectState.Detached) counts.DetachedAlive++;
                if (!entry.Ended) continue;
                counts.EndedAlive++;
                var age = Stopwatch.GetElapsedTime(entry.StateAt).TotalSeconds;
                var collections = gen2 - entry.EndGen2;
                if (collections <= 0 || age < 30) continue;
                counts.EndedAliveAfterGen2++;
                if (retained.Count < 16) retained.Add(new { objectId = id, kind = entry.Kind.ToString(),
                    relatedId = entry.RelatedId, state = entry.State.ToString(), ageSeconds = (long)age, gen2Collections = collections });
            }
            summary = new { sampleId, tracked = _entries.Count, capacity = Capacity, evicted = _evicted, expired = _expired,
                kinds = summaries.Select(pair => new { kind = pair.Key.ToString(), alive = pair.Value.Alive,
                    collectedSinceSample = pair.Value.Collected, detachedAlive = pair.Value.DetachedAlive,
                    endedAlive = pair.Value.EndedAlive, endedAliveAfterGen2 = pair.Value.EndedAliveAfterGen2 }).ToArray() };
            if (retained.Count > 0) candidates = new { sampleId, limit = 16, objects = retained };
        }
        MemoryDiagnostics.Record("lifetime.sample", summary);
        if (candidates != null) MemoryDiagnostics.Record("lifetime.retained", candidates);
    }

    private sealed class LifetimeCounts
    {
        internal int Alive, Collected, DetachedAlive, EndedAlive, EndedAliveAfterGen2;
    }
}

internal sealed class MemoryDiagnosticOperation : IDisposable
{
    private static long _nextId;
    private readonly long _id = Interlocked.Increment(ref _nextId);
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly DiagnosticAction _action;
    private readonly long _objectId, _relatedId;
    private bool _finished;

    internal MemoryDiagnosticOperation(DiagnosticAction action, long objectId, long relatedId)
    {
        _action = action; _objectId = objectId; _relatedId = relatedId;
        MemoryDiagnostics.Record("operation.start", new { operationId = _id, action = action.ToString(), objectId, relatedId });
    }

    internal void Complete(DiagnosticOutcome outcome = DiagnosticOutcome.Completed, int? count = null)
    {
        if (_finished) return;
        _finished = true;
        MemoryDiagnostics.Record("operation.end", new { operationId = _id, action = _action.ToString(), objectId = _objectId,
            relatedId = _relatedId, outcome = outcome.ToString(), count, elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds });
    }

    public void Dispose() => Complete(DiagnosticOutcome.Interrupted);
}
