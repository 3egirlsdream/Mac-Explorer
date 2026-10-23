using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

internal static class DatabaseSpaceMaintenance
{
    internal const long JournalSizeLimit = 16L * 1024 * 1024;

    internal static void Configure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // These pragmas are connection-local, including when a pooled handle is reused.
        command.CommandText = $"PRAGMA journal_size_limit={JournalSizeLimit}; PRAGMA wal_autocheckpoint=1000;";
        command.ExecuteNonQuery();
    }

    internal static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1
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

    // Online calls only truncate reusable WAL capacity. Full compaction runs after
    // the UI/search workers have stopped, or on an exclusively held legacy profile.
    internal static bool Reclaim(string path, bool compact, long minimumFreeBytes = 64L * 1024 * 1024)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var connection = Open(path);
            Execute(connection, "PRAGMA busy_timeout=0");
            Configure(connection);
            using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                using var reader = checkpoint.ExecuteReader();
                if (reader.Read() && reader.GetInt32(0) != 0) return false;
            }
            if (compact)
            {
                if (Scalar(connection, "SELECT count(*) FROM pragma_table_info('search_entries') WHERE name='parent_id'") > 0)
                    Execute(connection, "DELETE FROM search_directories WHERE NOT EXISTS (SELECT 1 FROM search_entries WHERE parent_id=search_directories.id)");
                Compact(connection, minimumFreeBytes);
            }
            return true;
        }
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
}
