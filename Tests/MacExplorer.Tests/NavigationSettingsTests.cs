using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

public sealed class NavigationSettingsTests
{
    [Fact]
    public async Task DeferredNavigationWritesKeepCacheResponsiveAndFlushLatestDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-settings-" + Guid.NewGuid().ToString("N"));
        var factory = new DatabaseConnectionFactory(Path.Combine(root, "settings.db"));
        try
        {
            using var writer = factory.GetConnection();
            using (var schema = writer.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE app_settings(key TEXT PRIMARY KEY, value TEXT)";
                schema.ExecuteNonQuery();
            }
            var settings = new SettingsService(factory);
            try
            {
                using (var transaction = writer.BeginTransaction())
                {
                    using var hold = writer.CreateCommand();
                    hold.Transaction = transaction;
                    hold.CommandText = "INSERT INTO app_settings VALUES ('writer', 'held')";
                    hold.ExecuteNonQuery();
                    var changed = new List<string>();
                    settings.SettingChanged += changed.Add;
                    settings.SetDeferred("navigation_last_directory", "/first");
                    // Let the worker reach SQLite while the other connection holds its write lock.
                    await Task.Delay(250);
                    var reads = Task.Run(() =>
                    {
                        settings.SetDeferred("navigation_last_directory", "/latest");
                        Assert.Equal("/latest", settings.Get("navigation_last_directory"));
                        Assert.Equal("/latest", settings.GetAll()["navigation_last_directory"]);
                    });
                    await reads.WaitAsync(TimeSpan.FromSeconds(1));
                    Assert.Equal(2, changed.Count);
                    transaction.Commit();
                }
                // Disposal waits for the final navigation state to be durable.
                settings.Dispose();
                using var read = writer.CreateCommand();
                read.CommandText = "SELECT value FROM app_settings WHERE key='navigation_last_directory'";
                Assert.Equal("/latest", read.ExecuteScalar());
            }
            finally { settings.Dispose(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
