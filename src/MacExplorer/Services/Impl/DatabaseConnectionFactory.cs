using Microsoft.Data.Sqlite;

namespace MacExplorer.Services.Impl;

/// <summary>
/// Factory for SQLite connections to the application database.
/// Ensures consistent PRAGMA settings (WAL mode, busy timeout) across all connections.
/// Each caller receives its own connection and is responsible for disposing it.
/// </summary>
public class DatabaseConnectionFactory
{
    private readonly string _databasePath;

    public DatabaseConnectionFactory(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>
    /// Creates a new connection with consistent PRAGMA settings.
    /// The caller owns the connection and must dispose it.
    /// </summary>
    public SqliteConnection GetConnection()
    {
        var conn = new SqliteConnection($"Data Source={_databasePath};Mode=ReadWriteCreate");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }
}
