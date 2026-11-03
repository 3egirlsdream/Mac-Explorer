using System.Text.Json;
using MacExplorer.Services.Impl;

namespace MacExplorer.Services.Subscriptions;

public sealed record AppTrialRecord(DateTimeOffset StartedAt, DateTimeOffset ExpiresAt,
    bool SubscriberSeen = false, int Notices = 0, bool EligibilityConfirmed = false);

public interface IAppTrialStore
{
    AppTrialRecord LoadOrCreate(DateTimeOffset firstReadyAt);
    void Save(AppTrialRecord record);
}

/// <summary>Trial writes must be durable; unlike appearance preferences, failures are propagated.</summary>
public sealed class AppTrialStore(DatabaseConnectionFactory factory) : IAppTrialStore
{
    internal const string Key = "app_trial_v1";

    public AppTrialRecord LoadOrCreate(DateTimeOffset firstReadyAt)
    {
        using var connection = factory.GetConnection();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO app_settings (key,value) VALUES (@key,@value)";
        insert.Parameters.AddWithValue("@key", Key);
        insert.Parameters.AddWithValue("@value", JsonSerializer.Serialize(new AppTrialRecord(firstReadyAt, firstReadyAt.AddHours(168))));
        insert.ExecuteNonQuery();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT value FROM app_settings WHERE key=@key";
        read.Parameters.AddWithValue("@key", Key);
        var record = JsonSerializer.Deserialize<AppTrialRecord>((string)read.ExecuteScalar()!)
            ?? throw new InvalidDataException("Invalid application trial record.");
        if (record.ExpiresAt != record.StartedAt.AddHours(168))
            throw new InvalidDataException("Invalid application trial duration.");
        transaction.Commit();
        return record;
    }

    public void Save(AppTrialRecord record)
    {
        using var connection = factory.GetConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE app_settings SET value=@value WHERE key=@key";
        command.Parameters.AddWithValue("@key", Key);
        command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(record));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Application trial record is missing.");
        transaction.Commit();
    }
}
