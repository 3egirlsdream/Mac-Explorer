using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

public class SqliteFileIndex : IFileIndex, IFileIndexWriter, IDisposable
{
    private readonly SqliteConnection _readConnection;
    private readonly SqliteConnection _writeConnection;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public SqliteFileIndex(string databasePath)
    {
        _databasePath = databasePath;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // Try to open existing DB; if schema init fails, delete and recreate
        _writeConnection = OpenAndInitialize(databasePath, allowRecreate: true);
        _readConnection = OpenAndInitialize(databasePath, allowRecreate: false);
    }

    public SqliteFileIndex(string databasePath, DatabaseConnectionFactory connectionFactory)
    {
        _databasePath = databasePath;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // Try to open existing DB; if schema init fails, delete and recreate
        _writeConnection = OpenAndInitialize(databasePath, connectionFactory, allowRecreate: true);
        _readConnection = OpenAndInitialize(databasePath, connectionFactory, allowRecreate: false);
    }

    private static SqliteConnection OpenAndInitialize(string dbPath, DatabaseConnectionFactory connectionFactory, bool allowRecreate)
    {
        var conn = connectionFactory.GetConnection();
        try
        {
            SqliteSchema.Initialize(conn);
            return conn;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SQLite init failed: {ex.Message}");
            conn.Close();
            conn.Dispose();

            if (!allowRecreate)
                throw;

            // Delete corrupted/incompatible DB and recreate from scratch
            try
            {
                foreach (var suffix in new[] { "", "-shm", "-wal", "-journal" })
                {
                    var f = dbPath + suffix;
                    if (File.Exists(f)) File.Delete(f);
                }
                System.Diagnostics.Debug.WriteLine("Deleted old DB, recreating...");
            }
            catch { /* best effort cleanup */ }

            return OpenAndInitialize(dbPath, connectionFactory, allowRecreate: false);
        }
    }

    private static SqliteConnection OpenAndInitialize(string dbPath, bool allowRecreate)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        try
        {
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                cmd.ExecuteNonQuery();
            }

            SqliteSchema.Initialize(conn);
            return conn;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SQLite init failed: {ex.Message}");
            conn.Close();
            conn.Dispose();

            if (!allowRecreate)
                throw;

            // Delete corrupted/incompatible DB and recreate from scratch
            try
            {
                foreach (var suffix in new[] { "", "-shm", "-wal", "-journal" })
                {
                    var f = dbPath + suffix;
                    if (File.Exists(f)) File.Delete(f);
                }
                System.Diagnostics.Debug.WriteLine("Deleted old DB, recreating...");
            }
            catch { /* best effort cleanup */ }

            return OpenAndInitialize(dbPath, allowRecreate: false);
        }
    }

    // IFileIndex - Read operations

    public async Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string parentPath)
    {
        await _readLock.WaitAsync();
        try
        {
            var entries = new List<FileSystemEntry>();

            using var cmd = _readConnection.CreateCommand();
            cmd.CommandText = """
                SELECT path, name, extension, parent_path, size, is_directory,
                       created_at, modified_at, content_type, is_hidden
                FROM files
                WHERE parent_path = @parentPath
                ORDER BY is_directory DESC, name COLLATE NOCASE ASC
                """;
            cmd.Parameters.AddWithValue("@parentPath", parentPath);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }
        finally
        {
            _readLock.Release();
        }
    }

    public async Task<FileSystemEntry?> GetEntryAsync(string path)
    {
        await _readLock.WaitAsync();
        try
        {
            using var cmd = _readConnection.CreateCommand();
            cmd.CommandText = """
                SELECT path, name, extension, parent_path, size, is_directory,
                       created_at, modified_at, content_type, is_hidden
                FROM files
                WHERE path = @path
                """;
            cmd.Parameters.AddWithValue("@path", path);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return ReadEntry(reader);
            }

            return null;
        }
        finally
        {
            _readLock.Release();
        }
    }

    public async Task<IReadOnlyList<FileSystemEntry>> SearchByNameAsync(string pattern, int limit = 100)
    {
        pattern = pattern.Trim();
        await _readLock.WaitAsync();
        try
        {
            // If FTS5 is not available or the query contains FTS syntax
            // characters, go directly to LIKE search. This avoids using
            // SqliteException as the normal path for punctuation-heavy names.
            if (!SqliteSchema.IsFts5Available || !CanUseFtsPrefixSearch(pattern))
                return await SearchByNameLikeAsync(pattern, limit);

            var entries = new List<FileSystemEntry>();

            using var cmd = _readConnection.CreateCommand();
            cmd.CommandText = """
                SELECT f.path, f.name, f.extension, f.parent_path, f.size, f.is_directory,
                       f.created_at, f.modified_at, f.content_type, f.is_hidden
                FROM files_fts ft
                JOIN files f ON f.rowid = ft.rowid
                WHERE files_fts MATCH @pattern
                ORDER BY rank
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@pattern", $"{pattern}*");
            cmd.Parameters.AddWithValue("@limit", limit);

            try
            {
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    entries.Add(ReadEntry(reader));
                }
            }
            catch (SqliteException)
            {
                // FTS5 query syntax error, try simple LIKE search as fallback
                return await SearchByNameLikeAsync(pattern, limit);
            }

            // FTS5 only supports prefix matching; supplement with LIKE for contains matching
            if (entries.Count < limit)
            {
                var existingPaths = new HashSet<string>(entries.Select(e => e.FullPath));
                var likeResults = await SearchByNameLikeAsync(pattern, limit - entries.Count + existingPaths.Count);
                foreach (var entry in likeResults)
                {
                    if (existingPaths.Add(entry.FullPath))
                    {
                        entries.Add(entry);
                        if (entries.Count >= limit) break;
                    }
                }
            }

            return entries;
        }
        finally
        {
            _readLock.Release();
        }
    }

    public async Task<bool> IsDirectoryFreshAsync(string path, TimeSpan freshnessThreshold)
    {
        await _readLock.WaitAsync();
        try
        {
            using var cmd = _readConnection.CreateCommand();
            cmd.CommandText = """
                SELECT last_scanned FROM directories WHERE path = @path
                """;
            cmd.Parameters.AddWithValue("@path", path);

            var result = await cmd.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
                return false;

            var lastScanned = new DateTime((long)result, DateTimeKind.Utc);
            return DateTime.UtcNow - lastScanned < freshnessThreshold;
        }
        finally
        {
            _readLock.Release();
        }
    }

    // IFileIndexWriter - Write operations

    public async Task UpdateDirectoryAsync(string directoryPath, IReadOnlyList<FileSystemEntry> entries, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _writeConnection.BeginTransaction();

            try
            {
                // Delete old entries for this directory
                using (var cmd = _writeConnection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM files WHERE parent_path = @parentPath";
                    cmd.Parameters.AddWithValue("@parentPath", directoryPath);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Insert new entries with one reusable command. This avoids
                // recompiling the same INSERT statement for large directories.
                using (var insertCmd = _writeConnection.CreateCommand())
                {
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = """
                        INSERT OR REPLACE INTO files (path, name, extension, parent_path, size, is_directory, created_at, modified_at, content_type, is_hidden, indexed_at)
                        VALUES (@path, @name, @extension, @parentPath, @size, @isDirectory, @createdAt, @modifiedAt, @contentType, @isHidden, @indexedAt)
                        """;
                    AddReusableEntryParameters(insertCmd);
                    insertCmd.Prepare();

                    var indexedAt = DateTime.UtcNow.Ticks;
                    foreach (var entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SetEntryParameterValues(insertCmd, entry, indexedAt);
                        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                // Update directory metadata
                using (var cmd = _writeConnection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = """
                        INSERT OR REPLACE INTO directories (path, file_count, total_size, last_scanned, scan_status)
                        VALUES (@path, @fileCount, @totalSize, @lastScanned, @scanStatus)
                        """;
                    cmd.Parameters.AddWithValue("@path", directoryPath);
                    cmd.Parameters.AddWithValue("@fileCount", entries.Count);
                    cmd.Parameters.AddWithValue("@totalSize", entries.Sum(e => e.IsDirectory ? 0 : e.Size));
                    cmd.Parameters.AddWithValue("@lastScanned", DateTime.UtcNow.Ticks);
                    cmd.Parameters.AddWithValue("@scanStatus", "complete");
                    await cmd.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InvalidateDirectoriesAsync(IEnumerable<string> directoryPaths)
    {
        var paths = directoryPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeDirectoryPath)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0) return;

        await _writeLock.WaitAsync();
        try
        {
            using var transaction = _writeConnection.BeginTransaction();

            try
            {
                foreach (var path in paths)
                {
                    using (var cmd = _writeConnection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = "DELETE FROM files WHERE parent_path = @path";
                        cmd.Parameters.AddWithValue("@path", path);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    using (var cmd = _writeConnection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = "DELETE FROM directories WHERE path = @path";
                        cmd.Parameters.AddWithValue("@path", path);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RemoveEntryAsync(string path)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", path);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RenameEntryAsync(string oldPath, string newPath, string newName)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                UPDATE files
                SET path = @newPath, name = @newName, extension = @ext, modified_at = @now
                WHERE path = @oldPath
                """;
            cmd.Parameters.AddWithValue("@newPath", newPath);
            cmd.Parameters.AddWithValue("@newName", newName);
            cmd.Parameters.AddWithValue("@ext", string.IsNullOrEmpty(Path.GetExtension(newName)) ? DBNull.Value : Path.GetExtension(newName));
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("@oldPath", oldPath);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task AddEntryAsync(FileSystemEntry entry)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO files (path, name, extension, parent_path, size, is_directory, created_at, modified_at, content_type, is_hidden, indexed_at)
                VALUES (@path, @name, @extension, @parentPath, @size, @isDirectory, @createdAt, @modifiedAt, @contentType, @isHidden, @indexedAt)
                """;
            AddEntryParameters(cmd, entry);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static FileSystemEntry ReadEntry(SqliteDataReader reader)
    {
        var isDirectory = reader.GetInt32(5) != 0;
        var extension = reader.IsDBNull(2) ? "" : reader.GetString(2);
        return new FileSystemEntry
        {
            FullPath = reader.GetString(0),
            Name = reader.GetString(1),
            Extension = extension,
            Size = reader.GetInt64(4),
            IsDirectory = isDirectory,
            Created = new DateTime(reader.GetInt64(6), DateTimeKind.Utc).ToLocalTime(),
            LastModified = new DateTime(reader.GetInt64(7), DateTimeKind.Utc).ToLocalTime(),
            IsHidden = reader.GetInt32(9) != 0,
            IconKey = isDirectory
                ? ResolveBundleIconKey(extension)
                : ResolveIconKey(extension)
        };
    }

    internal static string ResolveBundleIconKey(string extension) => extension.ToLowerInvariant() switch
    {
        ".app" => "app-bundle",
        ".photoslibrary" or ".photobooth" or ".miximages"
            or ".musiclibrary" or ".tvlibrary" or ".aplibrary"
            or ".fcpbundle" or ".fcpproject"
            or ".garageband" or ".band" or ".logicx"
            or ".bundle" or ".framework" or ".plugin" or ".appex" or ".kext" => "app-bundle",
        ".pvm" or ".vmwarevm" or ".vbox" => "file-vm",
        ".xcodeproj" or ".xcworkspace" or ".playground" => "file-code",
        _ => "folder"
    };

    internal static string ResolveIconKey(string extension)
    {
        return FileIconResolver.ResolveIconKey(extension);
    }

    private static void AddEntryParameters(SqliteCommand cmd, FileSystemEntry entry)
    {
        cmd.Parameters.AddWithValue("@path", entry.FullPath);
        cmd.Parameters.AddWithValue("@name", entry.Name);
        cmd.Parameters.AddWithValue("@extension", string.IsNullOrEmpty(entry.Extension) ? DBNull.Value : entry.Extension);
        cmd.Parameters.AddWithValue("@parentPath", entry.FullPath.StartsWith("/") && entry.FullPath.Count(c => c == '/') == 1 && entry.FullPath == "/"
            ? "/"
            : Path.GetDirectoryName(entry.FullPath) ?? "/");
        cmd.Parameters.AddWithValue("@size", entry.Size);
        cmd.Parameters.AddWithValue("@isDirectory", entry.IsDirectory ? 1 : 0);
        cmd.Parameters.AddWithValue("@createdAt", entry.Created.ToUniversalTime().Ticks);
        cmd.Parameters.AddWithValue("@modifiedAt", entry.LastModified.ToUniversalTime().Ticks);
        cmd.Parameters.AddWithValue("@contentType", DBNull.Value);
        cmd.Parameters.AddWithValue("@isHidden", entry.IsHidden ? 1 : 0);
        cmd.Parameters.AddWithValue("@indexedAt", DateTime.UtcNow.Ticks);
    }

    private static void AddReusableEntryParameters(SqliteCommand cmd)
    {
        cmd.Parameters.Add("@path", SqliteType.Text);
        cmd.Parameters.Add("@name", SqliteType.Text);
        cmd.Parameters.Add("@extension", SqliteType.Text);
        cmd.Parameters.Add("@parentPath", SqliteType.Text);
        cmd.Parameters.Add("@size", SqliteType.Integer);
        cmd.Parameters.Add("@isDirectory", SqliteType.Integer);
        cmd.Parameters.Add("@createdAt", SqliteType.Integer);
        cmd.Parameters.Add("@modifiedAt", SqliteType.Integer);
        cmd.Parameters.Add("@contentType", SqliteType.Text);
        cmd.Parameters.Add("@isHidden", SqliteType.Integer);
        cmd.Parameters.Add("@indexedAt", SqliteType.Integer);
    }

    private static void SetEntryParameterValues(SqliteCommand cmd, FileSystemEntry entry, long indexedAt)
    {
        cmd.Parameters["@path"].Value = entry.FullPath;
        cmd.Parameters["@name"].Value = entry.Name;
        cmd.Parameters["@extension"].Value = string.IsNullOrEmpty(entry.Extension) ? DBNull.Value : entry.Extension;
        cmd.Parameters["@parentPath"].Value = entry.FullPath.StartsWith("/") && entry.FullPath.Count(c => c == '/') == 1 && entry.FullPath == "/"
            ? "/"
            : Path.GetDirectoryName(entry.FullPath) ?? "/";
        cmd.Parameters["@size"].Value = entry.Size;
        cmd.Parameters["@isDirectory"].Value = entry.IsDirectory ? 1 : 0;
        cmd.Parameters["@createdAt"].Value = entry.Created.ToUniversalTime().Ticks;
        cmd.Parameters["@modifiedAt"].Value = entry.LastModified.ToUniversalTime().Ticks;
        cmd.Parameters["@contentType"].Value = DBNull.Value;
        cmd.Parameters["@isHidden"].Value = entry.IsHidden ? 1 : 0;
        cmd.Parameters["@indexedAt"].Value = indexedAt;
    }

    private async Task<IReadOnlyList<FileSystemEntry>> SearchByNameLikeAsync(string pattern, int limit)
    {
        var entries = new List<FileSystemEntry>();

        using var cmd = _readConnection.CreateCommand();
        cmd.CommandText = """
            SELECT path, name, extension, parent_path, size, is_directory,
                   created_at, modified_at, content_type, is_hidden
            FROM files
            WHERE name LIKE @pattern
            ORDER BY name COLLATE NOCASE ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@pattern", $"%{pattern}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    private static bool CanUseFtsPrefixSearch(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        foreach (var c in pattern)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && !char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _readConnection.Close();
            _readConnection.Dispose();
            _writeConnection.Close();
            _writeConnection.Dispose();
            _readLock.Dispose();
            _writeLock.Dispose();
            _disposed = true;
        }
    }

    // Icon cache operations

    public string? GetCachedIcon(string appPath)
    {
        _readLock.Wait();
        try
        {
            using var cmd = _readConnection.CreateCommand();
            cmd.CommandText = "SELECT icon_base64 FROM icon_cache WHERE app_path = @path";
            cmd.Parameters.AddWithValue("@path", appPath);
            var result = cmd.ExecuteScalar();
            return result as string;
        }
        finally
        {
            _readLock.Release();
        }
    }

    public void SetCachedIcon(string appPath, string iconBase64)
    {
        _writeLock.Wait();
        try
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO icon_cache (app_path, icon_base64, modified_at)
                VALUES (@path, @icon, @modifiedAt)
                """;
            cmd.Parameters.AddWithValue("@path", appPath);
            cmd.Parameters.AddWithValue("@icon", iconBase64);
            cmd.Parameters.AddWithValue("@modifiedAt", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (path == "/") return path;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public interface IFileIndex
{
    Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string parentPath);
    Task<FileSystemEntry?> GetEntryAsync(string path);
    Task<IReadOnlyList<FileSystemEntry>> SearchByNameAsync(string pattern, int limit = 100);
    Task<bool> IsDirectoryFreshAsync(string path, TimeSpan freshnessThreshold);
}

public interface IFileIndexWriter
{
    Task UpdateDirectoryAsync(string directoryPath, IReadOnlyList<FileSystemEntry> entries, CancellationToken cancellationToken = default);
    Task InvalidateDirectoriesAsync(IEnumerable<string> directoryPaths);
    Task RenameEntryAsync(string oldPath, string newPath, string newName);
    Task RemoveEntryAsync(string path);
    Task AddEntryAsync(FileSystemEntry entry);
}
