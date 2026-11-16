namespace MacExplorer.Services.Search;

public sealed partial class SearchIndexer
{
    private SearchEntry ReadEntry(FileSystemInfo entry, string parent)
    {
        lock (_gate) _checkedEntries++;
        var attributes = entry.Attributes;
        var directory = attributes.HasFlag(FileAttributes.Directory);
        var link = attributes.HasFlag(FileAttributes.ReparsePoint);
        var name = entry.Name;
        return new(entry.FullName, name, parent, Path.GetExtension(name), SearchQuery.Fold(name),
            _pinyin.GetInitials(name), SearchQuery.Fold(parent), directory || link ? 0 : ((FileInfo)entry).Length,
            directory, attributes.HasFlag(FileAttributes.Hidden), entry.CreationTimeUtc.Ticks,
            entry.LastWriteTimeUtc.Ticks, link);
    }

    private async Task UpdateEntriesAsync(RootState state, Generation generation, List<string> paths, CancellationToken ct)
    {
        var entries = new List<SearchEntry>(paths.Count);
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
                var entry = ReadEntry(info, parent); entries.Add(entry);
                lock (_gate) if (state.DirectoryErrors.ContainsKey(parent)) reconcile.Add(parent);
                if (entry.IsDirectory && !entry.IsSymbolicLink && CanDescend(state, path)) reconcile.Add(path);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { reconcile.Add(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetError(state, parent, path, ex); }
        }
        try
        {
            foreach (var entry in entries) _access.EnsureAccess(entry.Path);
            RecordWrites(await _catalog.ApplyEntriesAsync(entries, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        { foreach (var parent in entries.Select(entry => entry.ParentPath).Distinct(StringComparer.Ordinal)) SetError(state, parent, null, ex); }
        foreach (var parent in reconcile) generation.Work.Enqueue(new(parent, SearchWorkKind.Directory));
    }

    private async Task<List<string>> ScanDirectoryAsync(RootState state, Generation generation,
        string current, bool recursive, CancellationToken ct)
    {
        var children = new List<string>();
        var batch = new List<SearchEntry>(256);
        var complete = true;
        lock (_gate) _directoryScans++;
        try
        {
            _access.EnsureAccess(current);
            var attributes = File.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.Directory)) throw new DirectoryNotFoundException(current);
            if (current != state.Root && attributes.HasFlag(FileAttributes.ReparsePoint)) return children;
            await using var scan = await _catalog.BeginDirectoryScanAsync(current, ct).ConfigureAwait(false);
            foreach (var item in new DirectoryInfo(current).EnumerateFileSystemInfos("*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                ct.ThrowIfCancellationRequested();
                _access.EnsureAccess(current);
                if (IsIndexArtifact(item.FullName) || item.Name.EndsWith(".fkfinder-tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_access.CanAccess(item.FullName))
                {
                    complete = false;
                    SetError(state, current, item.FullName, new UnauthorizedAccessException());
                    continue;
                }
                try
                {
                    var entry = ReadEntry(item, current);
                    batch.Add(entry);
                    if (recursive && entry.IsDirectory && !entry.IsSymbolicLink && CanDescend(state, entry.Path)) children.Add(entry.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { complete = false; SetError(state, current, item.FullName, ex); }
                if (batch.Count < 256) continue;
                _access.EnsureAccess(current);
                RecordWrites(await scan.RecordAsync(batch, ct).ConfigureAwait(false));
                PublishProgress(state, batch.Count); batch.Clear();
            }
            if (batch.Count > 0)
            {
                _access.EnsureAccess(current);
                RecordWrites(await scan.RecordAsync(batch, ct).ConfigureAwait(false));
                PublishProgress(state, batch.Count);
            }
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
                foreach (var path in state.DirectoryErrors.Keys.Where(path => SearchPath.IsWithin(path, current)).ToArray())
                    state.DirectoryErrors.Remove(path);
            var parent = Path.GetDirectoryName(current);
            if (parent != null && CanDescend(state, parent)) generation.Work.Enqueue(new(parent, SearchWorkKind.Directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetError(state, current, null, ex); }
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
