using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

public static class SqliteSchema
{
    public const int CurrentVersion = 7;

    /// <summary>
    /// Whether FTS5 is available (set during Initialize).
    /// </summary>
    public static bool IsFts5Available { get; private set; }

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

        // Create FTS5 virtual table — best-effort with tokenizer fallback
        IsFts5Available = TryCreateFts5(connection, transaction);

        if (IsFts5Available)
        {
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
        }

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

        // Create icon cache table for .app icons
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS icon_cache (
                app_path TEXT PRIMARY KEY,
                icon_base64 TEXT NOT NULL,
                modified_at INTEGER NOT NULL DEFAULT 0
            )
            """, transaction);

        // Create schema_version table
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY
            )
            """, transaction);

        // Run migrations based on current stored version
        var storedVersion = GetStoredVersion(connection, transaction);
        if (storedVersion < 2)
            MigrateToV2(connection, transaction);
        if (storedVersion < 3)
            MigrateToV3(connection, transaction);
        if (storedVersion < 4)
            MigrateToV4(connection, transaction);
        if (storedVersion < 5)
            MigrateToV5(connection, transaction);
        if (storedVersion < 6)
            MigrateToV6(connection, transaction);
        if (storedVersion < 7)
            MigrateToV7(connection, transaction);

        // Record current version
        ExecuteNonQuery(connection, """
            INSERT OR REPLACE INTO schema_version (version) VALUES (@version)
            """, transaction,
            ("@version", CurrentVersion));

        transaction.Commit();
    }

    private static int GetStoredVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT MAX(version) FROM schema_version";
            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
                return Convert.ToInt32(result);
        }
        catch { }
        return 0;
    }

    private static void MigrateToV2(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS collections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                icon TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL
            )
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS collection_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                collection_id INTEGER NOT NULL,
                file_path TEXT NOT NULL,
                added_at INTEGER NOT NULL,
                UNIQUE(collection_id, file_path)
            )
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_ci_collection_id ON collection_items(collection_id)
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_ci_file_path ON collection_items(file_path)
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS file_ratings (
                file_path TEXT PRIMARY KEY,
                rating INTEGER NOT NULL DEFAULT 0,
                updated_at INTEGER NOT NULL
            )
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_fr_rating ON file_ratings(rating)
            """, transaction);

        System.Diagnostics.Debug.WriteLine("Schema migrated to v2: collections, collection_items, file_ratings");
    }

    private static void MigrateToV3(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """, transaction);

        System.Diagnostics.Debug.WriteLine("Schema migrated to v3: app_settings");
    }

    private static void MigrateToV4(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS frequent_folders (
                path TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                visit_count INTEGER NOT NULL DEFAULT 1,
                last_visited INTEGER NOT NULL
            )
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_ff_visit_count ON frequent_folders(visit_count DESC)
            """, transaction);

        System.Diagnostics.Debug.WriteLine("Schema migrated to v4: frequent_folders");
    }

    private static void MigrateToV5(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS pinned_folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                folder_path TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                pinned_at INTEGER NOT NULL
            )
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_pf_sort_order ON pinned_folders(sort_order)
            """, transaction);

        System.Diagnostics.Debug.WriteLine("Schema migrated to v5: pinned_folders");
    }

    private static void MigrateToV6(SqliteConnection connection, SqliteTransaction transaction)
    {
        // AI analysis status — tracks which files have been analyzed (per-file granularity for resume)
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS ai_analysis_status (
                file_path TEXT PRIMARY KEY,
                file_modified_at INTEGER NOT NULL,
                analyzed_at INTEGER NOT NULL,
                analysis_version INTEGER NOT NULL DEFAULT 1
            )
            """, transaction);

        // AI tags — stores all AI-detected tags
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS ai_tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                tag_type TEXT NOT NULL,
                tag_value TEXT NOT NULL,
                confidence REAL NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL
            )
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_ai_tags_file_path ON ai_tags(file_path)
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_ai_tags_type_value ON ai_tags(tag_type, tag_value)
            """, transaction);

        // AI tags FTS5 for full-text search on tag values
        TryCreateAiTagsFts5(connection, transaction);

        // Face observations — raw face detection data per image
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS face_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                cluster_id INTEGER,
                bounding_box_x REAL NOT NULL,
                bounding_box_y REAL NOT NULL,
                bounding_box_w REAL NOT NULL,
                bounding_box_h REAL NOT NULL,
                feature_print BLOB,
                created_at INTEGER NOT NULL
            )
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_fo_file_path ON face_observations(file_path)
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS idx_fo_cluster_id ON face_observations(cluster_id)
            """, transaction);

        // Face clusters — named face groups
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS face_clusters (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                display_name TEXT,
                representative_face_id INTEGER,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """, transaction);

        System.Diagnostics.Debug.WriteLine("Schema migrated to v6: ai_analysis_status, ai_tags, ai_tags_fts, face_observations, face_clusters");
    }

    private static void MigrateToV7(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS open_with_apps (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                bundle_id TEXT NOT NULL UNIQUE,
                label TEXT NOT NULL,
                is_top_level INTEGER NOT NULL DEFAULT 1,
                sort_order INTEGER NOT NULL DEFAULT 0,
                icon_base64 TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """, transaction);

        // Insert default editors
        var defaults = new[]
        {
            ("com.microsoft.VSCode", "VS Code", 1, 0),
            ("com.todesktop.230313mzl4w4u92", "Cursor", 1, 1),
            ("dev.kiro.desktop", "Kiro", 1, 2),
            ("com.qoder.ide", "Qoder", 1, 3),
        };

        foreach (var (bundleId, label, isTopLevel, sortOrder) in defaults)
        {
            ExecuteNonQuery(connection, """
                INSERT OR IGNORE INTO open_with_apps (bundle_id, label, is_top_level, sort_order)
                VALUES (@bundleId, @label, @isTopLevel, @sortOrder)
                """, transaction,
                ("@bundleId", bundleId),
                ("@label", label),
                ("@isTopLevel", isTopLevel),
                ("@sortOrder", sortOrder));
        }

        System.Diagnostics.Debug.WriteLine("Schema migrated to v7: open_with_apps");
    }

    private static void TryCreateAiTagsFts5(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!IsFts5Available) return;

        // Check if already exists
        try
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM ai_tags_fts LIMIT 0";
            check.ExecuteNonQuery();
            return;
        }
        catch { }

        string[] tokenizers =
        [
            "tokenize='unicode61 categories L* N* Co'",
            "tokenize='unicode61'",
            "tokenize='ascii'"
        ];

        bool created = false;
        foreach (var tokenizer in tokenizers)
        {
            try
            {
                ExecuteNonQuery(connection, $"""
                    CREATE VIRTUAL TABLE IF NOT EXISTS ai_tags_fts USING fts5(
                        tag_value,
                        content='ai_tags',
                        content_rowid='id',
                        {tokenizer}
                    )
                    """, transaction);
                created = true;
                break;
            }
            catch { }
        }

        if (!created)
        {
            try
            {
                ExecuteNonQuery(connection, """
                    CREATE VIRTUAL TABLE IF NOT EXISTS ai_tags_fts USING fts5(
                        tag_value,
                        content='ai_tags',
                        content_rowid='id'
                    )
                    """, transaction);
            }
            catch { return; }
        }

        // Create triggers to keep FTS in sync
        ExecuteNonQuery(connection, """
            CREATE TRIGGER IF NOT EXISTS ai_tags_ai AFTER INSERT ON ai_tags BEGIN
                INSERT INTO ai_tags_fts(rowid, tag_value) VALUES (new.id, new.tag_value);
            END
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE TRIGGER IF NOT EXISTS ai_tags_ad AFTER DELETE ON ai_tags BEGIN
                INSERT INTO ai_tags_fts(ai_tags_fts, rowid, tag_value) VALUES ('delete', old.id, old.tag_value);
            END
            """, transaction);

        ExecuteNonQuery(connection, """
            CREATE TRIGGER IF NOT EXISTS ai_tags_au AFTER UPDATE ON ai_tags BEGIN
                INSERT INTO ai_tags_fts(ai_tags_fts, rowid, tag_value) VALUES ('delete', old.id, old.tag_value);
                INSERT INTO ai_tags_fts(rowid, tag_value) VALUES (new.id, new.tag_value);
            END
            """, transaction);
    }

    /// <summary>
    /// Attempt to create FTS5 virtual table with progressive tokenizer fallback.
    /// Returns true if FTS5 is available (either already existed or was just created).
    /// </summary>
    private static bool TryCreateFts5(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Check if already exists
        try
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM files_fts LIMIT 0";
            check.ExecuteNonQuery();
            return true; // Already exists
        }
        catch { /* Doesn't exist yet, try to create */ }

        // Try tokenizers from most capable to most basic
        string[] tokenizers =
        [
            "tokenize='unicode61 categories L* N* Co'",  // Best: Unicode-aware with categories
            "tokenize='unicode61'",                       // Good: Unicode-aware
            "tokenize='ascii'"                            // Basic: ASCII only
        ];

        foreach (var tokenizer in tokenizers)
        {
            try
            {
                ExecuteNonQuery(connection, $"""
                    CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
                        name,
                        path,
                        content='files',
                        content_rowid='rowid',
                        {tokenizer}
                    )
                    """, transaction);
                System.Diagnostics.Debug.WriteLine($"FTS5 created with {tokenizer}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FTS5 with {tokenizer} failed: {ex.Message}");
            }
        }

        // Last resort: FTS5 without any tokenize option
        try
        {
            ExecuteNonQuery(connection, """
                CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
                    name,
                    path,
                    content='files',
                    content_rowid='rowid'
                )
                """, transaction);
            System.Diagnostics.Debug.WriteLine("FTS5 created with default tokenizer");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FTS5 completely unavailable: {ex.Message}");
            return false;
        }
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
