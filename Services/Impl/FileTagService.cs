using MacExplorer.Indexing;
using MacExplorer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed class FileTagService : IFileTagService, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IFinderTagQueryService? _finderTagQueryService;
    private readonly IFileTagStore? _store;
    private readonly IBackgroundTaskManager? _tasks;
    private readonly ILogger<FileTagService>? _logger;
    // All reads, pending edits and native writes share this gate. Never write a stale per-view snapshot.
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _retryLock = new(1, 1);
    private List<FileTag>? _definitions;
    private long _definitionsChanges = -1;
    private long _definitionsDataVersion = -1;

    public FileTagService(DatabaseConnectionFactory connectionFactory,
        IFinderTagQueryService? finderTagQueryService = null, ILogger<FileTagService>? logger = null,
        IFileTagStore? store = null, IBackgroundTaskManager? tasks = null)
    {
        _connection = connectionFactory.GetConnection();
        _finderTagQueryService = finderTagQueryService;
        _logger = logger;
        _store = store;
        _tasks = tasks;
        FileTagSchema.Ensure(_connection);
    }

    public event EventHandler? TagsChanged;
    public event EventHandler<TagRenamedEventArgs>? TagRenamed;

    public async Task<IReadOnlyList<FileTag>> GetSidebarTagsAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try { return ReadDefinitions().OrderByDescending(tag => tag.IsPinned).ToArray(); }
        finally { _connectionLock.Release(); }
    }

    private List<FileTag> ReadDefinitions()
    {
        // Indexing uses other connections. Invalidate for both local writes and
        // commits by those connections, rather than trusting only TagsChanged.
        using var version = _connection.CreateCommand();
        version.CommandText = "SELECT total_changes(), data_version FROM pragma_data_version";
        long changes, dataVersion;
        using (var versionReader = version.ExecuteReader())
        {
            versionReader.Read(); changes = versionReader.GetInt64(0); dataVersion = versionReader.GetInt64(1);
        }
        if (_definitions != null && changes == _definitionsChanges && dataVersion == _definitionsDataVersion)
            return _definitions;
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT d.name, d.color_id, d.is_pinned, d.sort_order, COUNT(f.file_path), d.parent_name, d.display_name_override
            FROM tag_definitions d LEFT JOIN file_tags f ON f.tag = d.name COLLATE NOCASE
            GROUP BY d.name ORDER BY d.sort_order, d.name COLLATE NOCASE
            """;
        var result = new List<FileTag>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var system = FileTagCatalog.TryGetFinderColor(name, out var color);
            var colorId = system ? color.ColorId : reader.GetInt32(1);
            result.Add(new FileTag(name, FileTagCatalog.ColorHex(colorId),
                system ? FileTagKind.FinderColor : FileTagKind.Custom,
                reader.GetInt32(4), colorId, reader.GetInt32(2) != 0, reader.GetInt32(3), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        _definitionsChanges = changes;
        _definitionsDataVersion = dataVersion;
        return _definitions = result;
    }

    public Task<FileTag> CreateTagAsync(string name, CancellationToken cancellationToken = default) =>
        CreateChildTagAsync(name, null, cancellationToken);

    public async Task<FileTag> CreateChildTagAsync(string name, string? parentName, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        FileTag created;
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            var definitions = ReadDefinitions();
            if (definitions.Any(t => HasName(t, name))) throw new InvalidOperationException(LocalizationText.Get("标签“{0}”已存在", name));
            var parent = ResolveParent(definitions, parentName);
            using (var transaction = _connection.BeginTransaction())
            {
                Execute("UPDATE tag_definitions SET sort_order = sort_order + 1 WHERE parent_name IS @parent COLLATE NOCASE", transaction,
                    ("@parent", (object?)parent ?? DBNull.Value));
                Execute("INSERT INTO tag_definitions(name, parent_name, sort_order) VALUES (@name, @parent, 0)", transaction,
                    ("@name", name), ("@parent", (object?)parent ?? DBNull.Value));
                transaction.Commit();
            }
            created = ReadDefinitions().Single(t => SameName(t.Name, name));
        }
        finally { _connectionLock.Release(); }
        TagsChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }

    public async Task MoveTagAsync(FileTag tag, string? parentName, int index, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            var definitions = ReadDefinitions();
            var current = definitions.Single(t => SameName(t.Name, tag.Name));
            var parent = ResolveParent(definitions, parentName);
            for (var ancestor = parent; ancestor != null; ancestor = definitions.Single(t => SameName(t.Name, ancestor)).ParentName)
                if (SameName(current.Name, ancestor)) throw new InvalidOperationException(LocalizationText.Get("不能将标签移入自身或子标签"));
            var siblings = definitions.Where(t => SameParent(t.ParentName, parent) && !SameName(t.Name, current.Name)).ToList();
            siblings.Insert(Math.Clamp(index, 0, siblings.Count), current);
            using var transaction = _connection.BeginTransaction();
            for (var i = 0; i < siblings.Count; i++)
                Execute("UPDATE tag_definitions SET parent_name = @parent, sort_order = @order WHERE name = @name COLLATE NOCASE", transaction,
                    ("@parent", (object?)parent ?? DBNull.Value), ("@order", i), ("@name", siblings[i].Name));
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameParent(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string? ResolveParent(IReadOnlyList<FileTag> definitions, string? parentName) => parentName == null ? null :
        definitions.FirstOrDefault(t => SameName(t.Name, parentName))?.Name
            ?? throw new InvalidOperationException(LocalizationText.Get("父标签不存在"));

    public async Task<IReadOnlyList<string>> FindFilePathsAsync(FileTag tag, CancellationToken cancellationToken = default)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (_finderTagQueryService != null)
        {
            try { paths.UnionWith(await _finderTagQueryService.FindFilePathsAsync(tag, cancellationToken)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new IOException(LocalizationText.Get("无法查询 Finder 标签“{0}”，请重试", tag.Name), ex); }
        }
        var changed = false;
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            paths.UnionWith(ReadPaths("SELECT file_path FROM file_tags WHERE tag = @tag COLLATE NOCASE", ("@tag", tag.Name)));
            paths.ExceptWith(ReadPaths("SELECT file_path FROM pending_tag_changes WHERE tag = @tag COLLATE NOCASE AND applied = 0", ("@tag", tag.Name)));
            if (_store != null)
            {
                foreach (var path in paths.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsSupportedPath(path) || !DirectoryAccess.Current.CanAccess(path)) continue;
                    IReadOnlyList<NativeFileTag> tags;
                    try { tags = OverlayPending(path, await _store.ReadAsync(path, cancellationToken)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Cannot verify tag membership for {Path}", path);
                        continue;
                    }
                    changed |= ReplaceIndexIfChangedLocked(path, tags);
                    if (!tags.Any(candidate => SameName(candidate.Name, tag.Name))) paths.Remove(path);
                }
            }
            return paths.Select(DirectoryAccess.Current.ResolvePath).Where(DirectoryAccess.Current.CanAccess).ToArray();
        }
        finally
        {
            _connectionLock.Release();
            if (changed) TagsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<IReadOnlyList<FileTag>> GetFileTagsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FileTag> result;
        bool changed;
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            var read = await RefreshFileTagsLockedAsync(filePath, cancellationToken);
            changed = read.Changed;
            var names = read.Names;
            result = ReadDefinitions().Where(t => names.Contains(t.Name)).ToArray();
        }
        finally { _connectionLock.Release(); }
        if (changed) TagsChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task<FileTagSelection> GetFileTagSelectionAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        var membership = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var path in filePaths.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await RefreshFileTagsLockedAsync(path, cancellationToken);
                changed |= read.Changed;
                foreach (var name in read.Names) membership[name] = membership.GetValueOrDefault(name) + 1;
            }
            return new(ReadDefinitions().OrderByDescending(tag => tag.IsPinned).ToArray(), membership);
        }
        finally
        {
            _connectionLock.Release();
            // Include completed reads even if a later file was cancelled.
            if (changed) TagsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<(HashSet<string> Names, bool Changed)> RefreshFileTagsLockedAsync(string path, CancellationToken cancellationToken)
    {
        var before = ReadIndexed(path);
        if (ReadPending(path).Count > 0) await SyncFileLockedAsync(path, cancellationToken);
        var tags = await ReadEffectiveLockedAsync(path, cancellationToken);
        var changed = ReplaceIndexIfChangedLocked(path, tags, before);
        return (tags.Select(t => FileTagCatalog.NormalizeName(t.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase), changed);
    }

    private bool ReplaceIndexIfChangedLocked(string path, IReadOnlyList<NativeFileTag> tags, IReadOnlyList<NativeFileTag>? indexed = null)
    {
        var before = (indexed ?? ReadIndexed(path)).GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().ColorId, StringComparer.OrdinalIgnoreCase);
        var changed = before.Count != tags.Count || tags.Any(tag =>
        {
            var name = FileTagCatalog.NormalizeName(tag.Name);
            var color = FileTagCatalog.TryGetFinderColor(name, out var system) ? system.ColorId : tag.ColorId;
            return !before.TryGetValue(name, out var indexedColor) || indexedColor != color;
        });
        if (changed) ReplaceIndex(path, tags, updateColors: true);
        return changed;
    }

    // Index import only. Pending edits always take precedence over incoming metadata.
    public async Task ReplaceFileTagsAsync(string filePath, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedPath(filePath)) return;
        bool changed;
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            var before = ReadIndexed(filePath).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var effective = OverlayPending(filePath, tags.Select(name => new NativeFileTag(FileTagCatalog.NormalizeName(name))));
            changed = !before.SetEquals(effective.Select(t => t.Name));
            if (changed) ReplaceIndex(filePath, effective);
        }
        finally { _connectionLock.Release(); }
        if (changed) TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<TagSyncResult> SetTagAsync(IReadOnlyList<string> filePaths, FileTag tag, bool applied, CancellationToken cancellationToken = default)
    {
        var name = ValidateName(tag.Name);
        var paths = filePaths.Where(IsSupportedPath).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length != filePaths.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException(LocalizationText.Get("仅支持本地文件和文件夹的标签"));
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            if (applied) EnsureDefinition(name, tag.ColorId, transaction);
            var color = ReadColor(name, transaction);
            foreach (var path in paths) QueueChange(path, name, applied, color, transaction);
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        TagsChanged?.Invoke(this, EventArgs.Empty);
        return await SyncPathsAsync(paths, cancellationToken);
    }

    public async Task<TagSyncResult> MoveTagMembershipAsync(IReadOnlyList<string> filePaths, FileTag sourceTag,
        FileTag targetTag, CancellationToken cancellationToken = default)
    {
        var source = ValidateName(sourceTag.Name);
        var target = ValidateName(targetTag.Name);
        var paths = filePaths.Where(IsSupportedPath).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length != filePaths.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException(LocalizationText.Get("仅支持本地文件和文件夹的标签"));
        if (SameName(source, target)) return new(0, 0);
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            EnsureDefinition(target, targetTag.ColorId, transaction);
            var color = ReadColor(target, transaction);
            foreach (var path in paths)
            {
                QueueChange(path, target, true, color, transaction);
                QueueChange(path, source, false, 0, transaction);
            }
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        TagsChanged?.Invoke(this, EventArgs.Empty);
        return await SyncPathsAsync(paths, cancellationToken);
    }

    public async Task RenameTagAsync(FileTag tag, string newName, CancellationToken cancellationToken = default)
    {
        if (tag.IsFinderColor || FileTagCatalog.TryGetFinderColor(tag.Name, out _))
        {
            await RenameSystemTagDisplayAsync(tag, newName, cancellationToken);
            return;
        }
        RequireCustom(tag);
        newName = ValidateName(newName);
        if (tag.Name == newName) return;
        if (FileTagCatalog.TryGetFinderColor(newName, out _)) throw new InvalidOperationException(LocalizationText.Get("系统颜色标签名称不能用于重命名"));
        var paths = await FindFilePathsAsync(tag, cancellationToken);
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (ReadDefinitions().Any(t => HasName(t, newName) && !SameName(t.Name, tag.Name)))
                throw new InvalidOperationException(LocalizationText.Get("标签“{0}”已存在", newName));
            paths = paths.Concat(ReadPaths("SELECT file_path FROM file_tags WHERE tag = @tag COLLATE NOCASE", ("@tag", tag.Name))).Distinct().ToArray();
            using var transaction = _connection.BeginTransaction();
            var colorId = ReadColor(tag.Name, transaction);
            Execute("UPDATE tag_definitions SET name = @new WHERE name = @old COLLATE NOCASE", transaction, ("@new", newName), ("@old", tag.Name));
            Execute("UPDATE tag_definitions SET parent_name = @new WHERE parent_name = @old COLLATE NOCASE", transaction, ("@new", newName), ("@old", tag.Name));
            foreach (var path in paths)
            {
                QueueChange(path, tag.Name, false, 0, transaction);
                QueueChange(path, newName, true, colorId, transaction);
            }
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        TagRenamed?.Invoke(this, new TagRenamedEventArgs(tag.Name, newName));
        TagsChanged?.Invoke(this, EventArgs.Empty);
        await SyncPathsAsync(paths, cancellationToken);
    }

    private async Task RenameSystemTagDisplayAsync(FileTag tag, string name, CancellationToken cancellationToken)
    {
        name = ValidateDisplayName(name);
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            var definitions = ReadDefinitions();
            var current = definitions.Single(t => SameName(t.Name, tag.Name));
            if (definitions.Any(t => !SameName(t.Name, current.Name) && HasName(t, name)))
                throw new InvalidOperationException(LocalizationText.Get("标签“{0}”已存在", name));
            if (current.DisplayName == name) return;
            Execute("UPDATE tag_definitions SET display_name_override = @display WHERE name = @name COLLATE NOCASE", null,
                ("@display", name == current.Name ? DBNull.Value : name), ("@name", current.Name));
        }
        finally { _connectionLock.Release(); }
        // Display-only edits never rewrite native tags, navigation identities or pending sync work.
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool HasName(FileTag tag, string name) => SameName(tag.Name, name)
        || string.Equals(tag.DisplayName, name, StringComparison.OrdinalIgnoreCase);

    public async Task DeleteTagAsync(FileTag tag, CancellationToken cancellationToken = default)
    {
        RequireCustom(tag);
        // Resolve native-only memberships before taking the database gate for the transaction.
        var snapshot = await GetSidebarTagsAsync(cancellationToken);
        var subtree = FileTagTree.Descendants(snapshot, tag.Name).Prepend(tag).ToArray();
        var pathsByTag = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var custom in subtree.Where(t => t.IsCustom))
            pathsByTag[custom.Name] = await FindFilePathsAsync(custom, cancellationToken);
        var allPaths = new HashSet<string>(StringComparer.Ordinal);
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            // A concurrent hierarchy edit must not leave a partially deleted tree.
            var currentTree = FileTagTree.Descendants(ReadDefinitions(), tag.Name).Prepend(tag).ToArray();
            if (!currentTree.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(subtree.Select(t => t.Name)))
                throw new InvalidOperationException(LocalizationText.Get("标签层级已变化，请重试"));
            var indexed = pathsByTag.ToDictionary(pair => pair.Key,
                pair => pair.Value.Concat(ReadPaths("SELECT file_path FROM file_tags WHERE tag = @tag COLLATE NOCASE", ("@tag", pair.Key))).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);
            using var transaction = _connection.BeginTransaction();
            foreach (var node in currentTree)
            {
                if (node.IsFinderColor)
                {
                    Execute("UPDATE tag_definitions SET parent_name = NULL, sort_order = (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM tag_definitions WHERE parent_name IS NULL) WHERE name = @tag", transaction, ("@tag", node.Name));
                    continue;
                }
                foreach (var path in indexed[node.Name])
                {
                    QueueChange(path, node.Name, false, 0, transaction);
                    allPaths.Add(path);
                }
                Execute("DELETE FROM tag_definitions WHERE name = @tag COLLATE NOCASE", transaction, ("@tag", node.Name));
            }
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        foreach (var name in pathsByTag.Keys) TagRenamed?.Invoke(this, new TagRenamedEventArgs(name, null));
        TagsChanged?.Invoke(this, EventArgs.Empty);
        await SyncPathsAsync(allPaths.ToArray(), cancellationToken);
    }

    public async Task SetTagColorAsync(FileTag tag, int colorId, CancellationToken cancellationToken = default)
    {
        RequireCustom(tag);
        if (colorId is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(colorId));
        var paths = await FindFilePathsAsync(tag, cancellationToken);
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            paths = paths.Concat(ReadPaths("SELECT file_path FROM file_tags WHERE tag = @tag COLLATE NOCASE", ("@tag", tag.Name))).Distinct().ToArray();
            using var transaction = _connection.BeginTransaction();
            Execute("UPDATE tag_definitions SET color_id = @color WHERE name = @tag COLLATE NOCASE", transaction, ("@color", colorId), ("@tag", tag.Name));
            foreach (var path in paths) QueueChange(path, tag.Name, true, colorId, transaction);
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        TagsChanged?.Invoke(this, EventArgs.Empty);
        await SyncPathsAsync(paths, cancellationToken);
    }

    public async Task SetTagPinnedAsync(FileTag tag, bool pinned, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            Execute("""
                UPDATE tag_definitions SET is_pinned = @pinned
                WHERE name = @tag COLLATE NOCASE
                """, null, ("@tag", tag.Name), ("@pinned", pinned ? 1 : 0));
        }
        finally { _connectionLock.Release(); }
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<TagSyncResult> RetryPendingAsync(CancellationToken cancellationToken = default)
    {
        await _retryLock.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<string> paths;
            await _connectionLock.WaitAsync(cancellationToken);
            try { paths = ReadPaths("SELECT DISTINCT file_path FROM pending_tag_changes"); }
            finally { _connectionLock.Release(); }
            return await SyncPathsAsync(paths, cancellationToken);
        }
        finally { _retryLock.Release(); }
    }

    private async Task<TagSyncResult> SyncPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var progress = paths.Count > 0 ? _tasks?.AddTask("同步 Finder 标签", BackgroundTaskKind.Generic,
            retryAction: async () => { await RetryPendingAsync(); }) : null;
        using var linkedCancellation = progress == null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, progress.Cts.Token);
        cancellationToken = linkedCancellation?.Token ?? cancellationToken;
        var synced = 0;
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                await _connectionLock.WaitAsync(cancellationToken);
                try { if (await SyncFileLockedAsync(paths[i], cancellationToken)) synced++; }
                finally { _connectionLock.Release(); }
                if (progress != null) _tasks?.UpdateProgress(progress.Id, (i + 1d) / paths.Count * 100, Path.GetFileName(paths[i]));
            }
            var result = new TagSyncResult(synced, paths.Count - synced);
            if (progress != null)
            {
                if (result.PendingFiles == 0) _tasks?.CompleteTask(progress.Id);
                else _tasks?.FailTask(progress.Id, $"{result.PendingFiles} 个文件标签已保存，等待同步 Finder（文件不可访问、无写入权限；远程及压缩包内文件暂不支持同步）");
            }
            TagsChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (progress != null) _tasks?.CancelTask(progress.Id);
            throw;
        }
    }

    private async Task<bool> SyncFileLockedAsync(string path, CancellationToken cancellationToken)
    {
        var pending = ReadPending(path);
        if (pending.Count == 0) return true;
        if (_store == null || !IsSupportedPath(path) || !DirectoryAccess.Current.CanAccess(path)) return false;
        try
        {
            var desired = OverlayPending(path, await _store.ReadAsync(path, cancellationToken));
            await _store.WriteAsync(path, desired, cancellationToken);
            var persisted = await _store.ReadAsync(path, cancellationToken);
            if (pending.Any(p => p.Applied
                ? !persisted.Any(t => SameName(t.Name, p.Name) && t.ColorId == p.Color)
                : persisted.Any(t => SameName(t.Name, p.Name)))) return false;
            using var transaction = _connection.BeginTransaction();
            Execute("DELETE FROM pending_tag_changes WHERE file_path = @path", transaction, ("@path", path));
            ReplaceIndex(path, persisted, transaction, updateColors: true);
            transaction.Commit();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Finder tags pending for {Path}", path);
            return false;
        }
    }

    private async Task<IReadOnlyList<NativeFileTag>> ReadEffectiveLockedAsync(string path, CancellationToken cancellationToken)
    {
        if (_store != null && IsSupportedPath(path) && DirectoryAccess.Current.CanAccess(path))
        {
            try { return OverlayPending(path, await _store.ReadAsync(path, cancellationToken)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger?.LogDebug(ex, "Cannot refresh tags for {Path}", path); }
        }
        return OverlayPending(path, ReadIndexed(path));
    }

    private List<NativeFileTag> OverlayPending(string path, IEnumerable<NativeFileTag> source)
    {
        var tags = source.Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => FileTagCatalog.NormalizeName(t.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var change in ReadPending(path))
        {
            if (change.Applied) tags[change.Name] = new NativeFileTag(change.Name, change.Color);
            else tags.Remove(change.Name);
        }
        return tags.Values.ToList();
    }

    private List<(string Name, bool Applied, int Color)> ReadPending(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT tag, applied, color_id FROM pending_tag_changes WHERE file_path = @path";
        command.Parameters.AddWithValue("@path", path);
        var result = new List<(string, bool, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(2)));
        return result;
    }

    private List<NativeFileTag> ReadIndexed(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT f.tag, COALESCE(d.color_id, 0) FROM file_tags f LEFT JOIN tag_definitions d ON f.tag = d.name COLLATE NOCASE WHERE f.file_path = @path";
        command.Parameters.AddWithValue("@path", path);
        var result = new List<NativeFileTag>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new NativeFileTag(reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    private void ReplaceIndex(string path, IReadOnlyList<NativeFileTag> tags, SqliteTransaction? transaction = null, bool updateColors = false)
    {
        using var owned = transaction == null ? _connection.BeginTransaction() : null;
        transaction ??= owned;
        Execute("DELETE FROM file_tags WHERE file_path = @path", transaction, ("@path", path));
        foreach (var tag in tags)
        {
            var name = FileTagCatalog.NormalizeName(tag.Name);
            if (name.Length == 0) continue;
            EnsureDefinition(name, tag.ColorId, transaction);
            if (updateColors && !FileTagCatalog.TryGetFinderColor(name, out _))
                Execute("UPDATE tag_definitions SET color_id = @color WHERE name = @tag COLLATE NOCASE AND NOT EXISTS (SELECT 1 FROM pending_tag_changes WHERE tag = @tag COLLATE NOCASE)",
                    transaction, ("@tag", name), ("@color", tag.ColorId));
            Execute("""
                INSERT OR IGNORE INTO file_tags(file_path, tag, is_system, created_at)
                VALUES (@path, (SELECT name FROM tag_definitions WHERE name = @tag COLLATE NOCASE), @system, @time)
                """, transaction, ("@path", path), ("@tag", name),
                ("@system", FileTagCatalog.TryGetFinderColor(name, out _) ? 1 : 0), ("@time", DateTime.UtcNow.Ticks));
        }
        owned?.Commit();
    }

    private void QueueChange(string path, string name, bool applied, int color, SqliteTransaction transaction)
    {
        Execute("""
            DELETE FROM file_tags WHERE file_path = @path AND tag = @tag COLLATE NOCASE;
            INSERT INTO pending_tag_changes(file_path, tag, applied, color_id) VALUES (@path, @tag, @applied, @color)
            ON CONFLICT(file_path, tag) DO UPDATE SET tag = excluded.tag, applied = excluded.applied, color_id = excluded.color_id;
            """, transaction, ("@path", path), ("@tag", name), ("@applied", applied ? 1 : 0), ("@color", color));
        if (applied)
            Execute("INSERT OR IGNORE INTO file_tags(file_path, tag, is_system, created_at) VALUES (@path, @tag, @system, @time)",
                transaction, ("@path", path), ("@tag", name), ("@system", FileTagCatalog.TryGetFinderColor(name, out _) ? 1 : 0), ("@time", DateTime.UtcNow.Ticks));
    }

    private void EnsureDefinition(string name, int color = 0, SqliteTransaction? transaction = null) =>
        Execute("INSERT OR IGNORE INTO tag_definitions(name, color_id) VALUES (@tag, @color)", transaction,
            ("@tag", name), ("@color", FileTagCatalog.TryGetFinderColor(name, out var system) ? system.ColorId : color));

    private int ReadColor(string name, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT color_id FROM tag_definitions WHERE name = @tag COLLATE NOCASE";
        command.Parameters.AddWithValue("@tag", name);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private List<string> ReadPaths(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public Task UpdatePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default) => TransferPathAsync(oldPath, newPath, true, cancellationToken);
    public Task CopyPathAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => TransferPathAsync(sourcePath, destinationPath, false, cancellationToken);

    private async Task TransferPathAsync(string source, string destination, bool move, CancellationToken cancellationToken)
    {
        if (!IsSupportedPath(source) || !IsSupportedPath(destination) || source == destination) return;
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var (table, columns) in new[] { ("file_tags", "tag, is_system, created_at"), ("pending_tag_changes", "tag, applied, color_id") })
            {
                Execute($"""
                    INSERT OR REPLACE INTO {table}(file_path, {columns})
                    SELECT @destination || substr(file_path, length(@source) + 1), {columns} FROM {table}
                    WHERE file_path = @source OR substr(file_path, 1, length(@prefix)) = @prefix;
                    """, transaction, ("@source", source), ("@destination", destination), ("@prefix", Prefix(source)));
                if (move) DeleteRows(table, source, transaction);
            }
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeletePathAsync(string path, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            DeleteRows("file_tags", path, transaction);
            DeleteRows("pending_tag_changes", path, transaction);
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteRows(string table, string path, SqliteTransaction transaction) => Execute(
        $"DELETE FROM {table} WHERE file_path = @path OR substr(file_path, 1, length(@prefix)) = @prefix",
        transaction, ("@path", path), ("@prefix", Prefix(path)));
    private int Execute(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters) => FileTagSchema.Execute(_connection, transaction, sql, parameters);
    private static string Prefix(string path) => path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    private static bool SameName(string left, string right) => string.Equals(FileTagCatalog.NormalizeName(left), FileTagCatalog.NormalizeName(right), StringComparison.OrdinalIgnoreCase);
    public static bool IsSupportedPath(string path) => Path.IsPathRooted(path) && !VirtualPath.IsRemotePath(path) && !path.Contains("!/");
    private static string ValidateName(string name)
    {
        return ValidateDisplayName(FileTagCatalog.NormalizeName(name));
    }
    private static string ValidateDisplayName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Any(char.IsControl)) throw new InvalidOperationException(LocalizationText.Get("标签名称不能为空或包含控制字符"));
        return name;
    }
    private static void RequireCustom(FileTag tag)
    {
        if (tag.IsFinderColor || FileTagCatalog.TryGetFinderColor(tag.Name, out _))
            throw new InvalidOperationException(LocalizationText.Get("tags.system-protected"));
    }
    public void Dispose() { _connection.Dispose(); _connectionLock.Dispose(); _retryLock.Dispose(); }
}
