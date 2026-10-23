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
        CREATE TABLE IF NOT EXISTS search_directories (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE COLLATE BINARY,
            parent_key TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS search_entries (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE COLLATE BINARY,
            name TEXT NOT NULL,
            parent_id INTEGER NOT NULL REFERENCES search_directories(id),
            extension TEXT NOT NULL,
            extension_key TEXT NOT NULL,
            name_key TEXT NOT NULL,
            initials_key TEXT NOT NULL,
            size INTEGER NOT NULL,
            is_directory INTEGER NOT NULL,
            is_hidden INTEGER NOT NULL,
            is_symbolic_link INTEGER NOT NULL,
            created_ticks INTEGER NOT NULL,
            modified_ticks INTEGER NOT NULL,
            scan_id TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS search_entries_parent ON search_entries(parent_id);
        CREATE INDEX IF NOT EXISTS search_entries_extension ON search_entries(extension_key);
        CREATE VIEW IF NOT EXISTS search_entry_details AS
            SELECT e.*, d.path AS parent_path, d.parent_key
            FROM search_entries e JOIN search_directories d ON d.id=e.parent_id;
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
            name_key, initials_key, content='search_entries', content_rowid='id',
            tokenize='trigram case_sensitive 1'
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS search_paths USING fts5(
            parent_key, content='search_directories', content_rowid='id',
            tokenize='trigram case_sensitive 1'
        );
        CREATE TRIGGER IF NOT EXISTS search_directories_ai AFTER INSERT ON search_directories BEGIN
            INSERT INTO search_paths(rowid,parent_key) VALUES(new.id,new.parent_key);
        END;
        CREATE TRIGGER IF NOT EXISTS search_directories_ad AFTER DELETE ON search_directories BEGIN
            INSERT INTO search_paths(search_paths,rowid,parent_key) VALUES('delete',old.id,old.parent_key);
        END;
        CREATE TRIGGER IF NOT EXISTS search_entries_ai AFTER INSERT ON search_entries BEGIN
            INSERT INTO search_names(rowid, name_key, initials_key)
            VALUES(new.id, new.name_key, new.initials_key);
        END;
        CREATE TRIGGER IF NOT EXISTS search_entries_ad AFTER DELETE ON search_entries BEGIN
            INSERT INTO search_names(search_names, rowid, name_key, initials_key)
            VALUES('delete', old.id, old.name_key, old.initials_key);
        END;
        CREATE TRIGGER IF NOT EXISTS search_entries_au
        AFTER UPDATE OF name_key, initials_key ON search_entries
        WHEN old.name_key IS NOT new.name_key OR old.initials_key IS NOT new.initials_key BEGIN
            INSERT INTO search_names(search_names, rowid, name_key, initials_key)
            VALUES('delete', old.id, old.name_key, old.initials_key);
            INSERT INTO search_names(rowid, name_key, initials_key)
            VALUES(new.id, new.name_key, new.initials_key);
        END;
        """;

    public static bool Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cache_size";
        var previousCache = Convert.ToInt64(command.ExecuteScalar());
        // The shared parent lookup otherwise thrashes SQLite's 2 MiB default cache
        // when migrating millions of entries. This budget exists only during setup.
        command.CommandText = "PRAGMA cache_size=-65536";
        command.ExecuteNonQuery();
        try { return InitializeCore(connection); }
        finally
        {
            command.CommandText = $"PRAGMA cache_size={previousCache}; PRAGMA shrink_memory";
            command.ExecuteNonQuery();
        }
    }

    private static bool InitializeCore(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT count(*) FROM pragma_table_info('search_entries') WHERE name='parent_path'";
        var legacy = (long)command.ExecuteScalar()! != 0;
        if (legacy)
        {
            // This entire conversion, including both FTS rebuilds, commits atomically.
            // User profile tables and existing search row IDs are untouched.
            command.CommandText = """
                DROP TRIGGER IF EXISTS search_entries_ai;
                DROP TRIGGER IF EXISTS search_entries_ad;
                DROP TRIGGER IF EXISTS search_entries_au;
                DROP TRIGGER IF EXISTS search_entries_replaced_directory;
                DROP TABLE IF EXISTS search_names;
                DROP INDEX IF EXISTS search_entries_parent;
                DROP INDEX IF EXISTS search_entries_extension;
                ALTER TABLE search_entries RENAME TO search_entries_legacy;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = TablesSql;
        command.ExecuteNonQuery();
        if (legacy)
        {
            command.CommandText = """
                DROP INDEX search_entries_parent;
                DROP INDEX search_entries_extension;
                INSERT INTO search_directories(path,parent_key)
                    SELECT parent_path,MIN(parent_key) FROM search_entries_legacy GROUP BY parent_path COLLATE BINARY;
                INSERT INTO search_entries(id,path,name,parent_id,extension,extension_key,name_key,initials_key,
                    size,is_directory,is_hidden,is_symbolic_link,created_ticks,modified_ticks,scan_id)
                    SELECT e.id,e.path,e.name,d.id,e.extension,e.extension_key,e.name_key,e.initials_key,
                        e.size,e.is_directory,e.is_hidden,e.is_symbolic_link,e.created_ticks,e.modified_ticks,e.scan_id
                    FROM search_entries_legacy e JOIN search_directories d ON d.path=e.parent_path COLLATE BINARY;
                DROP TABLE search_entries_legacy;
                CREATE INDEX search_entries_parent ON search_entries(parent_id);
                CREATE INDEX search_entries_extension ON search_entries(extension_key);
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='search_names'";
        var existed = (long)command.ExecuteScalar()! != 0;
        command.CommandText = "SAVEPOINT search_fts";
        command.ExecuteNonQuery();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = FtsSql;
            cmd.ExecuteNonQuery();
            if (!existed)
            {
                cmd.CommandText = """
                    INSERT INTO search_names(search_names) VALUES('rebuild');
                    INSERT INTO search_paths(search_paths) VALUES('rebuild');
                    """;
                cmd.ExecuteNonQuery();
            }
            command.CommandText = "RELEASE search_fts";
            command.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
        catch (SqliteException ex) when (!existed && ex.SqliteErrorCode == 1 &&
            (ex.Message.Contains("tokenizer", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("no such module", StringComparison.OrdinalIgnoreCase)))
        {
            command.CommandText = "ROLLBACK TO search_fts; RELEASE search_fts";
            command.ExecuteNonQuery();
            tx.Commit();
            // Older SQLite builds can still use the exact scoped SQL predicates.
            // Corruption, permission failures and busy errors must NOT look like empty results.
            return false;
        }
    }
}
