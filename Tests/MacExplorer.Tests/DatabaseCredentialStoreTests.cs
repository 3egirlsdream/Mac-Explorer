using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class DatabaseCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "credentials-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "app.db");

    [Fact]
    public void CredentialsSurviveReopeningAndRemainScopedByServiceAndAccount()
    {
        var first = new DatabaseCredentialStore("copilot", DatabasePath);
        var other = new DatabaseCredentialStore("sftp", DatabasePath);
        Assert.Null(first.Read("api-key"));
        first.Save("api-key", "initial-fixture-key");
        other.Save("api-key", "other-fixture-key");
        first.Save("api-key", "更新 ' $secret 🔑");
        var reopened = new DatabaseCredentialStore("copilot", DatabasePath);
        Assert.Equal("更新 ' $secret 🔑", reopened.Read("api-key"));
        Assert.Equal("other-fixture-key", other.Read("api-key"));
        Assert.Null(reopened.Read("another-account"));
        reopened.Delete("api-key");
        reopened.Delete("api-key");
        Assert.Null(first.Read("api-key"));
        Assert.Equal("other-fixture-key", other.Read("api-key"));
    }

    [Fact]
    public void RemoteLegacyPasswordMigratesToDatabaseAndDeleteRemovesCredentials()
    {
        var credentials = new DatabaseCredentialStore("sftp", DatabasePath);
        var config = Path.Combine(_root, "servers.json");
        File.WriteAllText(config, """
            [{"Id":"fixture","Host":"fixture.test","Username":"tester","Password":"legacy-fixture-password"}]
            """);
        using (var service = new RemoteConnectionService(config, credentials))
        {
            Assert.Null(service.CredentialLoadError);
            Assert.Equal("legacy-fixture-password", Assert.Single(service.GetSavedServers()).Password);
            Assert.DoesNotContain("legacy-fixture-password", File.ReadAllText(config));
        }
        using var reopened = new RemoteConnectionService(config, new DatabaseCredentialStore("sftp", DatabasePath));
        var server = Assert.Single(reopened.GetSavedServers());
        Assert.Equal("legacy-fixture-password", server.Password);
        server.AuthMethod = RemoteAuthMethod.PrivateKey;
        server.RememberPrivateKeyPassphrase = true;
        server.PrivateKeyPassphrase = "passphrase-fixture";
        reopened.SaveServer(server);
        Assert.Null(credentials.Read(server.Id));
        Assert.Equal("passphrase-fixture", credentials.Read(server.Id + ":private-key-passphrase"));
        Assert.DoesNotContain("passphrase-fixture", File.ReadAllText(config));
        reopened.RemoveServer(server.Id);
        Assert.Null(credentials.Read(server.Id + ":private-key-passphrase"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
