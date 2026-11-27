using System.Text;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

internal readonly record struct SearchWriteResult(long Inserted = 0, long Updated = 0, long Deleted = 0)
{
    public static SearchWriteResult operator +(SearchWriteResult a, SearchWriteResult b) =>
        new(a.Inserted + b.Inserted, a.Updated + b.Updated, a.Deleted + b.Deleted);
}

internal readonly record struct ExistingEntryMetadata(long Size, long CreatedTicks, long ModifiedTicks, byte Flags)
{
    public static byte PackFlags(bool isDirectory, bool isHidden, bool isSymbolicLink) =>
        (byte)((isDirectory ? 1 : 0) | (isHidden ? 2 : 0) | (isSymbolicLink ? 4 : 0));

    public bool Matches(long size, long createdTicks, long modifiedTicks, bool isDirectory, bool isHidden, bool isSymbolicLink) =>
        Size == size
        && CreatedTicks == createdTicks
        && ModifiedTicks == modifiedTicks
        && Flags == PackFlags(isDirectory, isHidden, isSymbolicLink);
}

public sealed partial class SearchCatalog
{
    internal long ScanMemoryLimitBytes { get; set; } = 8 * 1024 * 1024;
    internal string? ScanScratchDirectory { get; set; }
    // Directory scans own _writer, so their bounded membership/metadata buffer can be reused without retaining paths.
    private readonly Dictionary<string, ExistingEntryMetadata> _scanEntries = new(StringComparer.Ordinal);

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
        Dictionary<string, SearchEntry>? existing = null;
        if (!newDirectory)
        {
            existing = new Dictionary<string, SearchEntry>(entries.Count, StringComparer.Ordinal);
            using var lookup = connection.CreateCommand();
            var sql = new StringBuilder(128 + entries.Count * 5);
            sql.Append("SELECT ").Append(Columns).Append(",e.parent_id FROM search_entry_details e WHERE e.path IN (");
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0) sql.Append(',');
                var name = "@p" + i;
                sql.Append(name);
                lookup.Parameters.AddWithValue(name, entries[i].Path);
            }
            sql.Append(')');
            lookup.CommandText = sql.ToString();
            var parentValues = new Dictionary<long, (string Path, string Key)>();
            using var reader = lookup.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(13);
                if (!parentValues.TryGetValue(id, out var values))
                    parentValues[id] = values = (reader.GetString(2), reader.GetString(6));
                var entry = ReadEntry(reader, values.Path, values.Key); existing[entry.Path] = entry;
            }
            var allSame = true;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!existing.TryGetValue(entry.Path, out var old) || !SameMetadata(old, entry))
                {
                    allSame = false;
                    break;
                }
            }
            if (allSame) return default;
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;
        var pPath = command.Parameters.Add("@path", SqliteType.Text);
        var pName = command.Parameters.Add("@name", SqliteType.Text);
        var pExt = command.Parameters.Add("@ext", SqliteType.Text);
        var pExtKey = command.Parameters.Add("@extKey", SqliteType.Text);
        var pNameKey = command.Parameters.Add("@nameKey", SqliteType.Text);
        var pInitials = command.Parameters.Add("@initials", SqliteType.Text);
        var pParentId = command.Parameters.Add("@parentId", SqliteType.Integer);
        var pSize = command.Parameters.Add("@size", SqliteType.Integer);
        var pDir = command.Parameters.Add("@dir", SqliteType.Integer);
        var pHidden = command.Parameters.Add("@hidden", SqliteType.Integer);
        var pCreated = command.Parameters.Add("@created", SqliteType.Integer);
        var pModified = command.Parameters.Add("@modified", SqliteType.Integer);
        var pLink = command.Parameters.Add("@link", SqliteType.Integer);
        command.Prepare();
        using var parent = connection.CreateCommand();
        parent.Transaction = transaction;
        parent.CommandText = """
            INSERT INTO search_directories(path,parent_key) VALUES(@path,@key)
            ON CONFLICT(path) DO UPDATE SET parent_key=excluded.parent_key
            WHERE search_directories.parent_key IS NOT excluded.parent_key;
            SELECT id FROM search_directories WHERE path=@path COLLATE BINARY;
            """;
        var pParentPath = parent.Parameters.Add("@path", SqliteType.Text);
        var pParentKey = parent.Parameters.Add("@key", SqliteType.Text);
        var parents = new Dictionary<string, long>(StringComparer.Ordinal);
        existing ??= new Dictionary<string, SearchEntry>(entries.Count, StringComparer.Ordinal);
        var result = new SearchWriteResult();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ct.ThrowIfCancellationRequested();
            existing.TryGetValue(entry.Path, out var old);
            if (old != null && SameMetadata(old, entry)) continue;
            if (!parents.TryGetValue(entry.ParentPath, out var parentId))
            {
                pParentPath.Value = entry.ParentPath; pParentKey.Value = entry.ParentKey;
                parents[entry.ParentPath] = parentId = (long)parent.ExecuteScalar()!;
            }
            if (old is { IsDirectory: true } && (!entry.IsDirectory || (!old.IsSymbolicLink && entry.IsSymbolicLink)))
                result += new SearchWriteResult(Deleted: DeleteScope(connection, transaction, entry.Path, descendantsOnly: true));
            pPath.Value = entry.Path; pName.Value = entry.Name;
            pParentId.Value = parentId; pExt.Value = entry.Extension;
            pExtKey.Value = SearchQuery.Fold(entry.Extension.TrimStart('.'));
            pNameKey.Value = entry.NameKey; pInitials.Value = entry.InitialsKey;
            pSize.Value = entry.Size; pDir.Value = entry.IsDirectory;
            pHidden.Value = entry.IsHidden; pCreated.Value = entry.CreatedTicks;
            pModified.Value = entry.ModifiedTicks; pLink.Value = entry.IsSymbolicLink;
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
                Path.GetDirectoryName(connection.DataSource)!, "Logs", "scan-scratch"), owner._scanEntries);
            try
            {
                using var registration = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT path, size, is_directory, is_hidden, created_ticks, modified_ticks, is_symbolic_link
                    FROM search_entries
                    WHERE parent_id=(SELECT id FROM search_directories WHERE path=@parent COLLATE BINARY)
                    """;
                command.Parameters.AddWithValue("@parent", directory);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var metadata = new ExistingEntryMetadata(
                        reader.GetInt64(1),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        ExistingEntryMetadata.PackFlags(reader.GetInt32(2) != 0, reader.GetInt32(3) != 0, reader.GetInt32(6) != 0));
                    _remaining.Add(reader.GetString(0), metadata);
                }
                _newDirectory = _remaining.InitialCount == 0;
            }
            catch (SqliteException) when (ct.IsCancellationRequested)
            { _remaining.Dispose(); throw new OperationCanceledException(ct); }
            catch { _remaining.Dispose(); throw; }
        }

        internal bool TryMarkUnchanged(string path, long size, long createdTicks, long modifiedTicks,
            bool isDirectory, bool isHidden, bool isSymbolicLink)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed) throw new InvalidOperationException("Directory reconciliation already completed.");
            return _remaining.TryMatchAndRemove(path, size, createdTicks, modifiedTicks, isDirectory, isHidden, isSymbolicLink);
        }

        internal Task<SearchWriteResult> RecordAsync(IReadOnlyList<SearchEntry> entries, CancellationToken ct = default) =>
            ExecuteAsync(() =>
            {
                _remaining.Seen(entries);
                return ApplyEntries(_connection, entries, ct, _newDirectory);
            }, ct);

        internal Task<SearchWriteResult> CompleteAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed) throw new InvalidOperationException("Directory reconciliation already completed.");
            ct.ThrowIfCancellationRequested();
            if (_remaining.Error == null && !_remaining.Spilled && !_remaining.HasRemaining)
            {
                _completed = true;
                return Task.FromResult(default(SearchWriteResult));
            }
            return ExecuteAsync(() =>
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
        }

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

    private sealed class RemainingPaths(long limit, string directory, Dictionary<string, ExistingEntryMetadata> entries) : IDisposable
    {
        private readonly Dictionary<string, ExistingEntryMetadata> _entries = entries;
        private List<string>? _pending;
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
                if (_disk == null) return _entries.Count != 0;
                Flush();
                using var command = _disk.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM remaining)";
                return (long)command.ExecuteScalar()! != 0;
            }
        }

        public void Add(string path, ExistingEntryMetadata metadata) => Try(() =>
        {
            InitialCount++;
            if (_disk == null)
            {
                if (_entries.TryAdd(path, metadata)) _stringBytes += 32L + path.Length * 2L;
                if (_stringBytes + _entries.EnsureCapacity(0) * 48L + 48 <= limit) return;
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
                foreach (var key in _entries.Keys) { insert.Parameters[0].Value = key; insert.ExecuteNonQuery(); }
                transaction.Commit(); _entries.Clear(); _entries.TrimExcess(0);
            }
            else
            {
                _pending ??= new List<string>(256);
                _pending.Add(path);
                if (_pending.Count == 256) Flush();
            }
        });

        public bool TryMatchAndRemove(string path, long size, long createdTicks, long modifiedTicks,
            bool isDirectory, bool isHidden, bool isSymbolicLink)
        {
            if (Error != null || _disk != null || _entries.Count == 0) return false;
            if (!_entries.TryGetValue(path, out var existing)) return false;
            if (!existing.Matches(size, createdTicks, modifiedTicks, isDirectory, isHidden, isSymbolicLink)) return false;
            _entries.Remove(path);
            return true;
        }

        public void Seen(IReadOnlyList<SearchEntry> entries) => Try(() =>
        {
            if (_disk == null)
            {
                for (var i = 0; i < entries.Count; i++) _entries.Remove(entries[i].Path);
                return;
            }
            Flush();
            using var transaction = _disk.BeginTransaction();
            using var command = _disk.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM remaining WHERE path=@path COLLATE BINARY"; command.Parameters.Add("@path", SqliteType.Text);
            for (var i = 0; i < entries.Count; i++) { command.Parameters[0].Value = entries[i].Path; command.ExecuteNonQuery(); }
            transaction.Commit();
        });

        private void Flush()
        {
            if (_pending == null || _pending.Count == 0) return;
            using var transaction = _disk!.BeginTransaction();
            using var command = _disk.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO remaining VALUES(@path)"; command.Parameters.Add("@path", SqliteType.Text);
            for (var i = 0; i < _pending.Count; i++) { command.Parameters[0].Value = _pending[i]; command.ExecuteNonQuery(); }
            transaction.Commit(); _pending.Clear();
        }

        private void Try(Action action)
        {
            if (Error != null) return;
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            { Error = ex; _entries.Clear(); }
        }

        public IEnumerable<string> Read()
        {
            if (_disk == null) { foreach (var key in _entries.Keys) yield return key; yield break; }
            Flush();
            using var command = _disk.CreateCommand(); command.CommandText = "SELECT path FROM remaining";
            using var reader = command.ExecuteReader(); while (reader.Read()) yield return reader.GetString(0);
        }

        public void Dispose()
        {
            _entries.Clear();
            if (_entries.EnsureCapacity(0) > 1024) _entries.TrimExcess(256);
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
