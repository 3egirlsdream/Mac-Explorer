using FKFinder.Models;
using Microsoft.Data.Sqlite;

namespace FKFinder.Indexing;

public class SqliteFileIndex : IFileIndex, IFileIndexWriter, IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public SqliteFileIndex(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate");
        _connection.Open();

        // Enable WAL mode for concurrent reads
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL";
            cmd.ExecuteNonQuery();
        }

        SqliteSchema.Initialize(_connection);
    }

    // IFileIndex - Read operations

    public async Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string parentPath)
    {
        var entries = new List<FileSystemEntry>();

        using var cmd = _connection.CreateCommand();
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

    public async Task<FileSystemEntry?> GetEntryAsync(string path)
    {
        using var cmd = _connection.CreateCommand();
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

    public async Task<IReadOnlyList<FileSystemEntry>> SearchByNameAsync(string pattern, int limit = 100)
    {
        var entries = new List<FileSystemEntry>();

        using var cmd = _connection.CreateCommand();
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

        return entries;
    }

    public async Task<bool> IsDirectoryFreshAsync(string path, TimeSpan freshnessThreshold)
    {
        using var cmd = _connection.CreateCommand();
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

    // IFileIndexWriter - Write operations

    public async Task UpdateDirectoryAsync(string directoryPath, IReadOnlyList<FileSystemEntry> entries)
    {
        using var transaction = _connection.BeginTransaction();

        try
        {
            // Delete old entries for this directory
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM files WHERE parent_path = @parentPath";
                cmd.Parameters.AddWithValue("@parentPath", directoryPath);
                await cmd.ExecuteNonQueryAsync();
            }

            // Insert new entries
            foreach (var entry in entries)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO files (path, name, extension, parent_path, size, is_directory, created_at, modified_at, content_type, is_hidden, indexed_at)
                    VALUES (@path, @name, @extension, @parentPath, @size, @isDirectory, @createdAt, @modifiedAt, @contentType, @isHidden, @indexedAt)
                    """;
                AddEntryParameters(cmd, entry);
                await cmd.ExecuteNonQueryAsync();
            }

            // Update directory metadata
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO directories (path, file_count, total_size, last_scanned, scan_status)
                    VALUES (@path, @fileCount, @totalSize, @lastScanned, @scanStatus)
                    """;
                cmd.Parameters.AddWithValue("@path", directoryPath);
                cmd.Parameters.AddWithValue("@fileCount", entries.Count);
                cmd.Parameters.AddWithValue("@totalSize", entries.Where(e => !e.IsDirectory).Sum(e => e.Size));
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

    public async Task RemoveEntryAsync(string path)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddEntryAsync(FileSystemEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO files (path, name, extension, parent_path, size, is_directory, created_at, modified_at, content_type, is_hidden, indexed_at)
            VALUES (@path, @name, @extension, @parentPath, @size, @isDirectory, @createdAt, @modifiedAt, @contentType, @isHidden, @indexedAt)
            """;
        AddEntryParameters(cmd, entry);
        await cmd.ExecuteNonQueryAsync();
    }

    private static FileSystemEntry ReadEntry(SqliteDataReader reader)
    {
        return new FileSystemEntry
        {
            FullPath = reader.GetString(0),
            Name = reader.GetString(1),
            Extension = reader.IsDBNull(2) ? "" : reader.GetString(2),
            Size = reader.GetInt64(4),
            IsDirectory = reader.GetInt32(5) != 0,
            Created = new DateTime(reader.GetInt64(6), DateTimeKind.Utc).ToLocalTime(),
            LastModified = new DateTime(reader.GetInt64(7), DateTimeKind.Utc).ToLocalTime(),
            IsHidden = reader.GetInt32(9) != 0,
            IconKey = "file-generic" // Will be resolved by IconService
        };
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

    private async Task<IReadOnlyList<FileSystemEntry>> SearchByNameLikeAsync(string pattern, int limit)
    {
        var entries = new List<FileSystemEntry>();

        using var cmd = _connection.CreateCommand();
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
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
    Task UpdateDirectoryAsync(string directoryPath, IReadOnlyList<FileSystemEntry> entries);
    Task RemoveEntryAsync(string path);
    Task AddEntryAsync(FileSystemEntry entry);
}
