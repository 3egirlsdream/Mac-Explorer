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
    private readonly object _pragmaLock = new();
    private bool _walInitialized;

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
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();

        EnsureWalInitialized(conn);
        return conn;
    }

    private void EnsureWalInitialized(SqliteConnection connection)
    {
        if (_walInitialized) return;

        lock (_pragmaLock)
        {
            if (_walInitialized) return;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
            _walInitialized = true;
        }
    }
}
