using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

internal readonly record struct SearchWriteResult(long Inserted = 0, long Updated = 0, long Deleted = 0)
{
    public static SearchWriteResult operator +(SearchWriteResult a, SearchWriteResult b) =>
        new(a.Inserted + b.Inserted, a.Updated + b.Updated, a.Deleted + b.Deleted);
}

public sealed partial class SearchCatalog
{
    internal long ScanMemoryLimitBytes { get; set; } = 8 * 1024 * 1024;
    internal string? ScanScratchDirectory { get; set; }
    // Directory scans own _writer, so their bounded membership buffer can be reused without retaining paths.
    private readonly HashSet<string> _scanPaths = new(StringComparer.Ordinal);

    private const string UpsertSql = """
        INSERT INTO search_entries(path,name,parent_id,extension,extension_key,name_key,initials_key,
            size,is_directory,is_hidden,created_ticks,modified_ticks,is_symbolic_link,scan_id)
        VALUES(@path,@name,@parentId,@ext,@extKey,@nameKey,@initials,@size,@dir,@hidden,@created,@modified,@link,'')
        ON CONFLICT(path) DO UPDATE SET name=excluded.name,parent_id=excluded.parent_id,
            extension=excluded.extension,extension_key=excluded.extension_key,name_key=excluded.name_key,
            initials_key=excluded.initials_key,size=excluded.size,is_directory=excluded.is_directory,
            is_hidden=excluded.is_hidden,created_ticks=excluded.created_ticks,
            modified_ticks=excluded.modified_ticks,is_symbolic_link=excluded.is_symbolic_link
        WHERE search_entries.name IS NOT excluded.name OR search_entries.parent_id IS NOT excluded.parent_id
            OR search_entries.extension IS NOT excluded.extension OR search_entries.extension_key IS NOT excluded.extension_key
            OR search_entries.name_key IS NOT excluded.name_key OR search_entries.initials_key IS NOT excluded.initials_key
            OR search_entries.size IS NOT excluded.size OR search_entries.is_directory IS NOT excluded.is_directory
            OR search_entries.is_hidden IS NOT excluded.is_hidden OR search_entries.created_ticks IS NOT excluded.created_ticks
            OR search_entries.modified_ticks IS NOT excluded.modified_ticks OR search_entries.is_symbolic_link IS NOT excluded.is_symbolic_link
        """;

    internal Task<SearchWriteResult> ApplyEntriesAsync(IReadOnlyList<SearchEntry> entries, CancellationToken ct = default) =>
        RunAsync(true, (connection, _) => ApplyEntries(connection, entries, ct), ct);

    internal async Task<DirectoryScan> BeginDirectoryScanAsync(string directory, CancellationToken ct = default)
    {
        await _initialized.Value.WaitAsync(ct).ConfigureAwait(false);
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        SqliteConnection? connection = null;
        try
        {
            connection = _connections.GetConnection();
            return await Task.Run(() => new DirectoryScan(this, connection, directory, ct), ct).ConfigureAwait(false);
        }
        catch { connection?.Dispose(); _writer.Release(); throw; }
    }

    private static SearchWriteResult ApplyEntries(SqliteConnection connection, IReadOnlyList<SearchEntry> entries, CancellationToken ct,
        bool newDirectory = false)
    {
        if (entries.Count == 0) return default;
        var existing = new Dictionary<string, SearchEntry>(StringComparer.Ordinal);
        if (!newDirectory)
        {
            using var lookup = connection.CreateCommand();
            var parameters = entries.Select((entry, i) =>
            {
                var name = "@p" + i; lookup.Parameters.AddWithValue(name, entry.Path); return name;
            });
            lookup.CommandText = $"SELECT {Columns},e.parent_id FROM search_entry_details e WHERE e.path IN ({string.Join(',', parameters)})";
            var parentValues = new Dictionary<long, (string Path, string Key)>();
            using var reader = lookup.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(13);
                if (!parentValues.TryGetValue(id, out var values))
                    parentValues[id] = values = (reader.GetString(2), reader.GetString(6));
                var entry = ReadEntry(reader, values.Path, values.Key); existing[entry.Path] = entry;
            }
        }
        if (entries.All(entry => existing.TryGetValue(entry.Path, out var old) && SameMetadata(old, entry))) return default;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;
        foreach (var name in new[] { "@path", "@name", "@ext", "@extKey", "@nameKey", "@initials" }) command.Parameters.Add(name, SqliteType.Text);
        foreach (var name in new[] { "@parentId", "@size", "@dir", "@hidden", "@created", "@modified", "@link" }) command.Parameters.Add(name, SqliteType.Integer);
        command.Prepare();
        using var parent = connection.CreateCommand();
        parent.Transaction = transaction;
        parent.CommandText = """
            INSERT INTO search_directories(path,parent_key) VALUES(@path,@key)
            ON CONFLICT(path) DO UPDATE SET parent_key=excluded.parent_key
            WHERE search_directories.parent_key IS NOT excluded.parent_key;
            SELECT id FROM search_directories WHERE path=@path COLLATE BINARY;
            """;
        parent.Parameters.Add("@path", SqliteType.Text); parent.Parameters.Add("@key", SqliteType.Text);
        var parents = new Dictionary<string, long>(StringComparer.Ordinal);
        var result = new SearchWriteResult();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            existing.TryGetValue(entry.Path, out var old);
            if (old != null && SameMetadata(old, entry)) continue;
            if (!parents.TryGetValue(entry.ParentPath, out var parentId))
            {
                parent.Parameters["@path"].Value = entry.ParentPath; parent.Parameters["@key"].Value = entry.ParentKey;
                parents[entry.ParentPath] = parentId = (long)parent.ExecuteScalar()!;
            }
            if (old is { IsDirectory: true } && (!entry.IsDirectory || (!old.IsSymbolicLink && entry.IsSymbolicLink)))
                result += new SearchWriteResult(Deleted: DeleteScope(connection, transaction, entry.Path, descendantsOnly: true));
            command.Parameters["@path"].Value = entry.Path; command.Parameters["@name"].Value = entry.Name;
            command.Parameters["@parentId"].Value = parentId; command.Parameters["@ext"].Value = entry.Extension;
            command.Parameters["@extKey"].Value = SearchQuery.Fold(entry.Extension.TrimStart('.'));
            command.Parameters["@nameKey"].Value = entry.NameKey; command.Parameters["@initials"].Value = entry.InitialsKey;
            command.Parameters["@size"].Value = entry.Size; command.Parameters["@dir"].Value = entry.IsDirectory;
            command.Parameters["@hidden"].Value = entry.IsHidden; command.Parameters["@created"].Value = entry.CreatedTicks;
            command.Parameters["@modified"].Value = entry.ModifiedTicks; command.Parameters["@link"].Value = entry.IsSymbolicLink;
            if (command.ExecuteNonQuery() != 0)
                result += old != null ? new SearchWriteResult(Updated: 1) : new SearchWriteResult(Inserted: 1);
            existing[entry.Path] = entry;
        }
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
        return result;
    }

    private static bool SameMetadata(SearchEntry a, SearchEntry b) =>
        a.Name == b.Name && a.ParentPath == b.ParentPath && a.Extension == b.Extension
        && a.NameKey == b.NameKey && a.InitialsKey == b.InitialsKey && a.ParentKey == b.ParentKey
        && a.Size == b.Size && a.IsDirectory == b.IsDirectory && a.IsHidden == b.IsHidden
        && a.CreatedTicks == b.CreatedTicks && a.ModifiedTicks == b.ModifiedTicks && a.IsSymbolicLink == b.IsSymbolicLink;

    private static long DeleteScope(SqliteConnection connection, SqliteTransaction transaction, string path, bool descendantsOnly = false)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM search_entries WHERE " + (descendantsOnly
            ? SqlitePathScope.Descendants("path") : ScopePredicate("path"));
        SqlitePathScope.Bind(command, path);
        return command.ExecuteNonQuery();
    }

    internal sealed class DirectoryScan : IAsyncDisposable
    {
        private readonly SearchCatalog _owner;
        private readonly SqliteConnection _connection;
        private readonly RemainingPaths _remaining;
        private readonly bool _newDirectory;
        private bool _disposed, _completed;
        internal bool Spilled => _remaining.Spilled;

        internal DirectoryScan(SearchCatalog owner, SqliteConnection connection, string directory, CancellationToken ct)
        {
            _owner = owner; _connection = connection;
            _remaining = new(owner.ScanMemoryLimitBytes, owner.ScanScratchDirectory ?? Path.Combine(
                Path.GetDirectoryName(connection.DataSource)!, "Logs", "scan-scratch"), owner._scanPaths);
            try
            {
                using var registration = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT path FROM search_entries WHERE parent_id=(SELECT id FROM search_directories WHERE path=@parent COLLATE BINARY)";
                command.Parameters.AddWithValue("@parent", directory);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    _remaining.Add(reader.GetString(0));
                }
                _newDirectory = _remaining.InitialCount == 0;
            }
            catch (SqliteException) when (ct.IsCancellationRequested)
            { _remaining.Dispose(); throw new OperationCanceledException(ct); }
            catch { _remaining.Dispose(); throw; }
        }

        internal Task<SearchWriteResult> RecordAsync(IReadOnlyList<SearchEntry> entries, CancellationToken ct = default) =>
            ExecuteAsync(() =>
            {
                _remaining.Seen(entries.Select(entry => entry.Path));
                return ApplyEntries(_connection, entries, ct, _newDirectory);
            }, ct);

        internal Task<SearchWriteResult> CompleteAsync(CancellationToken ct = default) => ExecuteAsync(() =>
        {
            if (_remaining.Error != null)
                throw new IOException(LocalizationText.Get("search.reconciliation-unavailable"), _remaining.Error);
            if (!_remaining.HasRemaining) { _completed = true; return default; }
            using var transaction = _connection.BeginTransaction();
            long deleted = 0;
            foreach (var path in _remaining.Read())
            {
                ct.ThrowIfCancellationRequested();
                deleted += DeleteScope(_connection, transaction, path);
            }
            ct.ThrowIfCancellationRequested();
            transaction.Commit(); _completed = true;
            return new SearchWriteResult(Deleted: deleted);
        }, ct);

        private async Task<SearchWriteResult> ExecuteAsync(Func<SearchWriteResult> action, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed) throw new InvalidOperationException("Directory reconciliation already completed.");
            using var registration = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(_connection.Handle));
            try { return await Task.Run(() => { ct.ThrowIfCancellationRequested(); return action(); }, ct).ConfigureAwait(false); }
            catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                try { _remaining.Dispose(); }
                finally { _connection.Dispose(); _owner._writer.Release(); }
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RemainingPaths(long limit, string directory, HashSet<string> paths) : IDisposable
    {
        private readonly HashSet<string> _paths = paths;
        private readonly List<string> _pending = new(256);
        private long _stringBytes;
        private SqliteConnection? _disk;
        private string? _path;
        public Exception? Error { get; private set; }
        public long InitialCount { get; private set; }
        public bool Spilled => _disk != null;
        public bool HasRemaining
        {
            get
            {
                if (_disk == null) return _paths.Count != 0;
                Flush();
                using var command = _disk.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM remaining)";
                return (long)command.ExecuteScalar()! != 0;
            }
        }

        public void Add(string path) => Try(() =>
        {
            InitialCount++;
            if (_disk == null)
            {
                if (_paths.Add(path)) _stringBytes += 32L + path.Length * 2L;
                if (_stringBytes + _paths.EnsureCapacity(0) * 20L + 48 <= limit) return;
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, $"remaining-{Environment.ProcessId}-{Guid.NewGuid():N}.db");
                _disk = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
                _disk.Open();
                using var setup = _disk.CreateCommand();
                setup.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-2048; CREATE TABLE remaining(path TEXT PRIMARY KEY COLLATE BINARY) WITHOUT ROWID";
                setup.ExecuteNonQuery();
                using var transaction = _disk.BeginTransaction();
                using var insert = _disk.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO remaining VALUES(@path)"; insert.Parameters.Add("@path", SqliteType.Text);
                foreach (var value in _paths) { insert.Parameters[0].Value = value; insert.ExecuteNonQuery(); }
                transaction.Commit(); _paths.Clear(); _paths.TrimExcess();
            }
            else
            {
                _pending.Add(path);
                if (_pending.Count == 256) Flush();
            }
        });

        public void Seen(IEnumerable<string> paths) => Try(() =>
        {
            if (_disk == null) { foreach (var path in paths) _paths.Remove(path); return; }
            Flush();
            using var transaction = _disk.BeginTransaction();
            using var command = _disk.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM remaining WHERE path=@path COLLATE BINARY"; command.Parameters.Add("@path", SqliteType.Text);
            foreach (var path in paths) { command.Parameters[0].Value = path; command.ExecuteNonQuery(); }
            transaction.Commit();
        });

        private void Flush()
        {
            if (_pending.Count == 0) return;
            using var transaction = _disk!.BeginTransaction();
            using var command = _disk.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO remaining VALUES(@path)"; command.Parameters.Add("@path", SqliteType.Text);
            foreach (var path in _pending) { command.Parameters[0].Value = path; command.ExecuteNonQuery(); }
            transaction.Commit(); _pending.Clear();
        }

        private void Try(Action action)
        {
            if (Error != null) return;
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            { Error = ex; _paths.Clear(); }
        }

        public IEnumerable<string> Read()
        {
            if (_disk == null) { foreach (var path in _paths) yield return path; yield break; }
            Flush();
            using var command = _disk.CreateCommand(); command.CommandText = "SELECT path FROM remaining";
            using var reader = command.ExecuteReader(); while (reader.Read()) yield return reader.GetString(0);
        }

        public void Dispose()
        {
            _paths.Clear();
            try { _disk?.Dispose(); }
            finally
            {
                if (_path != null)
                    try { File.Delete(_path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
