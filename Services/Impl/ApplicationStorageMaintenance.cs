using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

internal sealed class ApplicationStorageMaintenance : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    public void Start()
    {
        if (RuntimePaths.TestRoot != null || _worker != null) return;
        _worker = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
                if (!DistributionChannel.IsAppStore && Environment.GetEnvironmentVariable("MACEXPLORER_DB_PATH") == null)
                    RetireLegacyIndex(Path.Combine(RuntimePaths.HomeDirectory, "Documents", "MacExplorer", "index.db"),
                        RuntimePaths.DatabasePath, _stop.Token);
                CleanLegacyCaches(RuntimePaths.DataDirectory, RuntimePaths.CacheDirectory, DateTime.UtcNow.AddDays(-7));
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
                do { DatabaseSpaceMaintenance.Reclaim(RuntimePaths.DatabasePath, compact: false); }
                while (await timer.WaitForNextTickAsync(_stop.Token));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine($"Storage maintenance deferred: {ex.Message}"); }
        });
    }

    internal static bool RetireLegacyIndex(string source, string destination, CancellationToken ct = default)
    {
        if (!File.Exists(source) || !File.Exists(destination)
            || Canonical(source) == Canonical(destination)
            || new FileInfo(source).LinkTarget != null || new FileInfo(destination).LinkTarget != null) return false;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var sourceIdentity = Platforms.MacOS.MacSandboxNative.FileIdentity(source);
                var destinationIdentity = Platforms.MacOS.MacSandboxNative.FileIdentity(destination);
                // Different paths can still be hard links to the active database.
                if (sourceIdentity == null || destinationIdentity == null || sourceIdentity == destinationIdentity) return false;
            }
            using (var probe = DatabaseSpaceMaintenance.Open(source))
                if (IsProfile(probe) && DatabaseSpaceMaintenance.Scalar(probe,
                        "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('files','search_entries')") == 0
                    && DatabaseSpaceMaintenance.Scalar(probe, "PRAGMA freelist_count") == 0) return true;
            // Confirm the new profile is readable before changing anything in the old one.
            using var current = DatabaseSpaceMaintenance.Open(destination);
            using (var check = current.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check(1)";
                if (!Equals(check.ExecuteScalar(), "ok") || !IsProfile(current)) return false;
            }
            using var legacy = DatabaseSpaceMaintenance.Open(source);
            using var interruption = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(legacy.Handle));
            ct.ThrowIfCancellationRequested();
            if (!IsProfile(legacy)) return false;
            // Switching out of WAL requires exclusive access; an older running app
            // keeps its profile intact. Retain the exclusive lock through VACUUM.
            DatabaseSpaceMaintenance.Execute(legacy, "PRAGMA busy_timeout=0; PRAGMA locking_mode=EXCLUSIVE; PRAGMA journal_mode=DELETE;");
            using (var transaction = legacy.BeginTransaction())
            using (var command = legacy.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DROP VIEW IF EXISTS search_entry_details;
                    DROP TABLE IF EXISTS search_names;
                    DROP TABLE IF EXISTS search_paths;
                    DROP TABLE IF EXISTS search_entries;
                    DROP TABLE IF EXISTS search_directories;
                    DROP TABLE IF EXISTS search_roots;
                    DROP TABLE IF EXISTS files_fts;
                    DROP TABLE IF EXISTS files;
                    DROP TABLE IF EXISTS directories;
                    DROP TABLE IF EXISTS icon_cache;
                    """;
                command.ExecuteNonQuery();
                ct.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            // Everything not explicitly listed above is retained, including unknown
            // future tables, credentials, AI data, old settings and deleted favorites.
            DatabaseSpaceMaintenance.Compact(legacy, minimumFreeBytes: 0);
            return true;
        }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Legacy profile retained: {ex.Message}");
            return false;
        }
    }

    private static bool IsProfile(SqliteConnection connection) => DatabaseSpaceMaintenance.Scalar(connection,
        "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('schema_version','app_settings','file_tags')") == 3;

    private static string Canonical(string path) => OperatingSystem.IsMacOS()
        ? Platforms.MacOS.MacSandboxNative.RealPath(path) : Path.GetFullPath(path);

    internal static void CleanLegacyCaches(string dataDirectory, string cacheDirectory, DateTime olderThanUtc)
    {
        if (Canonical(dataDirectory) == Canonical(cacheDirectory)) return;
        foreach (var name in new[] { "thumbnail-cache", "icon-cache", "preview-cache" })
        {
            var directory = new DirectoryInfo(Path.Combine(dataDirectory, name));
            if (!directory.Exists || directory.LinkTarget != null) continue;
            try { Clean(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine($"Legacy cache cleanup deferred: {ex.Message}"); }
        }

        void Clean(DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget != null) continue;
                if (entry is DirectoryInfo child) Clean(child);
                else if (entry.LastWriteTimeUtc < olderThanUtc) entry.Delete();
            }
            if (!directory.EnumerateFileSystemInfos().Any()) directory.Delete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_worker != null) await _worker.ConfigureAwait(false);
    }
}
