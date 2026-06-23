using MacExplorer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class CollectionService : ICollectionService, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ILogger<CollectionService>? _logger;
    private bool _disposed;

    public CollectionService(DatabaseConnectionFactory connectionFactory, ILoggerFactory? loggerFactory = null)
    {
        _connection = connectionFactory.GetConnection();
        _logger = loggerFactory?.CreateLogger<CollectionService>();
    }

    public async Task<IReadOnlyList<Collection>> GetAllCollectionsAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            var collections = new List<Collection>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, icon, sort_order, created_at FROM collections ORDER BY sort_order, created_at";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                collections.Add(new Collection
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Icon = reader.IsDBNull(2) ? null : reader.GetString(2),
                    SortOrder = reader.GetInt32(3),
                    CreatedAt = new DateTime(reader.GetInt64(4), DateTimeKind.Utc).ToLocalTime()
                });
            }
            return collections;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<Collection> CreateCollectionAsync(string name, string? icon = null)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
            INSERT INTO collections (name, icon, sort_order, created_at)
            VALUES (@name, @icon, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM collections), @createdAt);
            SELECT last_insert_rowid();
            """;
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@icon", (object?)icon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.Ticks);

            var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            return new Collection
            {
                Id = id,
                Name = name,
                Icon = icon,
                SortOrder = id,
                CreatedAt = DateTime.Now
            };
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task RenameCollectionAsync(int collectionId, string newName)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE collections SET name = @name WHERE id = @id";
            cmd.Parameters.AddWithValue("@name", newName);
            cmd.Parameters.AddWithValue("@id", collectionId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DeleteCollectionAsync(int collectionId)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM collection_items WHERE collection_id = @id";
                    cmd.Parameters.AddWithValue("@id", collectionId);
                    await cmd.ExecuteNonQueryAsync();
                }
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM collections WHERE id = @id";
                    cmd.Parameters.AddWithValue("@id", collectionId);
                    await cmd.ExecuteNonQueryAsync();
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to delete collection {CollectionId} in {Method}, rolling back transaction", collectionId, nameof(DeleteCollectionAsync));
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task AddFileToCollectionAsync(int collectionId, string filePath)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
            INSERT OR IGNORE INTO collection_items (collection_id, file_path, added_at)
            VALUES (@collectionId, @filePath, @addedAt)
            """;
            cmd.Parameters.AddWithValue("@collectionId", collectionId);
            cmd.Parameters.AddWithValue("@filePath", filePath);
            cmd.Parameters.AddWithValue("@addedAt", DateTime.UtcNow.Ticks);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task RemoveFileFromCollectionAsync(int collectionId, string filePath)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM collection_items WHERE collection_id = @collectionId AND file_path = @filePath";
            cmd.Parameters.AddWithValue("@collectionId", collectionId);
            cmd.Parameters.AddWithValue("@filePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetFilePathsInCollectionAsync(int collectionId)
    {
        await _connectionLock.WaitAsync();
        try
        {
            var paths = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT file_path FROM collection_items WHERE collection_id = @id ORDER BY added_at";
            cmd.Parameters.AddWithValue("@id", collectionId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                paths.Add(reader.GetString(0));

            return paths;
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
