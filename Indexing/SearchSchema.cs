using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

/// <summary>
/// Search coverage must not depend on the evictable directory-view cache in `files`.
/// These derived tables live in the SAME application SQLite database. User tags,
/// collections and settings are neither migrated nor deleted when rebuilding search.
/// </summary>
public static class SearchSchema
{
    public const string TablesSql = """
        CREATE TABLE IF NOT EXISTS search_entries (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE COLLATE BINARY,
            name TEXT NOT NULL,
            parent_path TEXT NOT NULL COLLATE BINARY,
            extension TEXT NOT NULL,
            extension_key TEXT NOT NULL,
            name_key TEXT NOT NULL,
            initials_key TEXT NOT NULL,
            parent_key TEXT NOT NULL,
            size INTEGER NOT NULL,
            is_directory INTEGER NOT NULL,
            is_hidden INTEGER NOT NULL,
            is_symbolic_link INTEGER NOT NULL,
            created_ticks INTEGER NOT NULL,
            modified_ticks INTEGER NOT NULL,
            scan_id TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS search_entries_parent ON search_entries(parent_path);
        CREATE INDEX IF NOT EXISTS search_entries_extension ON search_entries(extension_key);
        CREATE TRIGGER IF NOT EXISTS search_entries_replaced_directory
        AFTER UPDATE OF is_directory,is_symbolic_link ON search_entries
        WHEN old.is_directory=1 AND (new.is_directory=0 OR (old.is_symbolic_link=0 AND new.is_symbolic_link=1)) BEGIN
            DELETE FROM search_entries
            WHERE path >= (new.path || '/') COLLATE BINARY AND path < (new.path || '0') COLLATE BINARY;
        END;
        CREATE TABLE IF NOT EXISTS search_roots (
            path TEXT PRIMARY KEY COLLATE BINARY,
            checkpoint TEXT NOT NULL DEFAULT '0',
            phase TEXT NOT NULL DEFAULT 'Building',
            updated_ticks INTEGER NOT NULL DEFAULT 0
        );
        """;

    public const string FtsSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS search_names USING fts5(
            name_key, initials_key, parent_key, content='search_entries', content_rowid='id',
            tokenize='trigram case_sensitive 1'
        );
        CREATE TRIGGER IF NOT EXISTS search_entries_ai AFTER INSERT ON search_entries BEGIN
            INSERT INTO search_names(rowid, name_key, initials_key, parent_key)
            VALUES(new.id, new.name_key, new.initials_key, new.parent_key);
        END;
        CREATE TRIGGER IF NOT EXISTS search_entries_ad AFTER DELETE ON search_entries BEGIN
            INSERT INTO search_names(search_names, rowid, name_key, initials_key, parent_key)
            VALUES('delete', old.id, old.name_key, old.initials_key, old.parent_key);
        END;
        CREATE TRIGGER IF NOT EXISTS search_entries_au
        AFTER UPDATE OF name_key, initials_key, parent_key ON search_entries
        WHEN old.name_key IS NOT new.name_key OR old.initials_key IS NOT new.initials_key
            OR old.parent_key IS NOT new.parent_key BEGIN
            INSERT INTO search_names(search_names, rowid, name_key, initials_key, parent_key)
            VALUES('delete', old.id, old.name_key, old.initials_key, old.parent_key);
            INSERT INTO search_names(rowid, name_key, initials_key, parent_key)
            VALUES(new.id, new.name_key, new.initials_key, new.parent_key);
        END;
        """;

    public static bool Initialize(SqliteConnection connection)
    {
        using (var cmd = connection.CreateCommand()) { cmd.CommandText = TablesSql; cmd.ExecuteNonQuery(); }
        bool existed;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE name='search_names'";
            existed = cmd.ExecuteScalar() != null;
        }
        using var tx = connection.BeginTransaction();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = FtsSql;
            cmd.ExecuteNonQuery();
            if (!existed)
            {
                cmd.CommandText = "INSERT INTO search_names(search_names) VALUES('rebuild')";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
        catch (SqliteException ex) when (!existed && ex.SqliteErrorCode == 1 &&
            (ex.Message.Contains("tokenizer", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("no such module", StringComparison.OrdinalIgnoreCase)))
        {
            tx.Rollback();
            // Older SQLite builds can still use the exact scoped SQL predicates.
            // Corruption, permission failures and busy errors must NOT look like empty results.
            return false;
        }
    }
}
