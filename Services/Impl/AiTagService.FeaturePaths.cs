using MacExplorer.Indexing;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

public partial class AiTagService
{
    private void UpdateFeaturePaths(string oldPath, string newPath, SqliteTransaction transaction)
    {
        foreach (var table in new[] { "document_index", "document_segments" })
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE {table} SET path = @new || substr(path, length(@root) + 1)
                WHERE {SqlitePathScope.Match("path")}
                """;
            command.Parameters.AddWithValue("@new", newPath);
            SqlitePathScope.Bind(command, oldPath);
            command.ExecuteNonQuery();
        }
    }
    private void DeleteFeaturePaths(string path, SqliteTransaction transaction, bool children)
    {
        var predicate = children ? SqlitePathScope.Match("path") : "path = @root";
        foreach (var table in new[] { "document_index", "document_segments" })
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE {predicate}";
            if (children) SqlitePathScope.Bind(command, path);
            else command.Parameters.AddWithValue("@root", path.TrimEnd('/'));
            command.ExecuteNonQuery();
        }
    }
}
