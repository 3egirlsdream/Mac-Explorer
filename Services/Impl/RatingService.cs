using MacExplorer.Indexing;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

public class RatingService : IRatingService, IDisposable, IIdleMemoryCache
{
    internal const int DefaultMaxCacheEntries = 4096;
    internal const int DefaultIdleRetainEntries = 256;

    private sealed record RatingEntry(string FilePath, int Rating)
    {
        public DateTime LastUsedUtc = DateTime.UtcNow;
    }

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<RatingEntry>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<RatingEntry> _lru = [];
    private readonly int _maxCacheEntries;
    private readonly int _idleRetainEntries;
    private bool _disposed;

    public RatingService(DatabaseConnectionFactory connectionFactory)
        : this(connectionFactory, DefaultMaxCacheEntries, DefaultIdleRetainEntries) { }

    internal RatingService(DatabaseConnectionFactory connectionFactory, int maxCacheEntries, int idleRetainEntries = DefaultIdleRetainEntries)
    {
        _connection = connectionFactory.GetConnection();
        _maxCacheEntries = Math.Max(1, maxCacheEntries);
        _idleRetainEntries = Math.Clamp(idleRetainEntries, 0, _maxCacheEntries);
        MemoryCacheMaintenance.Register(this);
    }

    internal int CachedCount
    {
        get { lock (_cacheLock) return _cache.Count; }
    }

    internal void TrimMemory(DateTime unusedBefore)
    {
        lock (_cacheLock)
        {
            while (_cache.Count > _idleRetainEntries && _lru.Last is { } last && last.Value.LastUsedUtc < unusedBefore)
            {
                _cache.Remove(last.Value.FilePath);
                _lru.Remove(last);
            }
        }
    }

    void IIdleMemoryCache.TrimMemory(DateTime unusedBefore) => TrimMemory(unusedBefore);

    private void PutCache(string filePath, int rating)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(filePath, out var existing))
                _lru.Remove(existing);
            var node = _lru.AddFirst(new RatingEntry(filePath, rating));
            _cache[filePath] = node;
            while (_cache.Count > _maxCacheEntries && _lru.Last is { } last)
            {
                _cache.Remove(last.Value.FilePath);
                _lru.Remove(last);
            }
        }
    }

    private void RemoveCache(string filePath)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(filePath, out var existing))
            {
                _cache.Remove(filePath);
                _lru.Remove(existing);
            }
        }
    }

    private void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }

    public async Task TransferPathAsync(string source, string destination, bool move)
    {
        if (source == destination || VirtualPath.IsRemotePath(source) || VirtualPath.IsRemotePath(destination)) return;
        await _connectionLock.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            var scope = SqlitePathScope.Match("file_path");
            command.CommandText = $"""
                INSERT OR REPLACE INTO file_ratings(file_path, rating, updated_at)
                SELECT @destination || substr(file_path, length(@root) + 1), rating, updated_at FROM file_ratings
                WHERE {scope};
                """;
            command.Parameters.AddWithValue("@destination", destination);
            SqlitePathScope.Bind(command, source);
            await command.ExecuteNonQueryAsync();
            if (move)
            {
                command.CommandText = $"DELETE FROM file_ratings WHERE {scope}";
                await command.ExecuteNonQueryAsync();
            }
            transaction.Commit(); ClearCache();
        }
        finally { _connectionLock.Release(); }
    }
    public async Task DeletePathAsync(string path)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"DELETE FROM file_ratings WHERE {SqlitePathScope.Match("file_path")}";
            SqlitePathScope.Bind(command, path);
            await command.ExecuteNonQueryAsync(); ClearCache();
        }
        finally { _connectionLock.Release(); }
    }

    public int GetRatingCached(string filePath)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(filePath, out var node))
                return 0;
            node.Value.LastUsedUtc = DateTime.UtcNow;
            if (!ReferenceEquals(_lru.First, node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
            return node.Value.Rating;
        }
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
                RemoveCache(filePath);
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
                PutCache(filePath, rating);
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
                PutCache(path, rating);
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
            ClearCache();
            _connection.Close();
            _connection.Dispose();
            _connectionLock.Dispose();
            _disposed = true;
        }
    }
}
