using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

internal readonly record struct DatabaseStorageStatus(
    long DatabaseBytes,
    long WalBytes,
    long FreeBytes,
    long PageSize,
    long PageCount,
    long FreePages,
    int AutoVacuumMode)
{
    internal long TotalBytes => DatabaseBytes + WalBytes;
    internal bool IsIncremental => AutoVacuumMode == 2;
    internal bool NeedsOneTimeConversion => !IsIncremental
        && FreePages > 0
        && (FreeBytes >= 128L * 1024 * 1024
            || (FreeBytes >= 64L * 1024 * 1024 && FreePages >= PageCount / 10));
}

internal enum DatabaseOptimizationStage
{
    Preparing,
    Compacting,
    Finalizing,
    Completed
}

internal readonly record struct DatabaseOptimizationProgress(
    DatabaseOptimizationStage Stage,
    double Percentage,
    bool IsIndeterminate = false);

internal readonly record struct DatabaseOptimizationResult(
    long BeforeBytes,
    long AfterBytes,
    long FreedBytes,
    bool IsIncremental);

internal static class DatabaseSpaceMaintenance
{
    internal const long JournalSizeLimit = 16L * 1024 * 1024;
    internal const int IncrementalVacuumPagesPerTick = 512;
    internal const int ExitIncrementalVacuumPages = 256;

    internal static void Configure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // These pragmas are connection-local, including when a pooled handle is reused.
        command.CommandText = $"PRAGMA journal_size_limit={JournalSizeLimit}; PRAGMA wal_autocheckpoint=1000;";
        command.ExecuteNonQuery();
    }

    internal static SqliteConnection Open(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    internal static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static DatabaseStorageStatus? Inspect(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var connection = Open(path, readOnly: true);
            Execute(connection, "PRAGMA busy_timeout=100");
            var pageSize = Scalar(connection, "PRAGMA page_size");
            var pageCount = Scalar(connection, "PRAGMA page_count");
            var freePages = Scalar(connection, "PRAGMA freelist_count");
            var autoVacuum = (int)Scalar(connection, "PRAGMA auto_vacuum");
            var dbBytes = FileLength(path);
            var walBytes = FileLength(path + "-wal");
            return new DatabaseStorageStatus(dbBytes, walBytes, freePages * pageSize, pageSize, pageCount, freePages, autoVacuum);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Database inspection deferred: {ex.Message}");
            return null;
        }
    }

    // Strictly bounded exit maintenance: never runs a full-database VACUUM.
    internal static bool ReclaimFastOnExit(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var connection = Open(path);
            Execute(connection, "PRAGMA busy_timeout=0");
            Configure(connection);
            Execute(connection, "PRAGMA wal_checkpoint(PASSIVE)");
            if (Scalar(connection, "PRAGMA auto_vacuum") == 2 && Scalar(connection, "PRAGMA freelist_count") > 0)
            {
                Execute(connection, $"PRAGMA incremental_vacuum({ExitIncrementalVacuumPages})");
                Execute(connection, "PRAGMA wal_checkpoint(PASSIVE)");
            }
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Fast exit database maintenance skipped: {ex.Message}");
            return false;
        }
    }

    // Online background maintenance: small incremental steps and cancellable WAL checkpoints.
    internal static bool ReclaimIncremental(string path, int maxPages = IncrementalVacuumPagesPerTick,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || cancellationToken.IsCancellationRequested) return false;
        try
        {
            using var connection = Open(path);
            using var interruption = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
            cancellationToken.ThrowIfCancellationRequested();
            Execute(connection, "PRAGMA busy_timeout=0");
            Configure(connection);
            var autoVacuum = Scalar(connection, "PRAGMA auto_vacuum");
            if (autoVacuum == 2)
            {
                if (Scalar(connection, "PRAGMA freelist_count") > 0 && maxPages > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Execute(connection, $"PRAGMA incremental_vacuum({maxPages})");
                }
                cancellationToken.ThrowIfCancellationRequested();
                var mode = FileLength(path + "-wal") > JournalSizeLimit ? "TRUNCATE" : "PASSIVE";
                return Checkpoint(connection, mode);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Checkpoint(connection, "TRUNCATE");
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Incremental database maintenance deferred: {ex.Message}");
            return false;
        }
    }

    internal static Task<DatabaseOptimizationResult> ConvertToIncrementalAsync(string path,
        IProgress<DatabaseOptimizationProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => ConvertToIncrementalCore(path, progress, cancellationToken), cancellationToken);

    private static DatabaseOptimizationResult ConvertToIncrementalCore(string path,
        IProgress<DatabaseOptimizationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new DatabaseOptimizationResult(0, 0, 0, true);
        var beforeBytes = FileLength(path) + FileLength(path + "-wal");
        progress?.Report(new(DatabaseOptimizationStage.Preparing, 10, IsIndeterminate: true));

        using var connection = Open(path);
        using var interruption = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute(connection, "PRAGMA busy_timeout=5000");
            Configure(connection);

            if (Scalar(connection, "SELECT count(*) FROM pragma_table_info('search_entries') WHERE name='parent_id'") > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Execute(connection, "DELETE FROM search_directories WHERE NOT EXISTS (SELECT 1 FROM search_entries WHERE parent_id=search_directories.id)");
            }

            var autoVacuum = Scalar(connection, "PRAGMA auto_vacuum");
            if (autoVacuum == 2)
            {
                var initialFree = Scalar(connection, "PRAGMA freelist_count");
                if (initialFree > 0)
                {
                    const int chunk = 1024;
                    long remaining = initialFree;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var doneRatio = Math.Clamp(1.0 - (double)remaining / initialFree, 0, 1);
                        progress?.Report(new(DatabaseOptimizationStage.Compacting, 15 + doneRatio * 70, IsIndeterminate: false));
                        Execute(connection, $"PRAGMA incremental_vacuum({chunk})");
                        var next = Scalar(connection, "PRAGMA freelist_count");
                        if (next >= remaining) break;
                        remaining = next;
                    }
                }
                progress?.Report(new(DatabaseOptimizationStage.Finalizing, 90, IsIndeterminate: true));
                cancellationToken.ThrowIfCancellationRequested();
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            else
            {
                var pageSize = Scalar(connection, "PRAGMA page_size");
                var pages = Scalar(connection, "PRAGMA page_count");
                var required = pages * pageSize * 2 + 64L * 1024 * 1024;
                var volume = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(connection.DataSource))!);
                if (volume.AvailableFreeSpace < required)
                    throw new IOException(LocalizationText.Get("storage.optimize.insufficient_space"));

                progress?.Report(new(DatabaseOptimizationStage.Compacting, 35, IsIndeterminate: true));
                cancellationToken.ThrowIfCancellationRequested();
                Execute(connection, "PRAGMA auto_vacuum=INCREMENTAL");
                Execute(connection, "VACUUM");

                progress?.Report(new(DatabaseOptimizationStage.Finalizing, 85, IsIndeterminate: true));
                cancellationToken.ThrowIfCancellationRequested();
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            }

            var isIncremental = Scalar(connection, "PRAGMA auto_vacuum") == 2;
            var afterBytes = FileLength(path) + FileLength(path + "-wal");
            var freedBytes = Math.Max(0L, beforeBytes - afterBytes);
            progress?.Report(new(DatabaseOptimizationStage.Completed, 100, IsIndeterminate: false));
            return new DatabaseOptimizationResult(beforeBytes, afterBytes, freedBytes, isIncremental);
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    // Online calls only truncate reusable WAL capacity. Full compaction runs only
    // when explicitly requested (e.g. legacy migration or manual optimization).
    internal static bool Reclaim(string path, bool compact, long minimumFreeBytes = 64L * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || cancellationToken.IsCancellationRequested) return false;
        try
        {
            using var connection = Open(path);
            using var interruption = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
            cancellationToken.ThrowIfCancellationRequested();
            Execute(connection, "PRAGMA busy_timeout=0");
            Configure(connection);
            if (!Checkpoint(connection, "TRUNCATE")) return false;
            if (compact)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Scalar(connection, "SELECT count(*) FROM pragma_table_info('search_entries') WHERE name='parent_id'") > 0)
                    Execute(connection, "DELETE FROM search_directories WHERE NOT EXISTS (SELECT 1 FROM search_entries WHERE parent_id=search_directories.id)");
                Compact(connection, minimumFreeBytes);
            }
            return true;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Database maintenance deferred: {ex.Message}");
            return false;
        }
    }

    internal static bool Compact(SqliteConnection connection, long minimumFreeBytes)
    {
        var pageSize = Scalar(connection, "PRAGMA page_size");
        var pages = Scalar(connection, "PRAGMA page_count");
        var free = Scalar(connection, "PRAGMA freelist_count");
        if (free == 0 || free * pageSize < minimumFreeBytes || (minimumFreeBytes > 0 && free < pages / 10)) return false;
        // VACUUM is transactional, but needs scratch space. Never sacrifice the
        // original database or its WAL to make room for maintenance.
        var volume = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(connection.DataSource))!);
        if (volume.AvailableFreeSpace < pages * pageSize * 2 + 64L * 1024 * 1024) return false;
        Execute(connection, "VACUUM");
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        return true;
    }

    private static bool Checkpoint(SqliteConnection connection, string mode)
    {
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = $"PRAGMA wal_checkpoint({mode})";
        using var reader = checkpoint.ExecuteReader();
        return !reader.Read() || reader.GetInt32(0) == 0;
    }

    private static long FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
