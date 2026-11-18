using System.Threading.Channels;
using MacExplorer.Indexing;

namespace MacExplorer.Services.Search;

/// <summary>One background writer, resumable directory traversals, and independent search readers.</summary>
public sealed partial class SearchIndexer : IAsyncDisposable
{
    private readonly SearchCatalog _catalog;
    private readonly IndexConfiguration _configuration;
    private readonly IPinyinInitials _pinyin;
    private readonly ISearchChangeSource _changes;
    private readonly TimeProvider _timeProvider;
    private readonly DirectoryAccess _access;
    private readonly object _gate = new();
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.Ordinal);
    private readonly Channel<byte> _work = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Queue<RootState> _ready = new(), _urgentReady = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private bool _disposed;
    private Task? _disposeTask;
    private long _receivedEvents, _filteredEvents, _checkedEntries, _inserted, _updated, _deleted, _directoryScans, _fullScans;
    private long _pendingPeak, _pendingBytesPeak;
    private readonly Dictionary<SearchWorkReason, long> _scanReasons = [];

    private static readonly HashSet<string> ExcludedNames = new(StringComparer.Ordinal)
    {
        ".git", ".svn", ".hg", "node_modules", ".cache", ".gradle", "DerivedData",
        ".Spotlight-V100", ".fseventsd", ".Trash", ".Trashes"
    };
    private static readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".app", ".photoslibrary", ".musiclibrary", ".photobooth", ".fcpbundle",
        ".bundle", ".framework", ".plugin", ".appex", ".kext", ".pvm", ".vmwarevm"
    };

    public SearchIndexer(SearchCatalog catalog, IndexConfiguration configuration, IPinyinInitials pinyin,
        ISearchChangeSource changes, TimeProvider? timeProvider = null)
    {
        _catalog = catalog; _configuration = configuration; _pinyin = pinyin; _changes = changes;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _access = DirectoryAccess.Current;
        _access.GrantsChanged += OnGrantsChanged;
        _diagnosticWorker = Task.Run(DiagnosticsAsync);
        _worker = Task.Run(WorkAsync);
    }

    private sealed record DirectoryError(string Directory, string? Entry, string ExceptionType, int HResult, string Message);
    private sealed class Traversal(SearchWork[] items)
    {
        private readonly LinkedList<SearchWork> _items = new(items);
        public int Count => _items.Count;
        public SearchWork? First => _items.First?.Value;
        public void Enqueue(SearchWork work) => _items.AddLast(work);
        public SearchWork Dequeue() { var work = _items.First!.Value; _items.RemoveFirst(); return work; }
        public void AddChildren(List<string> children, SearchWorkReason reason)
        { for (var i = children.Count - 1; i >= 0; i--) _items.AddFirst(new SearchWork(children[i], SearchWorkKind.Recursive, reason)); }
    }
    private sealed class Generation(long number, ulong observed, SearchWork[] work, bool restart)
    {
        public long Number = number;
        public ulong Observed = observed;
        public readonly Traversal Work = new(work);
        public bool Restart = restart, Started;
        public bool Urgent = work.Any(item => item.Urgent);
        public bool FullScan;
    }
    private sealed class RootState(string root, IndexConfiguration configuration)
    {
        public string Root { get; } = root;
        public SearchIndexStatus Status = new(root, SearchIndexPhase.Building);
        public readonly SearchPendingWork Pending = new(root, configuration.PendingLimit, configuration.PendingBytesLimit);
        public readonly List<Generation> Generations = [];
        public readonly Dictionary<string, DirectoryError> DirectoryErrors = new(StringComparer.Ordinal);
        public string? WatcherError;
        public TaskCompletionSource<bool> Changed = NewSignal();
        public IDisposable? Watcher;
        public bool Queued, PriorityQueued, Running, Started, RestartWatcher, IncludeHiddenFolders, Deferred, RequireReconciliation;
        public ulong ObservedId, Checkpoint, RetiredObserved;
        public long? LastAttemptTick;
        public long NextGeneration, LastProgressTick;
        public long? LastFullScanTick;
        public CancellationTokenSource? Cooldown;
        public Task? CooldownTask;
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse(RootState state, bool diagnostics = true)
    {
        var previous = state.Changed; state.Changed = NewSignal(); previous.TrySetResult(true);
        if (diagnostics) RequestDiagnostics();
    }

    public bool EnsureRoot(string directory, bool includeHiddenFolders = false)
    {
        _access.EnsureAccess(directory);
        var root = SearchPath.Normalize(directory);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var state = FindState(root);
            if (state == null)
            {
                state = new(root, _configuration) { IncludeHiddenFolders = includeHiddenFolders };
                _roots.Add(root, state);
                AddWork(state, new(root, SearchWorkKind.Recursive, SearchWorkReason.Initial));
                return true;
            }
            if (includeHiddenFolders && !state.IncludeHiddenFolders)
            {
                state.IncludeHiddenFolders = true;
                AddWork(state, new(state.Root, SearchWorkKind.Recursive, SearchWorkReason.Coverage));
                return true;
            }
            if (!state.Running && !state.Queued && (state.Status.Phase == SearchIndexPhase.Unavailable || state.Watcher == null)
                && (state.LastAttemptTick is not { } attempt || _timeProvider.GetElapsedTime(attempt) > TimeSpan.FromSeconds(30)))
                AddWork(state, new(state.Root, SearchWorkKind.Recursive, SearchWorkReason.WatcherRecovery));
            return false;
        }
    }

    public void Refresh(string directory)
    {
        EnsureRoot(directory);
        lock (_gate)
        {
            var state = FindState(SearchPath.Normalize(directory))!;
            state.RestartWatcher = true;
            AddWork(state, new(state.Root, SearchWorkKind.Recursive, SearchWorkReason.Refresh));
        }
    }

    public (SearchIndexStatus Status, Task Changed) Observe(string directory)
    {
        var root = SearchPath.Normalize(directory);
        lock (_gate)
        {
            var state = FindState(root);
            return state == null ? (new SearchIndexStatus(root, SearchIndexPhase.Unavailable), Task.CompletedTask)
                : (state.Status with { Root = root }, state.Changed.Task);
        }
    }

    private RootState? FindState(string root) => _roots.Values
        .Where(state => SearchPath.IsWithin(root, state.Root) && CanDescend(state, root))
        .OrderByDescending(state => state.Root.Length).FirstOrDefault();

    private void Queue(RootState state)
    {
        if (_disposed || state.Running) return;
        var urgent = state.Pending.HasUrgent || state.Generations.Any(generation => generation.Urgent);
        if (state.Queued && (!urgent || state.PriorityQueued)) return;
        state.Queued = true; state.PriorityQueued = urgent;
        (urgent ? _urgentReady : _ready).Enqueue(state);
        _work.Writer.TryWrite(0);
    }

    private RootState? TakeReady()
    {
        while (_urgentReady.TryDequeue(out var state))
            if (state.Queued && state.PriorityQueued)
            { state.Queued = state.PriorityQueued = false; return state; }
        while (_ready.TryDequeue(out var state))
            if (state.Queued && !state.PriorityQueued) { state.Queued = false; return state; }
        return null;
    }

    private void AddWork(RootState state, SearchWork work)
    {
        state.Pending.Add(work);
        if (work.Path == state.Root && work.Kind == SearchWorkKind.Recursive && work.Urgent)
        {
            state.Deferred = false; state.Cooldown?.Cancel();
        }
        _pendingPeak = Math.Max(_pendingPeak, state.Pending.Count);
        _pendingBytesPeak = Math.Max(_pendingBytesPeak, state.Pending.Bytes);
        DeferOverflow(state);
        state.Status = state.Status with { Phase = SearchIndexPhase.Building };
        Pulse(state);
        if (state.Pending.Count != 0) Queue(state);
    }

    private void DeferOverflow(RootState state)
    {
        if (state.LastFullScanTick is { } last)
        {
            var remaining = _configuration.OverflowFullScanCooldown - _timeProvider.GetElapsedTime(last);
            if (remaining > TimeSpan.Zero && state.Pending.RemoveOverflow() != null)
            {
                state.Deferred = state.RequireReconciliation = true;
                RequestDiagnostics();
                if (state.Cooldown == null || state.Cooldown.IsCancellationRequested)
                {
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    state.Cooldown = cancellation;
                    state.CooldownTask = RequeueOverflowAsync(state, remaining, cancellation);
                }
            }
        }
    }

    private async Task RequeueOverflowAsync(RootState state, TimeSpan delay, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || cancellation.IsCancellationRequested || !state.Deferred) return;
                state.Deferred = false;
                state.Pending.Add(new(state.Root, SearchWorkKind.Recursive, SearchWorkReason.OverflowFallback));
                Pulse(state);
                Queue(state);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            lock (_gate) if (ReferenceEquals(state.Cooldown, cancellation)) state.Cooldown = null;
            cancellation.Dispose();
        }
    }

    private void OnChange(RootState state, SearchChange change)
    {
        lock (_gate)
        {
            _receivedEvents++;
            if (_disposed || !_access.CanAccess(state.Root) || !_access.CanAccess(change.Path) || IsIndexArtifact(change.Path))
            { _filteredEvents++; return; }
            state.ObservedId = change.RestartWatcher ? change.EventId : Math.Max(state.ObservedId, change.EventId);
            state.RestartWatcher |= change.RestartWatcher;
            if (change.MustRescan)
            {
                var scope = SearchPath.IsWithin(change.Path, state.Root) ? change.Path : state.Root;
                var scopeParent = Path.GetDirectoryName(scope);
                if (scope != state.Root && scopeParent != null && CanDescend(state, scopeParent))
                    AddWork(state, new(scopeParent, SearchWorkKind.Directory, SearchWorkReason.JournalRecovery));
                AddWork(state, new(scope,
                    SearchWorkKind.Recursive, SearchWorkReason.JournalRecovery));
                return;
            }
            if (!SearchPath.IsWithin(change.Path, state.Root)) { _filteredEvents++; return; }
            var parent = Path.GetDirectoryName(change.Path);
            if (change.Path == state.Root && change.IsDirectory)
            { AddWork(state, new(state.Root, SearchWorkKind.Directory)); return; }
            if (parent == null || !CanDescend(state, parent) || change.Path.EndsWith(".fkfinder-tmp", StringComparison.OrdinalIgnoreCase))
            { _filteredEvents++; return; }
            if (change.Kind == SearchChangeKind.Modified)
            {
                AddWork(state, new(change.Path, SearchWorkKind.Entry));
                return;
            }
            AddWork(state, new(parent, SearchWorkKind.Directory));
            if (change.IsDirectory && CanDescend(state, change.Path)
                && change.Kind != SearchChangeKind.Removed)
                AddWork(state, new(change.Path, SearchWorkKind.Recursive));
        }
    }

    private void OnGrantsChanged()
    {
        var watchers = new List<IDisposable>();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var state in _roots.Values)
            {
                if (_access.CanAccess(state.Root))
                {
                    if (state.Status.Phase == SearchIndexPhase.Unavailable)
                        AddWork(state, new(state.Root, SearchWorkKind.Recursive, SearchWorkReason.AccessRecovery));
                }
                else
                {
                    if (state.Watcher != null) watchers.Add(state.Watcher);
                    state.Watcher = null; state.Pending.Clear(); state.Generations.Clear();
                    state.Deferred = false; state.Cooldown?.Cancel(); state.LastAttemptTick = null;
                    state.Status = state.Status with { Phase = SearchIndexPhase.Unavailable, Error = "目录授权已撤销。" };
                    Pulse(state);
                }
            }
        }
        foreach (var watcher in watchers) watcher.Dispose();
        foreach (var root in _access.AuthorizedRoots) if (Directory.Exists(root)) EnsureRoot(root);
    }

    private Generation Capture(RootState state)
    {
        var work = state.Pending.Take();
        var generation = new Generation(++state.NextGeneration, state.ObservedId, work, state.RestartWatcher);
        generation.FullScan = work.Any(item => item.Path == state.Root && item.Kind == SearchWorkKind.Recursive);
        state.RestartWatcher = false;
        if (generation.FullScan)
        {
            // This traversal covers all earlier work; acknowledge it only when the new traversal finishes.
            state.Generations.Clear();
            state.Deferred = false; state.Cooldown?.Cancel();
            state.RequireReconciliation = true;
        }
        state.Generations.Add(generation);
        return generation;
    }

    private async Task WorkAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            await foreach (var signal in _work.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            while (true)
            {
                RootState? state;
                bool merge;
                lock (_gate)
                {
                    state = TakeReady();
                    if (state == null) break;
                    state.Running = true;
                    merge = state.Generations.Count == 0 && state.Pending.Count > 0 && !state.Pending.HasUrgent;
                }
                if (merge) await Task.Delay(_configuration.EventMergeDelay, _timeProvider, ct).ConfigureAwait(false);
                Generation? generation;
                SearchWork? item;
                lock (_gate)
                {
                    DeferOverflow(state);
                    var active = state.Generations.Where(g => g.Urgent).OrderBy(g => g.FullScan).ThenBy(g => g.Number).FirstOrDefault();
                    if (state.Pending.Count > 0 && (state.Generations.Count == 0 || state.Pending.HasUrgentRoot
                        || (active == null || active.FullScan) && state.Pending.HasUrgent)) Capture(state);
                    generation = state.Generations.OrderByDescending(g => g.Urgent).ThenBy(g => g.FullScan).ThenBy(g => g.Number).FirstOrDefault();
                    item = generation?.Work.Dequeue();
                }
                if (generation != null && item != null)
                {
                    try
                    {
                        _access.EnsureAccess(state.Root);
                        if (!generation.Started) await StartGenerationAsync(state, generation, item, ct).ConfigureAwait(false);
                        if (item.Kind == SearchWorkKind.Entry)
                        {
                            var entries = new List<string>(256) { item.Path };
                            lock (_gate)
                                while (entries.Count < 256 && generation.Work.First is { Kind: SearchWorkKind.Entry })
                                    entries.Add(generation.Work.Dequeue().Path);
                            await UpdateEntriesAsync(state, generation, entries, ct).ConfigureAwait(false);
                        }
                        else if (CanDescend(state, item.Path))
                        {
                            var children = await ScanDirectoryAsync(state, generation, item.Path, item.Kind == SearchWorkKind.Recursive, ct).ConfigureAwait(false);
                            lock (_gate)
                                if (state.Generations.Contains(generation))
                                    generation.Work.AddChildren(children, item.Reason);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        SetError(state, item.Kind == SearchWorkKind.Entry ? Path.GetDirectoryName(item.Path) ?? state.Root : item.Path,
                            item.Kind == SearchWorkKind.Entry ? item.Path : null, ex);
                    }
                    try { await FinishStepAsync(state, generation, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        SetError(state, state.Root, null, ex);
                        lock (_gate) { state.Status = CompletedStatus(state); Pulse(state); }
                    }
                }
                lock (_gate)
                {
                    state.Running = false;
                    if (state.Pending.Count != 0 || state.Generations.Count != 0) Queue(state);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task StartGenerationAsync(RootState state, Generation generation, SearchWork first, CancellationToken ct)
    {
        generation.Started = true;
        state.LastAttemptTick = _timeProvider.GetTimestamp();
        if (!state.Started)
        {
            state.Checkpoint = state.RetiredObserved = await _catalog.GetCheckpointAsync(state.Root, ct).ConfigureAwait(false);
            state.Started = true;
        }
        if (generation.Restart)
        {
            state.Watcher?.Dispose();
            lock (_gate) { state.Watcher = null; state.Checkpoint = state.RetiredObserved = 0; }
        }
        if (generation.FullScan)
        {
            lock (_gate)
            {
                state.DirectoryErrors.Clear(); state.Status = state.Status with { IndexedEntries = 0 };
                _fullScans++; _scanReasons[first.Reason] = _scanReasons.GetValueOrDefault(first.Reason) + 1;
            }
        }
        if (state.Watcher == null)
        {
            try
            {
                var watcher = _changes.Watch(state.Root, state.Checkpoint, change => OnChange(state, change), generation.FullScan);
                var retained = false;
                lock (_gate)
                {
                    if (_access.CanAccess(state.Root) && !_disposed) { state.Watcher = watcher; retained = true; }
                }
                if (!retained) { watcher.Dispose(); _access.EnsureAccess(state.Root); }
                lock (_gate) state.WatcherError = null;
            }
            catch (Exception ex) { lock (_gate) state.WatcherError = "变更监听不可用: " + ex.Message; }
        }
    }

    private async Task FinishStepAsync(RootState state, Generation generation, CancellationToken ct)
    {
        ulong checkpoint;
        SearchIndexPhase phase;
        lock (_gate)
        {
            if (generation.Work.Count != 0 || !state.Generations.Contains(generation)) return;
            if (generation.FullScan)
            {
                state.RequireReconciliation = state.Deferred;
                if (_access.CanAccess(state.Root)) state.LastFullScanTick = _timeProvider.GetTimestamp();
            }
            state.RetiredObserved = Math.Max(state.RetiredObserved, generation.Observed);
            state.Generations.Remove(generation);
            var completed = CompletedStatus(state);
            if (completed.Phase == SearchIndexPhase.Ready && !state.RequireReconciliation && state.Generations.Count == 0)
                state.Checkpoint = Math.Max(state.Checkpoint, state.RetiredObserved);
            if (state.Pending.Count != 0 || state.Generations.Count != 0 || state.Deferred)
                completed = completed with { Phase = SearchIndexPhase.Building };
            checkpoint = state.Checkpoint; phase = completed.Phase;
        }
        await _catalog.SaveCheckpointAsync(state.Root, checkpoint, phase, ct).ConfigureAwait(false);
        lock (_gate)
        {
            state.Status = CompletedStatus(state);
            if (state.Pending.Count != 0 || state.Generations.Count != 0 || state.Deferred)
                state.Status = state.Status with { Phase = SearchIndexPhase.Building };
            else state.Running = false;
            Pulse(state);
        }
    }

    private SearchIndexStatus CompletedStatus(RootState state)
    {
        var error = state.WatcherError ?? state.DirectoryErrors.Values.LastOrDefault()?.Message;
        return state.Status with
        {
            Phase = !_access.CanAccess(state.Root) || !Directory.Exists(state.Root) ? SearchIndexPhase.Unavailable
                : error != null ? SearchIndexPhase.Partial : SearchIndexPhase.Ready,
            FailedDirectories = state.DirectoryErrors.Count, Error = error
        };
    }

    private void SetError(RootState state, string directory, string? entry, Exception ex)
    { lock (_gate) state.DirectoryErrors[directory] = new(directory, entry, ex.GetType().Name, ex.HResult, ex.Message); }

    private void RecordWrites(SearchWriteResult result)
    { lock (_gate) { _inserted += result.Inserted; _updated += result.Updated; _deleted += result.Deleted; } }

    internal SearchIndexStatistics GetStatistics()
    {
        lock (_gate) return new(_receivedEvents, _filteredEvents, _checkedEntries, _inserted, _updated, _deleted,
            _directoryScans, _fullScans, _pendingPeak, _pendingBytesPeak, _diagnosticsWrites,
            _scanReasons.ToDictionary(item => item.Key.ToString(), item => item.Value),
            _roots.Values.Count(state => state.Deferred));
    }

    public bool CanDescend(string root, string directory)
        => CanDescend(root, directory, includeHiddenFolders: false);

    private bool CanDescend(RootState state, string directory)
        => CanDescend(state.Root, directory, state.IncludeHiddenFolders);

    private bool CanDescend(string root, string directory, bool includeHiddenFolders)
    {
        if (root == directory) return true; // Explicit scopes can opt into otherwise excluded locations.
        if (!SearchPath.IsWithin(directory, root)) return false;
        foreach (var excluded in _configuration.ExcludedPaths)
            if (excluded != root && SearchPath.IsWithin(excluded, root) && SearchPath.IsWithin(directory, excluded)) return false;
        var userLibrary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library");
        if (userLibrary != root && SearchPath.IsWithin(userLibrary, root) && SearchPath.IsWithin(directory, userLibrary)) return false;
        var relative = directory[SearchPath.Prefix(root).Length..];
        var start = 0;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            start += segment.Length;
            if (ExcludedNames.Contains(segment) || (!includeHiddenFolders && segment.StartsWith('.'))
                || segment.EndsWith(".fkfinder-tmp", StringComparison.OrdinalIgnoreCase)) return false;
            if (Packages.Contains(Path.GetExtension(segment)))
            {
                var candidate = directory[..(directory.Length - relative.Length + start)];
                if (!OperatingSystem.IsMacOS() || !_access.CanAccess(candidate)
                    || Platforms.MacOS.MacSandboxNative.DirectoryPackage(candidate, out _) != false) return false;
            }
            start++;
        }
        return true;
    }

    private bool IsIndexArtifact(string path)
    {
        var database = SearchPath.Normalize(_configuration.DatabasePath);
        return path == database || path == database + "-wal" || path == database + "-shm" || path == database + "-journal"
            || SearchPath.IsWithin(path, DiagnosticsDirectory);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask != null) return new(_disposeTask);
            _disposed = true; _access.GrantsChanged -= OnGrantsChanged;
            foreach (var state in _roots.Values) { state.Cooldown?.Cancel(); Pulse(state, diagnostics: false); }
            return new(_disposeTask = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        try
        {
            _lifetime.Cancel(); _work.Writer.TryComplete();
            await _worker.ConfigureAwait(false);
            await Task.WhenAll(_roots.Values.Select(state => state.CooldownTask ?? Task.CompletedTask)).ConfigureAwait(false);
        }
        finally
        {
            _diagnosticRequests.Writer.TryComplete(); _diagnosticStop.Cancel();
            try { await _diagnosticWorker.ConfigureAwait(false); }
            finally
            {
                try { foreach (var state in _roots.Values) state.Watcher?.Dispose(); }
                finally { _diagnosticStop.Dispose(); _lifetime.Dispose(); }
            }
        }
    }
}

internal sealed record SearchIndexStatistics(long ReceivedEvents, long FilteredEvents, long CheckedEntries,
    long InsertedEntries, long UpdatedEntries, long DeletedEntries, long DirectoryScans, long FullScans,
    long PendingPeak, long PendingBytesPeak, long DiagnosticsWrites, IReadOnlyDictionary<string, long> FullScanReasons, int DeferredRoots);
