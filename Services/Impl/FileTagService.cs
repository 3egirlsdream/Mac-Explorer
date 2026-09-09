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
        try { return ReadDefinitions(); }
        finally { _connectionLock.Release(); }
    }

    private List<FileTag> ReadDefinitions()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT d.name, d.color_id, d.is_pinned, d.sort_order, COUNT(f.file_path)
            FROM tag_definitions d LEFT JOIN file_tags f ON f.tag = d.name COLLATE NOCASE
            GROUP BY d.name ORDER BY d.is_pinned DESC, d.sort_order, d.name COLLATE NOCASE
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
                reader.GetInt32(4), colorId, reader.GetInt32(2) != 0, reader.GetInt32(3)));
        }
        return result.OrderByDescending(t => t.IsPinned).ThenBy(t => t.IsPinned ? t.SortOrder : 0)
            .ThenBy(t => t.IsFinderColor ? 0 : 1)
            .ThenBy(t => t.IsFinderColor ? FileTagCatalog.FinderColors.ToList().FindIndex(c => c.Name == t.Name) : 0)
            .ThenByDescending(t => t.IsPinned ? 0 : t.ItemCount).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<FileTag> CreateTagAsync(string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        FileTag created;
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (ReadDefinitions().Any(t => SameName(t.Name, name))) throw new InvalidOperationException($"标签“{name}”已存在");
            EnsureDefinition(name);
            created = ReadDefinitions().Single(t => SameName(t.Name, name));
        }
        finally { _connectionLock.Release(); }
        TagsChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }

    public async Task<IReadOnlyList<string>> FindFilePathsAsync(FileTag tag, CancellationToken cancellationToken = default)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (_finderTagQueryService != null)
        {
            try { paths.UnionWith(await _finderTagQueryService.FindFilePathsAsync(tag, cancellationToken)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new IOException($"无法查询 Finder 标签“{tag.Name}”，请重试", ex); }
        }
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            paths.UnionWith(ReadPaths("SELECT file_path FROM file_tags WHERE tag = @tag COLLATE NOCASE", ("@tag", tag.Name)));
            paths.ExceptWith(ReadPaths("SELECT file_path FROM pending_tag_changes WHERE tag = @tag COLLATE NOCASE AND applied = 0", ("@tag", tag.Name)));
            return paths.ToArray();
        }
        finally { _connectionLock.Release(); }
    }

    public async Task<IReadOnlyList<FileTag>> GetFileTagsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FileTag> result;
        bool changed;
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            var before = ReadIndexed(filePath).Select(t => (t.Name.ToUpperInvariant(), t.ColorId)).ToHashSet();
            if (ReadPending(filePath).Count > 0) await SyncFileLockedAsync(filePath, cancellationToken);
            var tags = await ReadEffectiveLockedAsync(filePath, cancellationToken);
            ReplaceIndex(filePath, tags, updateColors: true);
            changed = !before.SetEquals(tags.Select(t => (FileTagCatalog.NormalizeName(t.Name).ToUpperInvariant(), t.ColorId)));
            var names = tags.Select(t => FileTagCatalog.NormalizeName(t.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result = ReadDefinitions().Where(t => names.Contains(t.Name)).ToArray();
        }
        finally { _connectionLock.Release(); }
        if (changed) TagsChanged?.Invoke(this, EventArgs.Empty);
        return result;
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
            ReplaceIndex(filePath, effective);
        }
        finally { _connectionLock.Release(); }
        if (changed) TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<TagSyncResult> SetTagAsync(IReadOnlyList<string> filePaths, FileTag tag, bool applied, CancellationToken cancellationToken = default)
    {
        var name = ValidateName(tag.Name);
        var paths = filePaths.Where(IsSupportedPath).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length != filePaths.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("仅支持本地文件和文件夹的标签");
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

    public async Task RenameTagAsync(FileTag tag, string newName, CancellationToken cancellationToken = default)
    {
        RequireCustom(tag);
        newName = ValidateName(newName);
        if (tag.Name == newName) return;
        if (FileTagCatalog.TryGetFinderColor(newName, out _)) throw new InvalidOperationException("系统颜色标签名称不能用于重命名");
        var paths = await FindFilePathsAsync(tag, cancellationToken);
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (ReadDefinitions().Any(t => SameName(t.Name, newName) && !SameName(t.Name, tag.Name)))
                throw new InvalidOperationException($"标签“{newName}”已存在");
            paths = paths.Concat(ReadPaths("SELECT file_path FROM file_tags WHERE tag = @tag COLLATE NOCASE", ("@tag", tag.Name))).Distinct().ToArray();
            using var transaction = _connection.BeginTransaction();
            var colorId = ReadColor(tag.Name, transaction);
            Execute("UPDATE tag_definitions SET name = @new WHERE name = @old COLLATE NOCASE", transaction, ("@new", newName), ("@old", tag.Name));
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

    public async Task DeleteTagAsync(FileTag tag, CancellationToken cancellationToken = default)
    {
        RequireCustom(tag);
        var paths = await FindFilePathsAsync(tag, cancellationToken);
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            paths = paths.Concat(ReadPaths("SELECT file_path FROM file_tags WHERE tag = @tag COLLATE NOCASE", ("@tag", tag.Name))).Distinct().ToArray();
            using var transaction = _connection.BeginTransaction();
            foreach (var path in paths) QueueChange(path, tag.Name, false, 0, transaction);
            Execute("DELETE FROM tag_definitions WHERE name = @tag COLLATE NOCASE", transaction, ("@tag", tag.Name));
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        TagRenamed?.Invoke(this, new TagRenamedEventArgs(tag.Name, null));
        TagsChanged?.Invoke(this, EventArgs.Empty);
        await SyncPathsAsync(paths, cancellationToken);
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
                UPDATE tag_definitions SET is_pinned = @pinned,
                    sort_order = CASE WHEN @pinned = 1 THEN (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM tag_definitions) ELSE sort_order END
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
        if (_store == null || !IsSupportedPath(path)) return false;
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
        if (_store != null && IsSupportedPath(path))
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
        name = FileTagCatalog.NormalizeName(name);
        if (name.Length == 0 || name.Any(char.IsControl)) throw new InvalidOperationException("标签名称不能为空或包含控制字符");
        return name;
    }
    private static void RequireCustom(FileTag tag)
    {
        if (tag.IsFinderColor || FileTagCatalog.TryGetFinderColor(tag.Name, out _))
            throw new InvalidOperationException("系统颜色标签不能重命名、删除或改色");
    }
    public void Dispose() { _connection.Dispose(); _connectionLock.Dispose(); _retryLock.Dispose(); }
}
