using Microsoft.Data.Sqlite;
using MacExplorer.Indexing;

namespace MacExplorer.Services.Impl;

internal static class ApplicationDataMigration
{
    // Keep the source intact. SQLite backup includes committed WAL pages, unlike File.Copy.
    internal static void CopyDatabaseIfAbsent(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + ".migration-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var old = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = staging, Pooling = false }.ToString()))
            {
                old.Open(); copy.Open(); old.BackupDatabase(copy);
                using var check = copy.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                if (!Equals(check.ExecuteScalar(), "ok")) throw new IOException("迁移数据库完整性检查失败。");
            }
            File.Move(staging, destination, overwrite: false);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(staging + suffix)) File.Delete(staging + suffix);
        }
    }

    internal static void Prepare()
    {
        Directory.CreateDirectory(RuntimePaths.DataDirectory);
        Directory.CreateDirectory(RuntimePaths.CacheDirectory);
        Directory.CreateDirectory(RuntimePaths.LogDirectory);
        if (RuntimePaths.TestRoot == null && !DistributionChannel.IsAppStore
            && Environment.GetEnvironmentVariable("MACEXPLORER_DB_PATH") == null)
        {
            // Fail rather than silently create an empty profile after a failed upgrade.
            CopyDatabaseIfAbsent(Path.Combine(RuntimePaths.HomeDirectory, "Documents", "MacExplorer", "index.db"),
                RuntimePaths.DatabasePath);
        }
        PrepareSearchStorage(RuntimePaths.DatabasePath);
    }

    internal static void PrepareSearchStorage(string path)
    {
        if (!File.Exists(path)) return;
        using (var connection = DatabaseSpaceMaintenance.Open(path))
        {
            if (DatabaseSpaceMaintenance.Scalar(connection,
                    "SELECT count(*) FROM pragma_table_info('search_entries') WHERE name='parent_path'") == 0) return;
            // A first upgrade can rewrite a large index. Finish it before any windows,
            // settings writers or background workers exist, never behind an interactive UI.
            DatabaseSpaceMaintenance.Configure(connection);
            SearchSchema.Initialize(connection);
        }
        // Reclamation is best effort: a busy reader or a failed VACUUM must not
        // turn a committed, usable schema upgrade into an application startup failure.
        DatabaseSpaceMaintenance.Reclaim(path, compact: true);
    }
}
