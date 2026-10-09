namespace MacExplorer.Services.Impl;

internal interface ICredentialStore
{
    string? Read(string account);
    void Save(string account, string secret);
    void Delete(string account);
}

/// <summary>Local SQLite credentials, scoped by service and account. No OS credential prompts.</summary>
internal sealed class DatabaseCredentialStore : ICredentialStore
{
    private readonly string _service;
    private readonly DatabaseConnectionFactory _database;

    public DatabaseCredentialStore(string service, string? databasePath = null)
    {
        _service = service;
        _database = new DatabaseConnectionFactory(databasePath ?? RuntimePaths.DatabasePath);
        using var connection = _database.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS app_credentials (
                service TEXT NOT NULL, account TEXT NOT NULL, secret TEXT NOT NULL,
                PRIMARY KEY (service, account));
            """;
        command.ExecuteNonQuery();
    }

    public string? Read(string account)
    {
        using var connection = _database.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret FROM app_credentials WHERE service = $service AND account = $account";
        command.Parameters.AddWithValue("$service", _service);
        command.Parameters.AddWithValue("$account", account);
        return command.ExecuteScalar() as string;
    }

    public void Save(string account, string secret)
    {
        using var connection = _database.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_credentials(service, account, secret) VALUES ($service, $account, $secret)
            ON CONFLICT(service, account) DO UPDATE SET secret = excluded.secret;
            """;
        command.Parameters.AddWithValue("$service", _service);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$secret", secret);
        command.ExecuteNonQuery();
    }

    public void Delete(string account)
    {
        using var connection = _database.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_credentials WHERE service = $service AND account = $account";
        command.Parameters.AddWithValue("$service", _service);
        command.Parameters.AddWithValue("$account", account);
        command.ExecuteNonQuery();
    }
}
