using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace FKFinder.Services.Impl;

public class RatingService : IRatingService, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ConcurrentDictionary<string, int> _cache = new();
    private bool _disposed;

    public RatingService(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
    }

    public int GetRatingCached(string filePath)
    {
        return _cache.TryGetValue(filePath, out var rating) ? rating : 0;
    }

    public async Task SetRatingAsync(string filePath, int rating)
    {
        if (rating < 0 || rating > 5) return;

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

    public async Task BatchLoadRatingsAsync(IEnumerable<string> filePaths)
    {
        var paths = filePaths.ToList();
        if (paths.Count == 0) return;

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
