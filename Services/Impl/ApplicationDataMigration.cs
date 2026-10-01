using Microsoft.Data.Sqlite;

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
        if (RuntimePaths.TestRoot != null || DistributionChannel.IsAppStore
            || Environment.GetEnvironmentVariable("MACEXPLORER_DB_PATH") != null) return;
        // Fail rather than silently create an empty profile after a failed upgrade.
        CopyDatabaseIfAbsent(Path.Combine(RuntimePaths.HomeDirectory, "Documents", "MacExplorer", "index.db"),
            RuntimePaths.DatabasePath);
    }
}
