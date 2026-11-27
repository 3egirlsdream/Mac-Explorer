using System.Diagnostics;

namespace MacExplorer.Services;

internal enum DiagnosticObjectKind { Window, Tab, Workspace, FileList, Navigation, Preview, InfoPanel, DocumentPreview, VideoPreview }
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
        LifetimeState data;
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
        LifetimeState data;
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

    private static LifetimeState Snapshot(MemoryDiagnosticLifetime entry, string change)
        => new LifetimeState(entry.Id, entry.Kind.ToString(), entry.RelatedId, change, entry.State.ToString());

    internal void Sample(long sampleId = 0)
    {
        LifetimeSample summary;
        RetainedLifetimes? candidates = null;
        lock (_sync)
        {
            var gen2 = GC.CollectionCount(2);
            var summaries = new Dictionary<DiagnosticObjectKind, LifetimeCounts>();
            var retained = new List<RetainedLifetime>(16);
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
                if (retained.Count < 16) retained.Add(new RetainedLifetime(id, entry.Kind.ToString(), entry.RelatedId,
                        entry.State.ToString(), (long)age, collections));
            }
            summary = new LifetimeSample(sampleId, _entries.Count, Capacity, _evicted, _expired,
                        summaries.Select(pair => new LifetimeKindSample(pair.Key.ToString(), pair.Value.Alive,
                        pair.Value.Collected, pair.Value.DetachedAlive, pair.Value.EndedAlive,
                        pair.Value.EndedAliveAfterGen2)).ToArray());
            if (retained.Count > 0) candidates = new RetainedLifetimes(sampleId, 16, retained);
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
        MemoryDiagnostics.Record("operation.start", new DiagnosticOperationStart(_id, action.ToString(), objectId,
                                                        relatedId));
    }

    internal void Complete(DiagnosticOutcome outcome = DiagnosticOutcome.Completed, int? count = null)
    {
        if (_finished) return;
        _finished = true;
        MemoryDiagnostics.Record("operation.end", new DiagnosticOperationEnd(_id, _action.ToString(), _objectId,
                _relatedId, outcome.ToString(), count, Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
    }

    public void Dispose() => Complete(DiagnosticOutcome.Interrupted);
}
