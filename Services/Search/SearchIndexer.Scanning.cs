namespace MacExplorer.Services.Search;

public sealed partial class SearchIndexer
{
    private readonly List<SearchEntry> _scanBatch = new(16);
    internal Action<string>? EntryInspected { get; set; }

    private SearchEntry ReadEntry(FileSystemInfo entry, string parent, string parentKey)
    {
        Interlocked.Increment(ref _checkedEntries);
        var attributes = entry.Attributes;
        var directory = attributes.HasFlag(FileAttributes.Directory);
        var link = attributes.HasFlag(FileAttributes.ReparsePoint);
        var name = entry.Name;
        EntryInspected?.Invoke(name);
        return new(entry.FullName, name, parent, Path.GetExtension(name), SearchQuery.Fold(name),
            _pinyin.GetInitials(name), parentKey, directory || link ? 0 : ((FileInfo)entry).Length,
            directory, attributes.HasFlag(FileAttributes.Hidden), entry.CreationTimeUtc.Ticks,
            entry.LastWriteTimeUtc.Ticks, link);
    }

    private async Task UpdateEntriesAsync(RootState state, Generation generation, List<string> paths, CancellationToken ct)
    {
        var entries = new List<SearchEntry>(paths.Count);
        var parentKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var reconcile = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(path);
            if (parent == null || !CanDescend(state, parent) || IsIndexArtifact(path)) continue;
            try
            {
                _access.EnsureAccess(path);
                var attributes = File.GetAttributes(path);
                FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path) : new FileInfo(path);
                if (!parentKeys.TryGetValue(parent, out var key)) parentKeys[parent] = key = SearchQuery.Fold(parent);
                var entry = ReadEntry(info, parent, key); entries.Add(entry);
                lock (_gate) if (state.DirectoryErrors.ContainsKey(parent)) reconcile.Add(parent);
                if (entry.IsDirectory && !entry.IsSymbolicLink && CanDescend(state, path)) reconcile.Add(path);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { reconcile.Add(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetError(state, parent, path, ex); }
        }
        if (entries.Count > 0)
        {
            try
            {
                foreach (var entry in entries) _access.EnsureAccess(entry.Path);
                RecordWrites(await _catalog.ApplyEntriesAsync(entries, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            { foreach (var parent in entries.Select(entry => entry.ParentPath).Distinct(StringComparer.Ordinal)) SetError(state, parent, null, ex); }
        }
        foreach (var parent in reconcile) generation.Work.Enqueue(new(parent, SearchWorkKind.Directory));
    }

    private async Task<List<string>> ScanDirectoryAsync(RootState state, Generation generation,
        string current, bool recursive, CancellationToken ct)
    {
        var children = new List<string>();
        var batch = _scanBatch;
        batch.Clear();
        var complete = true;
        var checkedInDir = 0;
        var progressPending = 0;
        lock (_gate) _directoryScans++;
        try
        {
            _access.EnsureAccess(current);
            var attributes = File.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.Directory)) throw new DirectoryNotFoundException(current);
            if (current != state.Root && attributes.HasFlag(FileAttributes.ReparsePoint)) return children;
            await using var scan = await _catalog.BeginDirectoryScanAsync(current, ct).ConfigureAwait(false);
            string? parentKey = null;
            foreach (var item in new DirectoryInfo(current).EnumerateFileSystemInfos("*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                ct.ThrowIfCancellationRequested();
                _access.EnsureAccess(current);
                var fullName = item.FullName;
                var name = item.Name;
                if (IsIndexArtifact(fullName) || name.EndsWith(".fkfinder-tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_access.CanAccess(fullName))
                {
                    complete = false;
                    SetError(state, current, fullName, new UnauthorizedAccessException());
                    continue;
                }
                try
                {
                    checkedInDir++;
                    var entryAttributes = item.Attributes;
                    var directory = entryAttributes.HasFlag(FileAttributes.Directory);
                    var link = entryAttributes.HasFlag(FileAttributes.ReparsePoint);
                    var hidden = entryAttributes.HasFlag(FileAttributes.Hidden);
                    var size = directory || link ? 0 : ((FileInfo)item).Length;
                    var createdTicks = item.CreationTimeUtc.Ticks;
                    var modifiedTicks = item.LastWriteTimeUtc.Ticks;
                    if (EntryInspected is { } inspect)
                    {
                        if (checkedInDir > 0) { Interlocked.Add(ref _checkedEntries, checkedInDir); checkedInDir = 0; }
                        inspect(name);
                    }
                    if (!scan.TryMarkUnchanged(fullName, size, createdTicks, modifiedTicks, directory, hidden, link))
                    {
                        parentKey ??= SearchQuery.Fold(current);
                        batch.Add(new SearchEntry(fullName, name, current, Path.GetExtension(name), SearchQuery.Fold(name),
                            _pinyin.GetInitials(name), parentKey, size, directory, hidden, createdTicks, modifiedTicks, link));
                    }
                    progressPending++;
                    if (recursive && directory && !link && CanDescend(state, fullName)) children.Add(fullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { complete = false; SetError(state, current, fullName, ex); }
                if (batch.Count >= 256)
                {
                    _access.EnsureAccess(current);
                    RecordWrites(await scan.RecordAsync(batch, ct).ConfigureAwait(false));
                    PublishProgress(state, progressPending);
                    progressPending = 0;
                    batch.Clear();
                }
                else if (progressPending >= 256)
                {
                    PublishProgress(state, progressPending);
                    progressPending = 0;
                }
            }
            if (batch.Count > 0)
            {
                _access.EnsureAccess(current);
                RecordWrites(await scan.RecordAsync(batch, ct).ConfigureAwait(false));
            }
            if (progressPending > 0)
                PublishProgress(state, progressPending);
            _access.EnsureAccess(current);
            if (complete)
            {
                RecordWrites(await scan.CompleteAsync(ct).ConfigureAwait(false));
                lock (_gate) state.DirectoryErrors.Remove(current);
            }
        }
        catch (Exception ex) when (current != state.Root && ex is DirectoryNotFoundException or FileNotFoundException)
        {
            lock (_gate)
                foreach (var path in state.DirectoryErrors.Keys.Where(path => IsWithinNoAlloc(path, current)).ToArray())
                    state.DirectoryErrors.Remove(path);
            var parent = Path.GetDirectoryName(current);
            if (parent != null && CanDescend(state, parent)) generation.Work.Enqueue(new(parent, SearchWorkKind.Directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetError(state, current, null, ex); }
        finally
        {
            if (checkedInDir > 0) Interlocked.Add(ref _checkedEntries, checkedInDir);
            batch.Clear();
            if (batch.Capacity > 256) batch.Capacity = 16;
        }
        children.Sort(StringComparer.Ordinal);
        return children;
    }

    private void PublishProgress(RootState state, int count)
    {
        lock (_gate)
        {
            state.Status = state.Status with { IndexedEntries = state.Status.IndexedEntries + count };
            var now = _timeProvider.GetTimestamp();
            if (_timeProvider.GetElapsedTime(state.LastProgressTick, now) < TimeSpan.FromMilliseconds(200)) return;
            state.LastProgressTick = now; Pulse(state);
        }
    }
}
