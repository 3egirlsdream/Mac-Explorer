using System.Threading.Channels;
using System.Text.Json;
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
    private readonly DirectoryAccess _access;
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
        _catalog = catalog;
        _configuration = configuration;
        _pinyin = pinyin;
        _changes = changes;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _access = DirectoryAccess.Current;
        _access.GrantsChanged += OnGrantsChanged;
        _worker = Task.Run(WorkAsync);
    }

    private sealed record DirectoryError(string Directory, string? Entry, string ExceptionType, int HResult, string Message);

    private sealed class RootState(string root)
    {
        public string Root { get; } = root;
        public SearchIndexStatus Status = new(root, SearchIndexPhase.Building);
        public readonly Dictionary<string, bool> Pending = new(StringComparer.Ordinal);
        public readonly Dictionary<string, DirectoryError> DirectoryErrors = new(StringComparer.Ordinal);
        public string? WatcherError;
        public TaskCompletionSource<bool> Changed = NewSignal();
        public IDisposable? Watcher;
        public bool Queued, Started, RestartWatcher;
        public bool IncludeHiddenFolders;
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

    public bool EnsureRoot(string directory, bool includeHiddenFolders = false)
    {
        _access.EnsureAccess(directory);
        var root = SearchPath.Normalize(directory);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var coverageChanged = false;
            var state = FindState(root);
            if (state == null)
            {
                state = new(root) { IncludeHiddenFolders = includeHiddenFolders };
                coverageChanged = true;
                _roots.Add(root, state);
                AddDirty(state, root, recursive: true);
            }
            else if (includeHiddenFolders && !state.IncludeHiddenFolders)
            {
                state.IncludeHiddenFolders = true;
                coverageChanged = true;
                AddDirty(state, state.Root, recursive: true);
            }
            // Partial coverage with a working watcher must not rescan on every
            // query. A failed watcher, however, has no events with which to recover.
            // Retry it after a cooldown, reconciling changes missed while unwatched.
            else if (!state.Queued &&
                     (state.Status.Phase == SearchIndexPhase.Unavailable || state.Watcher == null) &&
                     _timeProvider.GetUtcNow() - state.LastAttempt > TimeSpan.FromSeconds(30))
                AddDirty(state, state.Root, recursive: true);
            return coverageChanged;
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
        .Where(state => SearchPath.IsWithin(root, state.Root) && CanDescend(state, root))
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
            if (_disposed || !_access.CanAccess(state.Root) || !_access.CanAccess(change.Path) || IsIndexArtifact(change.Path)) return;
            state.ObservedId = change.RestartWatcher ? change.EventId : Math.Max(state.ObservedId, change.EventId);
            state.RestartWatcher |= change.RestartWatcher;
            if (change.MustRescan)
            {
                AddDirty(state, SearchPath.IsWithin(change.Path, state.Root) ? change.Path : state.Root, true);
                return;
            }
            if (!SearchPath.IsWithin(change.Path, state.Root)) return;
            var parent = Path.GetDirectoryName(change.Path);
            if (parent != null && CanDescend(state, parent)) AddDirty(state, parent, false);
            if (change.IsDirectory && CanDescend(state, change.Path)) AddDirty(state, change.Path, true);
        }
    }

    private void OnGrantsChanged()
    {
        var watchers = new List<IDisposable>();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var state in _roots.Values.Where(state => _access.CanAccess(state.Root)
                         && state.Status.Phase == SearchIndexPhase.Unavailable))
                AddDirty(state, state.Root, recursive: true);
            foreach (var state in _roots.Values.Where(state => !_access.CanAccess(state.Root)))
            {
                if (state.Watcher != null) watchers.Add(state.Watcher);
                state.Watcher = null;
                state.Pending.Clear();
                state.LastAttempt = DateTimeOffset.MinValue;
                state.Status = state.Status with { Phase = SearchIndexPhase.Unavailable, Error = "目录授权已撤销。" };
                Pulse(state);
            }
        }
        // Native callbacks take _gate; drain them after releasing it.
        foreach (var watcher in watchers) watcher.Dispose();
        // A recovered or moved grant may not have had a root state at startup.
        foreach (var root in _access.AuthorizedRoots)
            if (Directory.Exists(root)) EnsureRoot(root);
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
                if (fullScan) state.DirectoryErrors.Clear();
                string? batchError = null;
                var unavailable = false;
                try
                {
                    _access.EnsureAccess(state.Root);
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
                        try
                        {
                            var watcher = _changes.Watch(state.Root, state.Checkpoint, change => OnChange(state, change), fullScan);
                            var retained = false;
                            lock (_gate)
                            {
                                if (_access.CanAccess(state.Root)) { state.Watcher = watcher; retained = true; }
                            }
                            if (!retained) { watcher.Dispose(); _access.EnsureAccess(state.Root); }
                            state.WatcherError = null;
                        }
                        catch (Exception ex) { state.WatcherError = "变更监听不可用: " + ex.Message; }
                    }
                    if (fullScan)
                    {
                        lock (_gate) state.Status = state.Status with { IndexedEntries = 0 };
                    }
                    foreach (var item in pending)
                    {
                        if (!CanDescend(state, item.Key)) continue;
                        unavailable |= await ScanAsync(state, item.Key, item.Value, ct).ConfigureAwait(false);
                    }
                    // Only acknowledge the event IDs captured WITH this work batch, and only
                    // after all corresponding writes and directory reconciliation succeeded.
                    var completed = CompletedStatus(state, unavailable);
                    if (completed.Phase == SearchIndexPhase.Ready) state.Checkpoint = Math.Max(state.Checkpoint, observed);
                    await _catalog.SaveCheckpointAsync(state.Root, state.Checkpoint, completed.Phase, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (UnauthorizedAccessException ex) { batchError = ex.Message; unavailable = true; }
                catch (Exception ex) { batchError = ex.Message; }
                lock (_gate)
                {
                    unavailable |= !_access.CanAccess(state.Root);
                    state.Status = CompletedStatus(state, unavailable, batchError);
                    if (state.Pending.Count > 0)
                    {
                        state.Status = state.Status with { Phase = SearchIndexPhase.Building };
                        _work.Writer.TryWrite(state);
                    }
                    else state.Queued = false;
                    Pulse(state);
                }
                WriteDiagnostics();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private string DiagnosticsDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_configuration.DatabasePath))!, "Logs");

    private void WriteDiagnostics()
    {
        var path = Path.Combine(DiagnosticsDirectory, "search-index-status.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(new
                {
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    ProcessId = Environment.ProcessId,
                    Roots = _roots.Values.OrderBy(state => state.Root, StringComparer.Ordinal).Select(state => new
                    {
                        state.Root,
                        Phase = state.Status.Phase.ToString(),
                        state.Status.IndexedEntries,
                        state.Status.FailedDirectories,
                        state.Status.Error,
                        WatcherActive = state.Watcher != null,
                        state.WatcherError,
                        Failures = state.DirectoryErrors.Values.OrderBy(error => error.Directory, StringComparer.Ordinal).ToArray()
                    }).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            Directory.CreateDirectory(DiagnosticsDirectory);
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostic storage failures must not stop search or its index worker.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static SearchIndexStatus CompletedStatus(RootState state, bool unavailable, string? batchError = null)
    {
        var error = batchError ?? state.WatcherError ?? state.DirectoryErrors.Values.LastOrDefault()?.Message;
        return state.Status with
        {
            Phase = unavailable ? SearchIndexPhase.Unavailable : error != null ? SearchIndexPhase.Partial : SearchIndexPhase.Ready,
            FailedDirectories = state.DirectoryErrors.Count,
            Error = error
        };
    }

    private async Task<bool> ScanAsync(
        RootState state, string directory, bool recursive, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(directory);
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
                _access.EnsureAccess(current);
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
                    _access.EnsureAccess(current);
                    if (!_access.CanAccess(entry.FullName)) continue;
                    if (IsIndexArtifact(entry.FullName) || entry.Name.EndsWith(".fkfinder-tmp", StringComparison.OrdinalIgnoreCase)) continue;
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
                        if (recursive && isDirectory && !isLink && CanDescend(state, entry.FullName))
                            children.Add(entry.FullName);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        complete = false;
                        state.DirectoryErrors[current] = new(current, entry.FullName, ex.GetType().Name, ex.HResult, ex.Message);
                    }
                    if (batch.Count < 256) continue;
                    _access.EnsureAccess(current);
                    await _catalog.UpsertBatchAsync(batch, scan, ct).ConfigureAwait(false);
                    PublishProgress(state, batch.Count);
                    batch.Clear();
                }
                if (batch.Count > 0)
                {
                    _access.EnsureAccess(current);
                    await _catalog.UpsertBatchAsync(batch, scan, ct).ConfigureAwait(false);
                    PublishProgress(state, batch.Count);
                }
                _access.EnsureAccess(current);
                if (complete)
                {
                    await _catalog.CompleteDirectoryAsync(current, scan, ct).ConfigureAwait(false);
                    state.DirectoryErrors.Remove(current);
                }
            }
            catch (Exception ex) when (current != state.Root && (ex is DirectoryNotFoundException or FileNotFoundException))
            {
                // Rename/delete events commonly name the OLD directory. Confirm it via
                // the parent instead of reporting an expected disappearance as a failure.
                foreach (var failed in state.DirectoryErrors.Keys.Where(path => SearchPath.IsWithin(path, current)).ToArray())
                    state.DirectoryErrors.Remove(failed);
                var parent = Path.GetDirectoryName(current);
                if (parent != null)
                    lock (_gate) AddDirty(state, parent, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.DirectoryErrors[current] = new(current, null, ex.GetType().Name, ex.HResult, ex.Message);
                unavailable |= current == state.Root;
                // Keep existing rows on access failure. A confirmed parent enumeration removes
                // deleted/renamed subtrees; absence of permission is not proof of deletion.
            }
            foreach (var child in children) stack.Push(child);
        }
        return unavailable;
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
        return !relative.Split(Path.DirectorySeparatorChar).Any(segment => ExcludedNames.Contains(segment)
            || (!includeHiddenFolders && segment.StartsWith('.'))
            || segment.EndsWith(".fkfinder-tmp", StringComparison.OrdinalIgnoreCase)
            || Packages.Contains(Path.GetExtension(segment)));
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
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = true;
            _access.GrantsChanged -= OnGrantsChanged;
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
