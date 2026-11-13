using MacExplorer.Models;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

public class PinnedFolderService : IPinnedFolderService, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private bool _disposed;
    public event EventHandler? Changed;

    public PinnedFolderService(DatabaseConnectionFactory connectionFactory)
    {
        _connection = connectionFactory.GetConnection();
    }

    public async Task<IReadOnlyList<PinnedFolder>> GetAllAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            var folders = new List<PinnedFolder>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, folder_path, display_name, sort_order, pinned_at, group_id FROM pinned_folders ORDER BY group_id, sort_order, pinned_at";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                folders.Add(new PinnedFolder
                {
                    Id = reader.GetInt32(0),
                    GroupId = reader.GetInt32(5),
                    FolderPath = reader.GetString(1),
                    DisplayName = reader.GetString(2),
                    SortOrder = reader.GetInt32(3),
                    PinnedAt = new DateTime(reader.GetInt64(4), DateTimeKind.Utc).ToLocalTime()
                });
            }
            return folders;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task PinAsync(string folderPath, string displayName, int groupId = 0)
    {
        await _connectionLock.WaitAsync();
        try
        {
            await RequireGroupAsync(groupId);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
            INSERT OR IGNORE INTO pinned_folders (folder_path, display_name, sort_order, pinned_at, group_id)
            SELECT @path, @displayName, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM pinned_folders WHERE group_id = @group), @pinnedAt, @group
            WHERE @group = 0 OR EXISTS (SELECT 1 FROM pinned_folder_groups WHERE id = @group)
            """;
            cmd.Parameters.AddWithValue("@path", folderPath);
            cmd.Parameters.AddWithValue("@group", groupId);
            cmd.Parameters.AddWithValue("@displayName", displayName);
            cmd.Parameters.AddWithValue("@pinnedAt", DateTime.UtcNow.Ticks);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UnpinAsync(string folderPath, int groupId = 0)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM pinned_folders WHERE folder_path = @path AND group_id = @group";
            cmd.Parameters.AddWithValue("@path", folderPath);
            cmd.Parameters.AddWithValue("@group", groupId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> IsPinnedAsync(string folderPath, int groupId = 0)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM pinned_folders WHERE folder_path = @path AND group_id = @group";
            cmd.Parameters.AddWithValue("@path", folderPath);
            cmd.Parameters.AddWithValue("@group", groupId);
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result) > 0;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task UpdateFolderPathAsync(string oldPath, string newPath, string newDisplayName)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
            UPDATE OR REPLACE pinned_folders
            SET folder_path = @newPath || substr(folder_path, length(@oldPath) + 1),
                display_name = CASE WHEN folder_path = @oldPath THEN @newDisplayName ELSE display_name END
            WHERE folder_path = @oldPath OR substr(folder_path, 1, length(@prefix)) = @prefix
            """;
            cmd.Parameters.AddWithValue("@oldPath", oldPath);
            cmd.Parameters.AddWithValue("@newPath", newPath);
            cmd.Parameters.AddWithValue("@newDisplayName", newDisplayName);
            cmd.Parameters.AddWithValue("@prefix", oldPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReorderAsync(IReadOnlyList<string> orderedFolderPaths, int groupId = 0)
    {
        await _connectionLock.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            for (var index = 0; index < orderedFolderPaths.Count; index++)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE pinned_folders SET sort_order = @order WHERE folder_path = @path AND group_id = @group";
                cmd.Parameters.AddWithValue("@order", index + 1);
                cmd.Parameters.AddWithValue("@path", orderedFolderPaths[index]);
                cmd.Parameters.AddWithValue("@group", groupId);
                await cmd.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        finally
        {
            _connectionLock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<PinnedFolderGroup>> GetGroupsAsync()
    {
        await _connectionLock.WaitAsync();
        try { return await ReadGroupsAsync(); }
        finally { _connectionLock.Release(); }
    }

    private async Task<List<PinnedFolderGroup>> ReadGroupsAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM pinned_folder_groups ORDER BY id";
        using var reader = await cmd.ExecuteReaderAsync();
        var groups = new List<PinnedFolderGroup>();
        while (await reader.ReadAsync())
            groups.Add(new PinnedFolderGroup { Id = reader.GetInt32(0), Name = reader.GetString(1) });
        return groups;
    }

    private async Task RequireGroupAsync(int groupId)
    {
        if (groupId == 0) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pinned_folder_groups WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", groupId);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
            throw new InvalidOperationException(LocalizationText.Get("sidebar.groups.not_found"));
    }

    private async Task<string> ValidateGroupNameAsync(string name, int groupId = 0)
    {
        name = name.Trim();
        if (name.Length is 0 or > 80)
            throw new ArgumentException(LocalizationText.Get("sidebar.groups.invalid_name"));
        if ((await ReadGroupsAsync()).Any(group => group.Id != groupId
            && string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(LocalizationText.Get("sidebar.groups.duplicate_name"));
        return name;
    }

    public async Task<int> CreateGroupAsync(string name)
    {
        int id;
        await _connectionLock.WaitAsync();
        try
        {
            name = await ValidateGroupNameAsync(name);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO pinned_folder_groups(name) VALUES (@name); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@name", name);
            id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        finally { _connectionLock.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public async Task RenameGroupAsync(int groupId, string name)
    {
        await _connectionLock.WaitAsync();
        try
        {
            await RequireGroupAsync(groupId);
            name = await ValidateGroupNameAsync(name, groupId);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE pinned_folder_groups SET name = @name WHERE id = @id";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@id", groupId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _connectionLock.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteGroupAsync(int groupId)
    {
        if (groupId <= 0) throw new ArgumentOutOfRangeException(nameof(groupId));
        await _connectionLock.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM pinned_folders WHERE group_id = @id; DELETE FROM pinned_folder_groups WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", groupId);
            await cmd.ExecuteNonQueryAsync();
            transaction.Commit();
        }
        finally { _connectionLock.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
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
