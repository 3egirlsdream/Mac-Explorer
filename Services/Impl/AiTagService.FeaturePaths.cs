using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

public partial class AiTagService
{
    private void UpdateFeaturePaths(string oldPath, string newPath, SqliteTransaction transaction)
    {
        foreach (var table in new[] { "document_index", "document_segments" })
        {
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"UPDATE {table} SET path=@new||substr(path,length(@old)+1) WHERE path=@old OR substr(path,1,length(@prefix))=@prefix";
            command.Parameters.AddWithValue("@new", newPath); command.Parameters.AddWithValue("@old", oldPath); command.Parameters.AddWithValue("@prefix", oldPath.TrimEnd('/') + "/");
            command.ExecuteNonQuery();
        }
    }
    private void DeleteFeaturePaths(string path, SqliteTransaction transaction, bool children)
    {
        foreach (var table in new[] { "document_index", "document_segments" })
        {
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE path=@p" + (children ? " OR substr(path,1,length(@prefix))=@prefix" : "");
            command.Parameters.AddWithValue("@p", path.TrimEnd('/')); if (children) command.Parameters.AddWithValue("@prefix", path.TrimEnd('/') + "/"); command.ExecuteNonQuery();
        }
    }
}
