using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

internal static class FeatureUpgradeSchema
{
    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS directory_preferences(path TEXT PRIMARY KEY,preferences TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS document_index(path TEXT PRIMARY KEY,modified INTEGER NOT NULL,size INTEGER NOT NULL,status TEXT NOT NULL,error TEXT);
            CREATE TABLE IF NOT EXISTS document_segments(path TEXT NOT NULL,ordinal INTEGER NOT NULL,text TEXT NOT NULL,location TEXT NOT NULL,PRIMARY KEY(path,ordinal));
            """;
        command.ExecuteNonQuery();
    }
}
