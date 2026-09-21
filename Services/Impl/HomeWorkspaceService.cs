using System.Text.Json;
using MacExplorer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

/// <summary>
/// Homepage preferences and bounded, application-local usage history. Favorites themselves remain
/// entirely in IFileTagService; this service never moves, copies or deletes a user's files.
/// </summary>
public sealed class HomeWorkspaceService : IDisposable
{
    internal const string LayoutsKey = "home_folder_layouts_v1";
    internal const string CommandsKey = "home_script_commands_v1";
    private readonly DatabaseConnectionFactory _database;
    private readonly ISettingsService _settings;
    private readonly IFileTagService _tags;
    private readonly ILogger<HomeWorkspaceService>? _logger;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private readonly object _preferencesGate = new();
    private bool _initialized;
    public event EventHandler? HistoryChanged;
    public event EventHandler? PreferencesChanged;

    public HomeWorkspaceService(DatabaseConnectionFactory database, ISettingsService settings,
        IFileTagService tags, ILogger<HomeWorkspaceService>? logger = null)
    {
        _database = database;
        _settings = settings;
        _tags = tags;
        _logger = logger;
        _tags.TagRenamed += OnTagRenamed;
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("只支持本地绝对路径。", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string LayoutKey(FileTag tag) => $"{tag.Kind}:{FileTagCatalog.NormalizeName(tag.Name).ToUpperInvariant()}";

    public HomeFolderLayout GetLayout(FileTag tag)
    {
        lock (_preferencesGate)
            return Read<Dictionary<string, HomeFolderLayout>>(LayoutsKey).GetValueOrDefault(LayoutKey(tag))?.Normalize() ?? new();
    }

    public void SaveLayout(FileTag tag, HomeFolderLayout layout)
    {
        lock (_preferencesGate)
        {
            var layouts = Read<Dictionary<string, HomeFolderLayout>>(LayoutsKey);
            layouts[LayoutKey(tag)] = layout.Normalize();
            _settings.Set(LayoutsKey, JsonSerializer.Serialize(layouts));
        }
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<HomeScriptCommand> GetCommands(string path)
    {
        var key = NormalizePath(path);
        lock (_preferencesGate)
            return Read<Dictionary<string, HomeScriptCommand[]>>(CommandsKey).GetValueOrDefault(key) ?? [];
    }

    public void SaveCommands(string path, IEnumerable<HomeScriptCommand> commands)
    {
        var key = NormalizePath(path);
        var list = commands.Select(c => c.Validate()).ToArray();
        if (list.Length > 32) throw new ArgumentException("一个脚本最多配置 32 条命令。");
        if (list.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Length)
            throw new ArgumentException("同一脚本的命令名称不能重复。");
        if (list.Any(c => string.IsNullOrWhiteSpace(c.Id)) || list.Select(c => c.Id).Distinct().Count() != list.Length)
            throw new ArgumentException("命令标识无效，请删除重复项后重新添加。");
        lock (_preferencesGate)
        {
            var stored = Read<Dictionary<string, HomeScriptCommand[]>>(CommandsKey);
            if (list.Length == 0) stored.Remove(key);
            else stored[key] = list;
            _settings.Set(CommandsKey, JsonSerializer.Serialize(stored));
        }
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private T Read<T>(string key) where T : new()
    {
        var json = _settings.Get(key);
        if (string.IsNullOrEmpty(json)) return new T();
        try { return JsonSerializer.Deserialize<T>(json) ?? new T(); }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Invalid homepage preference {Key}; using defaults", key);
            return new T();
        }
    }

    private void OnTagRenamed(object? sender, TagRenamedEventArgs e)
    {
        lock (_preferencesGate)
        {
            var layouts = Read<Dictionary<string, HomeFolderLayout>>(LayoutsKey);
            var oldKey = LayoutKey(new FileTag(e.OldName, "", FileTagKind.Custom));
            if (!layouts.Remove(oldKey, out var layout)) return;
            if (e.NewName != null)
                layouts[LayoutKey(new FileTag(e.NewName, "", FileTagKind.Custom))] = layout;
            _settings.Set(LayoutsKey, JsonSerializer.Serialize(layouts));
        }
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Safe to fire-and-forget from navigation/launch hooks: failures never break opening a file.</summary>
    public async Task RecordUseAsync(string path, bool directoriesOnly = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
            path = NormalizePath(path);
            var recorded = await WithDatabaseAsync(connection =>
            {
                var directory = Directory.Exists(path);
                if ((!directory && directoriesOnly) || (!directory && !File.Exists(path))) return false;
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO home_usage (path, is_directory, use_count, last_used, added_at)
                    VALUES (@path, @directory, 1, @now, @now)
                    ON CONFLICT(path) DO UPDATE SET is_directory = @directory,
                        use_count = MIN(use_count + 1, 2147483647), last_used = @now;
                    DELETE FROM home_usage WHERE path IN
                        (SELECT path FROM home_usage ORDER BY last_used DESC, path LIMIT -1 OFFSET 500);
                    """;
                command.Parameters.AddWithValue("@path", path);
                command.Parameters.AddWithValue("@directory", directory ? 1 : 0);
                command.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
                command.ExecuteNonQuery();
                transaction.Commit();
                return true;
            });
            if (recorded) HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Could not record homepage usage for {Path}", path); }
    }

    public Task<IReadOnlyList<HomeUsageEntry>> GetUsageAsync(bool frequent, int count = 8,
        CancellationToken cancellationToken = default)
        => WithDatabaseAsync<IReadOnlyList<HomeUsageEntry>>(connection =>
        {
            var entries = new List<HomeUsageEntry>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, is_directory, use_count, last_used, added_at FROM home_usage";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = reader.GetString(0);
                var directory = reader.GetInt32(1) != 0;
                if (directory ? Directory.Exists(path) : File.Exists(path))
                    entries.Add(new(path, directory, reader.GetInt32(2), new DateTime(reader.GetInt64(3), DateTimeKind.Utc)) { AddedUtc = new DateTime(reader.GetInt64(4), DateTimeKind.Utc) });
            }
            var now = DateTime.UtcNow;
            return (frequent ? entries.OrderByDescending(e => e.Score(now)) : entries.OrderByDescending(e => (double)e.LastUsedUtc.Ticks))
                .ThenByDescending(e => e.LastUsedUtc).ThenBy(e => e.Path, StringComparer.Ordinal)
                .Take(Math.Clamp(count, 1, 100)).ToArray();
        }, cancellationToken);

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await WithDatabaseAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM home_usage";
            return command.ExecuteNonQuery();
        }, cancellationToken);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<T> WithDatabaseAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct = default)
    {
        await _databaseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Microsoft.Data.Sqlite's async APIs still do synchronous SQLite I/O. Keep it off the UI thread.
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var connection = _database.GetConnection();
                if (!_initialized) Initialize(connection);
                return work(connection);
            }, ct).ConfigureAwait(false);
        }
        finally { _databaseGate.Release(); }
    }

    private void Initialize(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'home_usage'";
        var existed = Convert.ToInt64(command.ExecuteScalar()) != 0;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS home_usage (
                path TEXT PRIMARY KEY COLLATE BINARY,
                is_directory INTEGER NOT NULL,
                use_count INTEGER NOT NULL,
                last_used INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('home_usage') WHERE name = 'added_at'";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
        {
            command.CommandText = "ALTER TABLE home_usage ADD COLUMN added_at INTEGER NOT NULL DEFAULT 0; UPDATE home_usage SET added_at = last_used;";
            command.ExecuteNonQuery();
        }
        if (!existed)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'frequent_folders'";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            {
                command.CommandText = """
                    INSERT OR IGNORE INTO home_usage (path, is_directory, use_count, last_used, added_at)
                    SELECT path, 1, MIN(visit_count, 2147483647), last_visited, last_visited FROM frequent_folders
                    ORDER BY last_visited DESC LIMIT 500;
                    """;
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        _initialized = true;
    }

    public void Dispose() => _tags.TagRenamed -= OnTagRenamed;
}
