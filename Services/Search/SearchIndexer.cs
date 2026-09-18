using System.Threading.Channels;
using MacExplorer.Indexing;

namespace MacExplorer.Services.Search;

/// <summary>
/// One bounded-batch scan worker; queries never launch recursive scans of their own.
/// Watches are started before enumeration. Dirty work is coalesced, never silently dropped.
/// </summary>
public sealed class SearchIndexer : IAsyncDisposable
{
    private readonly SearchCatalog _catalog;
    private readonly IndexConfiguration _configuration;
    private readonly IPinyinInitials _pinyin;
    private readonly ISearchChangeSource _changes;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.Ordinal);
    private readonly Channel<RootState> _work = Channel.CreateUnbounded<RootState>(new() { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private bool _disposed;
    private Task? _disposeTask;

    private static readonly HashSet<string> ExcludedNames = new(StringComparer.Ordinal)
    {
        ".git", ".svn", ".hg", "node_modules", ".cache", ".gradle", "DerivedData",
        ".Spotlight-V100", ".fseventsd", ".Trashes"
    };
    private static readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".app", ".photoslibrary", ".musiclibrary", ".photobooth", ".fcpbundle",
        ".bundle", ".framework", ".plugin", ".appex", ".kext", ".pvm", ".vmwarevm"
    };

    public SearchIndexer(SearchCatalog catalog, IndexConfiguration configuration, IPinyinInitials pinyin,
        ISearchChangeSource changes, TimeProvider? timeProvider = null)
    {
        _catalog = catalog;
        _configuration = configuration;
        _pinyin = pinyin;
        _changes = changes;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _worker = Task.Run(WorkAsync);
    }

    private sealed class RootState(string root)
    {
        public string Root { get; } = root;
        public SearchIndexStatus Status = new(root, SearchIndexPhase.Building);
        public readonly Dictionary<string, bool> Pending = new(StringComparer.Ordinal);
        public TaskCompletionSource<bool> Changed = NewSignal();
        public IDisposable? Watcher;
        public bool Queued, Started, RestartWatcher;
        public ulong ObservedId, Checkpoint;
        public DateTimeOffset LastAttempt;
        public long LastProgressTick;
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Pulse(RootState state)
    {
        var previous = state.Changed;
        state.Changed = NewSignal();
        previous.TrySetResult(true);
    }

    public void EnsureRoot(string directory)
    {
        var root = SearchPath.Normalize(directory);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var state = FindState(root);
            if (state == null)
            {
                state = new(root);
                _roots.Add(root, state);
                AddDirty(state, root, recursive: true);
            }
            // Partial coverage with a working watcher must not rescan on every
            // query. A failed watcher, however, has no events with which to recover.
            // Retry it after a cooldown, reconciling changes missed while unwatched.
            else if (!state.Queued &&
                     (state.Status.Phase == SearchIndexPhase.Unavailable || state.Watcher == null) &&
                     _timeProvider.GetUtcNow() - state.LastAttempt > TimeSpan.FromSeconds(30))
                AddDirty(state, state.Root, recursive: true);
        }
    }

    public void Refresh(string directory)
    {
        EnsureRoot(directory);
        lock (_gate)
        {
            var state = FindState(SearchPath.Normalize(directory))!;
            state.RestartWatcher = true;
            AddDirty(state, state.Root, recursive: true);
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

    // Reuse an ancestor watch only if its indexing policy actually covers the requested directory.
    private RootState? FindState(string root) => _roots.Values
        .Where(state => SearchPath.IsWithin(root, state.Root) && CanDescend(state.Root, root))
        .OrderByDescending(state => state.Root.Length).FirstOrDefault();

    private void AddDirty(RootState state, string directory, bool recursive)
    {
        if (!SearchPath.IsWithin(directory, state.Root)) return;
        if (state.Pending.Any(item => item.Value && SearchPath.IsWithin(directory, item.Key))) return;
        if (recursive)
            foreach (var key in state.Pending.Keys.Where(key => SearchPath.IsWithin(key, directory)).ToArray())
                state.Pending.Remove(key);
        state.Pending[directory] = recursive || state.Pending.GetValueOrDefault(directory);
        if (state.Pending.Count > 512)
        {
            state.Pending.Clear();
            state.Pending[state.Root] = true; // Backpressure: collapse to a rescan, never lose correctness.
        }
        state.Status = state.Status with { Phase = SearchIndexPhase.Building };
        Pulse(state);
        if (!state.Queued)
        {
            state.Queued = true;
            _work.Writer.TryWrite(state);
        }
    }

    private void OnChange(RootState state, SearchChange change)
    {
        lock (_gate)
        {
            if (_disposed || IsDatabaseFile(change.Path)) return;
            state.ObservedId = change.RestartWatcher ? change.EventId : Math.Max(state.ObservedId, change.EventId);
            state.RestartWatcher |= change.RestartWatcher;
            if (change.MustRescan)
            {
                AddDirty(state, SearchPath.IsWithin(change.Path, state.Root) ? change.Path : state.Root, true);
                return;
            }
            if (!SearchPath.IsWithin(change.Path, state.Root)) return;
            var parent = Path.GetDirectoryName(change.Path);
            if (parent != null && CanDescend(state.Root, parent)) AddDirty(state, parent, false);
            if (change.IsDirectory && CanDescend(state.Root, change.Path)) AddDirty(state, change.Path, true);
        }
    }

    private async Task WorkAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            await foreach (var state in _work.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(200, ct).ConfigureAwait(false); // Merge a burst of filesystem events.
                KeyValuePair<string, bool>[] pending;
                ulong observed;
                bool restart;
                lock (_gate)
                {
                    pending = state.Pending.ToArray();
                    state.Pending.Clear();
                    observed = state.ObservedId;
                    restart = state.RestartWatcher;
                    state.RestartWatcher = false;
                    state.LastAttempt = _timeProvider.GetUtcNow();
                }
                var fullScan = pending.Any(item => item.Key == state.Root && item.Value);
                var errors = fullScan ? 0 : state.Status.FailedDirectories;
                string? error = fullScan ? null : state.Status.Error;
                var unavailable = false;
                try
                {
                    if (!state.Started)
                    {
                        state.Checkpoint = await _catalog.GetCheckpointAsync(state.Root, ct).ConfigureAwait(false);
                        state.Started = true;
                    }
                    if (restart)
                    {
                        state.Watcher?.Dispose();
                        state.Watcher = null;
                        state.Checkpoint = 0; // A wrapped journal or a different mount invalidates the old cursor.
                    }
                    if (state.Watcher == null)
                    {
                        try { state.Watcher = _changes.Watch(state.Root, state.Checkpoint, change => OnChange(state, change), fullScan); }
                        catch (Exception ex) { errors++; error = "变更监听不可用: " + ex.Message; }
                    }
                    if (fullScan)
                    {
                        lock (_gate) state.Status = state.Status with { IndexedEntries = 0 };
                    }
                    foreach (var item in pending)
                    {
                        if (!CanDescend(state.Root, item.Key)) continue;
                        var result = await ScanAsync(state, item.Key, item.Value, ct).ConfigureAwait(false);
                        errors += result.Errors;
                        error = result.Error ?? error;
                        unavailable |= result.RootUnavailable;
                    }
                    // Only acknowledge the event IDs captured WITH this work batch, and only
                    // after all corresponding writes and directory reconciliation succeeded.
                    if (errors == 0 && !unavailable) state.Checkpoint = Math.Max(state.Checkpoint, observed);
                    var phase = unavailable ? SearchIndexPhase.Unavailable : errors > 0 ? SearchIndexPhase.Partial : SearchIndexPhase.Ready;
                    await _catalog.SaveCheckpointAsync(state.Root, state.Checkpoint, phase, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors++; error = ex.Message; }
                lock (_gate)
                {
                    var phase = unavailable ? SearchIndexPhase.Unavailable : errors > 0 ? SearchIndexPhase.Partial : SearchIndexPhase.Ready;
                    state.Status = state.Status with { Phase = phase, FailedDirectories = errors, Error = error };
                    if (state.Pending.Count > 0)
                    {
                        state.Status = state.Status with { Phase = SearchIndexPhase.Building };
                        _work.Writer.TryWrite(state);
                    }
                    else state.Queued = false;
                    Pulse(state);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<(int Errors, string? Error, bool RootUnavailable)> ScanAsync(
        RootState state, string directory, bool recursive, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(directory);
        var failures = 0;
        string? error = null;
        var unavailable = false;
        while (stack.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            var scan = Guid.NewGuid().ToString("N");
            var batch = new List<SearchEntry>(256);
            var children = new List<string>();
            var complete = true;
            try
            {
                var info = new DirectoryInfo(current);
                // Reading Attributes throws for an absent/unreadable root instead of silently
                // interpreting Directory.Exists(false) as an empty directory and deleting records.
                var attributes = File.GetAttributes(current);
                if (!attributes.HasFlag(FileAttributes.Directory)) throw new DirectoryNotFoundException(current);
                if (current != state.Root && attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0
                }))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsDatabaseFile(entry.FullName) || entry.Name.EndsWith(".fkfinder-tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var attrs = entry.Attributes;
                        var isDirectory = attrs.HasFlag(FileAttributes.Directory);
                        var isLink = attrs.HasFlag(FileAttributes.ReparsePoint);
                        var name = entry.Name;
                        batch.Add(new SearchEntry(entry.FullName, name, current, Path.GetExtension(name),
                            SearchQuery.Fold(name), _pinyin.GetInitials(name), SearchQuery.Fold(current),
                            isDirectory || isLink ? 0 : ((FileInfo)entry).Length, isDirectory,
                            attrs.HasFlag(FileAttributes.Hidden), entry.CreationTimeUtc.Ticks, entry.LastWriteTimeUtc.Ticks, isLink));
                        if (recursive && isDirectory && !isLink && CanDescend(state.Root, entry.FullName))
                            children.Add(entry.FullName);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        complete = false;
                        error = ex.Message;
                    }
                    if (batch.Count < 256) continue;
                    await _catalog.UpsertBatchAsync(batch, scan, ct).ConfigureAwait(false);
                    PublishProgress(state, batch.Count);
                    batch.Clear();
                }
                if (batch.Count > 0)
                {
                    await _catalog.UpsertBatchAsync(batch, scan, ct).ConfigureAwait(false);
                    PublishProgress(state, batch.Count);
                }
                if (complete) await _catalog.CompleteDirectoryAsync(current, scan, ct).ConfigureAwait(false);
                else failures++;
            }
            catch (Exception ex) when (current != state.Root && (ex is DirectoryNotFoundException or FileNotFoundException))
            {
                // Rename/delete events commonly name the OLD directory. Confirm it via
                // the parent instead of reporting an expected disappearance as a failure.
                var parent = Path.GetDirectoryName(current);
                if (parent != null)
                    lock (_gate) AddDirty(state, parent, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                error = ex.Message;
                unavailable |= current == state.Root;
                // Keep existing rows on access failure. A confirmed parent enumeration removes
                // deleted/renamed subtrees; absence of permission is not proof of deletion.
            }
            foreach (var child in children) stack.Push(child);
        }
        return (failures, error, unavailable);
    }

    private void PublishProgress(RootState state, int count)
    {
        lock (_gate)
        {
            state.Status = state.Status with { IndexedEntries = state.Status.IndexedEntries + count };
            var now = Environment.TickCount64;
            if (now - state.LastProgressTick < 200) return;
            state.LastProgressTick = now;
            Pulse(state);
        }
    }

    public bool CanDescend(string root, string directory)
    {
        if (root == directory) return true; // Explicit scopes can opt into otherwise excluded locations.
        if (!SearchPath.IsWithin(directory, root)) return false;
        foreach (var excluded in _configuration.ExcludedPaths)
            if (excluded != root && SearchPath.IsWithin(excluded, root) && SearchPath.IsWithin(directory, excluded)) return false;
        var userLibrary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library");
        if (userLibrary != root && SearchPath.IsWithin(userLibrary, root) && SearchPath.IsWithin(directory, userLibrary)) return false;
        var relative = directory[SearchPath.Prefix(root).Length..];
        return !relative.Split(Path.DirectorySeparatorChar).Any(segment => ExcludedNames.Contains(segment)
            || segment.EndsWith(".fkfinder-tmp", StringComparison.OrdinalIgnoreCase)
            || Packages.Contains(Path.GetExtension(segment)));
    }

    private bool IsDatabaseFile(string path)
    {
        var database = SearchPath.Normalize(_configuration.DatabasePath);
        return path == database || path == database + "-wal" || path == database + "-shm" || path == database + "-journal";
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = true;
            foreach (var state in _roots.Values) Pulse(state);
            return new ValueTask(_disposeTask = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Publish the shared task before cancellation can invoke callbacks. All
        // callers must join the worker before they can dispose the database.
        await Task.Yield();
        try
        {
            _lifetime.Cancel();
            _work.Writer.TryComplete();
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            // Never hold _gate while draining native callbacks that also use it.
            try { foreach (var state in _roots.Values) state.Watcher?.Dispose(); }
            finally { _lifetime.Dispose(); }
        }
    }
}
