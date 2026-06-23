using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class FrequentFolderService : IFrequentFolderService, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly HashSet<string> _excludedPaths;
    private readonly string _homeDirectory;
    private readonly ILogger<FrequentFolderService>? _logger;
    private bool _disposed;

    // System folders that should not be tracked
    private static readonly HashSet<string> DefaultExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/System", "/Library", "/usr", "/bin", "/sbin",
        "/var", "/private", "/dev", "/Applications",
        "/System/Applications", "/Volumes", "/.vol"
    };

    public FrequentFolderService(DatabaseConnectionFactory connectionFactory, string homeDirectory, ILoggerFactory? loggerFactory = null)
    {
        _homeDirectory = homeDirectory;
        _connection = connectionFactory.GetConnection();
        _logger = loggerFactory?.CreateLogger<FrequentFolderService>();

        // Build excluded paths: system folders + standard sidebar folders
        _excludedPaths = new HashSet<string>(DefaultExcludedFolders, StringComparer.OrdinalIgnoreCase);
        _excludedPaths.Add(homeDirectory);
        _excludedPaths.Add(Path.Combine(homeDirectory, "Desktop"));
        _excludedPaths.Add(Path.Combine(homeDirectory, "Documents"));
        _excludedPaths.Add(Path.Combine(homeDirectory, "Downloads"));
        _excludedPaths.Add(Path.Combine(homeDirectory, "Pictures"));
        _excludedPaths.Add(Path.Combine(homeDirectory, "Music"));
    }

    private bool ShouldTrack(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return false;

        // Exclude exact match system/sidebar folders
        if (_excludedPaths.Contains(folderPath)) return false;

        // Exclude system root paths
        foreach (var excluded in DefaultExcludedFolders)
        {
            if (excluded != "/" && folderPath.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public async Task RecordVisitAsync(string folderPath)
    {
        if (!ShouldTrack(folderPath)) return;

        try
        {
            var name = Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(name)) return;

            await _connectionLock.WaitAsync();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO frequent_folders (path, name, visit_count, last_visited)
                    VALUES (@path, @name, 1, @now)
                    ON CONFLICT(path) DO UPDATE SET
                        visit_count = visit_count + 1,
                        last_visited = @now,
                        name = @name
                    """;
                cmd.Parameters.AddWithValue("@path", folderPath);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.Ticks);
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to record visit for folder {FolderPath} in {Method}", folderPath, nameof(RecordVisitAsync));
        }
    }

    public async Task<IReadOnlyList<FrequentFolder>> GetTopFoldersAsync(int count = 10)
    {
        var folders = new List<FrequentFolder>();
        try
        {
            await _connectionLock.WaitAsync();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    SELECT path, name, visit_count, last_visited
                    FROM frequent_folders
                    ORDER BY visit_count DESC, last_visited DESC
                    LIMIT @count
                    """;
                cmd.Parameters.AddWithValue("@count", count);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    folders.Add(new FrequentFolder(
                        Path: reader.GetString(0),
                        Name: reader.GetString(1),
                        VisitCount: reader.GetInt32(2),
                        LastVisited: new DateTime(reader.GetInt64(3), DateTimeKind.Utc).ToLocalTime()
                    ));
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get top folders in {Method}", nameof(GetTopFoldersAsync));
        }

        return folders.Where(folder => Directory.Exists(folder.Path)).ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _connection.Close();
        _connection.Dispose();
        _connectionLock.Dispose();
        _disposed = true;
    }
}
