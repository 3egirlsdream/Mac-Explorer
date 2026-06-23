using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

public class RatingService : IRatingService, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _cache = new();
    private bool _disposed;

    public RatingService(DatabaseConnectionFactory connectionFactory)
    {
        _connection = connectionFactory.GetConnection();
        EnsureTagTable();
    }

    public int GetRatingCached(string filePath)
    {
        return _cache.TryGetValue(filePath, out var rating) ? rating : 0;
    }

    public async Task SetRatingAsync(string filePath, int rating)
    {
        if (rating < 0 || rating > 5) return;

        await _connectionLock.WaitAsync();
        try
        {
            if (rating == 0)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM file_ratings WHERE file_path = @path";
                cmd.Parameters.AddWithValue("@path", filePath);
                await cmd.ExecuteNonQueryAsync();
                _cache.TryRemove(filePath, out _);
            }
            else
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO file_ratings (file_path, rating, updated_at)
                    VALUES (@path, @rating, @updatedAt)
                    """;
                cmd.Parameters.AddWithValue("@path", filePath);
                cmd.Parameters.AddWithValue("@rating", rating);
                cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.Ticks);
                await cmd.ExecuteNonQueryAsync();
                _cache[filePath] = rating;
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task BatchLoadRatingsAsync(IEnumerable<string> filePaths)
    {
        var paths = filePaths.ToList();
        if (paths.Count == 0) return;

        await _connectionLock.WaitAsync();
        try
        {
            // Build parameterized query for batch
            using var cmd = _connection.CreateCommand();
            var paramNames = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                var paramName = $"@p{i}";
                paramNames.Add(paramName);
                cmd.Parameters.AddWithValue(paramName, paths[i]);
            }

            cmd.CommandText = $"SELECT file_path, rating FROM file_ratings WHERE file_path IN ({string.Join(",", paramNames)})";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var path = reader.GetString(0);
                var rating = reader.GetInt32(1);
                _cache[path] = rating;
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void EnsureTagTable()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS file_tags (
                file_path TEXT NOT NULL,
                tag TEXT NOT NULL,
                is_system INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL,
                PRIMARY KEY (file_path, tag)
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public List<string> GetCustomTags(string filePath)
    {
        _connectionLock.Wait();
        try
        {
            var tags = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT tag FROM file_tags WHERE file_path = @path AND is_system = 0 ORDER BY created_at";
            cmd.Parameters.AddWithValue("@path", filePath);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                tags.Add(reader.GetString(0));
            return tags;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public List<string> GetSystemTags(string filePath)
    {
        _connectionLock.Wait();
        try
        {
            var tags = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT tag FROM file_tags WHERE file_path = @path AND is_system = 1 ORDER BY created_at";
            cmd.Parameters.AddWithValue("@path", filePath);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                tags.Add(reader.GetString(0));
            return tags;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public void AddCustomTag(string filePath, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        _connectionLock.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO file_tags (file_path, tag, is_system, created_at)
                VALUES (@path, @tag, 0, @now)
                """;
            cmd.Parameters.AddWithValue("@path", filePath);
            cmd.Parameters.AddWithValue("@tag", tag.Trim());
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public void RemoveCustomTag(string filePath, string tag)
    {
        _connectionLock.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM file_tags WHERE file_path = @path AND tag = @tag";
            cmd.Parameters.AddWithValue("@path", filePath);
            cmd.Parameters.AddWithValue("@tag", tag);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public void ToggleSystemTag(string filePath, string tagName)
    {
        _connectionLock.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM file_tags WHERE file_path = @path AND tag = @tag AND is_system = 1";
            cmd.Parameters.AddWithValue("@path", filePath);
            cmd.Parameters.AddWithValue("@tag", tagName);
            var exists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;

            if (exists)
            {
                using var del = _connection.CreateCommand();
                del.CommandText = "DELETE FROM file_tags WHERE file_path = @path AND tag = @tag AND is_system = 1";
                del.Parameters.AddWithValue("@path", filePath);
                del.Parameters.AddWithValue("@tag", tagName);
                del.ExecuteNonQuery();
            }
            else
            {
                using var ins = _connection.CreateCommand();
                ins.CommandText = """
                    INSERT OR IGNORE INTO file_tags (file_path, tag, is_system, created_at)
                    VALUES (@path, @tag, 1, @now)
                    """;
                ins.Parameters.AddWithValue("@path", filePath);
                ins.Parameters.AddWithValue("@tag", tagName);
                ins.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
                ins.ExecuteNonQuery();
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _connectionLock.Dispose();
            _disposed = true;
        }
    }
}
