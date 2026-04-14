using FKFinder.Models;
using Microsoft.Data.Sqlite;

namespace FKFinder.Services.Impl;

public class PinnedFolderService : IPinnedFolderService, IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public PinnedFolderService(DatabaseConnectionFactory connectionFactory)
    {
        _connection = connectionFactory.GetConnection();
    }

    public async Task<IReadOnlyList<PinnedFolder>> GetAllAsync()
    {
        var folders = new List<PinnedFolder>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, folder_path, display_name, sort_order, pinned_at FROM pinned_folders ORDER BY sort_order, pinned_at";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            folders.Add(new PinnedFolder
            {
                Id = reader.GetInt32(0),
                FolderPath = reader.GetString(1),
                DisplayName = reader.GetString(2),
                SortOrder = reader.GetInt32(3),
                PinnedAt = new DateTime(reader.GetInt64(4), DateTimeKind.Utc).ToLocalTime()
            });
        }
        return folders;
    }

    public async Task PinAsync(string folderPath, string displayName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO pinned_folders (folder_path, display_name, sort_order, pinned_at)
            VALUES (@path, @displayName, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM pinned_folders), @pinnedAt)
            """;
        cmd.Parameters.AddWithValue("@path", folderPath);
        cmd.Parameters.AddWithValue("@displayName", displayName);
        cmd.Parameters.AddWithValue("@pinnedAt", DateTime.UtcNow.Ticks);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UnpinAsync(string folderPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM pinned_folders WHERE folder_path = @path";
        cmd.Parameters.AddWithValue("@path", folderPath);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> IsPinnedAsync(string folderPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM pinned_folders WHERE folder_path = @path";
        cmd.Parameters.AddWithValue("@path", folderPath);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }

    public async Task UpdateFolderPathAsync(string oldPath, string newPath, string newDisplayName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE pinned_folders 
            SET folder_path = @newPath, display_name = @newDisplayName
            WHERE folder_path = @oldPath
            """;
        cmd.Parameters.AddWithValue("@oldPath", oldPath);
        cmd.Parameters.AddWithValue("@newPath", newPath);
        cmd.Parameters.AddWithValue("@newDisplayName", newDisplayName);
        await cmd.ExecuteNonQueryAsync();
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
