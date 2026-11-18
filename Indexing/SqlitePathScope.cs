using MacExplorer.Services.Search;
using Microsoft.Data.Sqlite;

namespace MacExplorer.Indexing;

internal static class SqlitePathScope
{
    internal static string Descendants(string column) =>
        $"{column} >= @prefix COLLATE BINARY AND {column} < @upper COLLATE BINARY";

    internal static string Match(string column) =>
        $"({column} = @root COLLATE BINARY OR ({Descendants(column)}))";

    internal static void Bind(SqliteCommand command, string root)
    {
        var directory = root.TrimEnd(Path.DirectorySeparatorChar);
        command.Parameters.AddWithValue("@root", root);
        command.Parameters.AddWithValue("@prefix", SearchPath.Prefix(directory));
        command.Parameters.AddWithValue("@upper", SearchPath.UpperBound(directory));
    }
}
