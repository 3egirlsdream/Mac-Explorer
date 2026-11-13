using System.Text.Json;

namespace MacExplorer.Services.Impl;

internal sealed class DirectoryPreferencesService(DatabaseConnectionFactory database) : IDirectoryPreferencesService
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private Task<T> Queue<T>(Func<T> work)
    {
        lock (_sync)
        {
            var next = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _tail = next; return next;
        }
    }
    public Task<DirectoryPreferences?> ReadAsync(string path) => Queue(() =>
    {
        using var connection = database.GetConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT preferences FROM directory_preferences WHERE path=@path";
        command.Parameters.AddWithValue("@path", path);
        var json = command.ExecuteScalar() as string;
        try { return json == null ? null : JsonSerializer.Deserialize<DirectoryPreferences>(json); }
        catch (JsonException) { return null; }
    });
    public Task SaveAsync(string path, DirectoryPreferences preferences) => Queue(() =>
    {
        using var connection = database.GetConnection(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO directory_preferences(path,preferences) VALUES(@path,@json) ON CONFLICT(path) DO UPDATE SET preferences=excluded.preferences";
        command.Parameters.AddWithValue("@path", path); command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(preferences)); command.ExecuteNonQuery();
        return true;
    });
    public Task RemoveAsync(string path) => Queue(() =>
    {
        using var connection = database.GetConnection(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM directory_preferences WHERE path=@path"; command.Parameters.AddWithValue("@path", path); command.ExecuteNonQuery();
        return true;
    });
}
