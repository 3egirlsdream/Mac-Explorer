using MacExplorer.Models;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

internal static class FileTagSchema
{
    internal static void Ensure(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var ownedTransaction = transaction == null ? connection.BeginTransaction() : null;
        transaction ??= ownedTransaction;
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS file_tags (
                file_path TEXT NOT NULL, tag TEXT NOT NULL, is_system INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL, PRIMARY KEY(file_path, tag));
            CREATE INDEX IF NOT EXISTS idx_file_tags_tag ON file_tags(tag, is_system);
            CREATE INDEX IF NOT EXISTS idx_file_tags_tag_nocase ON file_tags(tag COLLATE NOCASE, file_path);
            CREATE INDEX IF NOT EXISTS idx_file_tags_path ON file_tags(file_path);
            CREATE TABLE IF NOT EXISTS tag_definitions (
                name TEXT PRIMARY KEY COLLATE NOCASE, color_id INTEGER NOT NULL DEFAULT 0,
                is_pinned INTEGER NOT NULL DEFAULT 0, sort_order INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS pending_tag_changes (
                file_path TEXT NOT NULL, tag TEXT NOT NULL COLLATE NOCASE,
                applied INTEGER NOT NULL, color_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(file_path, tag));
            INSERT OR IGNORE INTO tag_definitions(name)
                SELECT DISTINCT tag FROM file_tags;
            """);
        bool migrateHierarchy;
        using (var columns = connection.CreateCommand())
        {
            columns.Transaction = transaction;
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tag_definitions') WHERE name = 'parent_name'";
            migrateHierarchy = Convert.ToInt32(columns.ExecuteScalar()) == 0;
        }
        if (migrateHierarchy)
            Execute(connection, transaction, "ALTER TABLE tag_definitions ADD COLUMN parent_name TEXT COLLATE NOCASE");
        using (var columns = connection.CreateCommand())
        {
            columns.Transaction = transaction;
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tag_definitions') WHERE name = 'display_name_override'";
            if (Convert.ToInt32(columns.ExecuteScalar()) == 0)
                Execute(connection, transaction, "ALTER TABLE tag_definitions ADD COLUMN display_name_override TEXT");
        }
        Execute(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_tag_definitions_parent ON tag_definitions(parent_name, sort_order)");
        var names = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT DISTINCT tag FROM file_tags";
            using var reader = command.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        foreach (var oldName in names)
        {
            var normalized = FileTagCatalog.NormalizeName(oldName);
            Execute(connection, transaction, "INSERT OR IGNORE INTO tag_definitions(name) VALUES (@name)", ("@name", normalized));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM tag_definitions WHERE name = @name COLLATE NOCASE";
            command.Parameters.AddWithValue("@name", normalized);
            var canonical = (string)command.ExecuteScalar()!;
            if (oldName == canonical) continue;
            Execute(connection, transaction, """
                INSERT OR IGNORE INTO file_tags(file_path, tag, is_system, created_at)
                    SELECT file_path, @new, @system, created_at FROM file_tags WHERE tag = @old COLLATE BINARY;
                DELETE FROM file_tags WHERE tag = @old COLLATE BINARY;
                """, ("@new", canonical), ("@old", oldName), ("@system", FileTagCatalog.TryGetFinderColor(canonical, out _) ? 1 : 0));
            if (!string.Equals(oldName, canonical, StringComparison.OrdinalIgnoreCase))
                Execute(connection, transaction, "DELETE FROM tag_definitions WHERE name = @old", ("@old", oldName));
        }
        foreach (var tag in FileTagCatalog.FinderColors)
            Execute(connection, transaction,
                "INSERT INTO tag_definitions(name, color_id) VALUES (@name, @color) ON CONFLICT(name) DO UPDATE SET color_id = excluded.color_id",
                ("@name", tag.Name), ("@color", tag.ColorId));
        if (migrateHierarchy)
        {
            // Freeze the former presentation order before enabling manual sibling ordering.
            Execute(connection, transaction, """
                WITH ordered AS (
                    SELECT d.name, ROW_NUMBER() OVER (ORDER BY d.is_pinned DESC,
                        CASE WHEN d.is_pinned = 1 THEN d.sort_order ELSE 0 END,
                        CASE d.name WHEN '红色' THEN 0 WHEN '橙色' THEN 1 WHEN '黄色' THEN 2
                            WHEN '绿色' THEN 3 WHEN '蓝色' THEN 4 WHEN '紫色' THEN 5 WHEN '灰色' THEN 6 ELSE 7 END,
                        CASE WHEN d.is_pinned = 1 THEN 0 ELSE (SELECT COUNT(*) FROM file_tags f WHERE f.tag = d.name COLLATE NOCASE) END DESC,
                        d.name COLLATE NOCASE) - 1 AS position FROM tag_definitions d)
                UPDATE tag_definitions SET sort_order = (SELECT position FROM ordered WHERE ordered.name = tag_definitions.name);
                """);
        }
        ownedTransaction?.Commit();
    }

    internal static void MigrateCollections(SqliteConnection connection, SqliteTransaction transaction)
    {
        Ensure(connection, transaction);
        var collections = new List<(int Id, string Name, int Order)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, name, sort_order FROM collections ORDER BY sort_order, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                collections.Add((reader.GetInt32(0), FileTagCatalog.NormalizeName(reader.GetString(1)), reader.GetInt32(2)));
        }
        foreach (var (id, name, order) in collections)
        {
            if (name.Length == 0) continue;
            var system = FileTagCatalog.TryGetFinderColor(name, out var color);
            Execute(connection, transaction, """
                INSERT INTO tag_definitions(name, color_id, is_pinned, sort_order) VALUES (@name, @color, 1, @order)
                ON CONFLICT(name) DO UPDATE SET is_pinned = 1,
                    sort_order = CASE WHEN tag_definitions.is_pinned = 1 THEN MIN(tag_definitions.sort_order, excluded.sort_order) ELSE excluded.sort_order END;
                INSERT OR IGNORE INTO file_tags(file_path, tag, is_system, created_at)
                    SELECT file_path, (SELECT name FROM tag_definitions WHERE name = @name), @system, added_at
                    FROM collection_items WHERE collection_id = @id;
                INSERT OR IGNORE INTO pending_tag_changes(file_path, tag, applied, color_id)
                    SELECT file_path, @name, 1, (SELECT color_id FROM tag_definitions WHERE name = @name)
                    FROM collection_items WHERE collection_id = @id;
                """, ("@name", name), ("@color", system ? color.ColorId : 0),
                ("@order", order), ("@system", system ? 1 : 0), ("@id", id));
        }
    }

    internal static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }
}
