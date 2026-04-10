using Microsoft.Data.Sqlite;

namespace FKFinder.Indexing;

public static class SqliteSchema
{
    public const int CurrentVersion = 1;

    public static void Initialize(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        // Create files table
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS files (
                path TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                extension TEXT,
                parent_path TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                is_directory INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL DEFAULT 0,
                modified_at INTEGER NOT NULL DEFAULT 0,
                content_type TEXT,
                is_hidden INTEGER NOT NULL DEFAULT 0,
                indexed_at INTEGER NOT NULL DEFAULT 0
            )
            """, transaction);

        // Create indexes
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_files_parent_path ON files(parent_path)
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_files_name ON files(name)
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_files_is_directory ON files(is_directory)
            """, transaction);

        // Create FTS5 virtual table for full-text search
        ExecuteNonQuery(connection, """
            CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
                name,
                path,
                content='files',
                content_rowid='rowid',
                tokenize='unicode61 categories L* N* Co'
            )
            """, transaction);

        // Create triggers to keep FTS in sync with files table
        ExecuteNonQuery(connection, """
            CREATE TRIGGER IF NOT EXISTS files_ai AFTER INSERT ON files BEGIN
                INSERT INTO files_fts(rowid, name, path) VALUES (new.rowid, new.name, new.path);
            END
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE TRIGGER IF NOT EXISTS files_ad AFTER DELETE ON files BEGIN
                INSERT INTO files_fts(files_fts, rowid, name, path) VALUES ('delete', old.rowid, old.name, old.path);
            END
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE TRIGGER IF NOT EXISTS files_au AFTER UPDATE ON files BEGIN
                INSERT INTO files_fts(files_fts, rowid, name, path) VALUES ('delete', old.rowid, old.name, old.path);
                INSERT INTO files_fts(rowid, name, path) VALUES (new.rowid, new.name, new.path);
            END
            """, transaction);

        // Create directories table
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS directories (
                path TEXT PRIMARY KEY,
                file_count INTEGER NOT NULL DEFAULT 0,
                total_size INTEGER NOT NULL DEFAULT 0,
                last_scanned INTEGER NOT NULL DEFAULT 0,
                scan_status TEXT NOT NULL DEFAULT 'pending'
            )
            """, transaction);

        // Create schema_version table
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY
            )
            """, transaction);

        // Record current version
        ExecuteNonQuery(connection, """
            INSERT OR REPLACE INTO schema_version (version) VALUES (@version)
            """, transaction,
            ("@version", CurrentVersion));

        transaction.Commit();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql, SqliteTransaction transaction, params (string name, object value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }
}
