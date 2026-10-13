using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Remote")]
public sealed class RemoteServiceTypeTests
{
    [Fact]
    public void OldConfigurationDefaultsToSftpAndSaveReloadPreservesIdentity()
    {
        var root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "remote-type-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "servers.json");
            File.WriteAllText(path, "[{\"Id\":\"legacy\",\"Host\":\"legacy.test\",\"Port\":2222,\"Username\":\"fixture\"}]");
            var credentials = new PrivacySecurityFollowupTests.MemoryCredentials();
            using (var service = new RemoteConnectionService(path, credentials))
            {
                var legacy = Assert.Single(service.GetSavedServers());
                Assert.Equal(RemoteServiceType.Sftp, legacy.ServiceType);
                legacy.Password = "fixture-secret";
                service.SaveServer(legacy);
                Assert.Equal(RemoteServiceType.Sftp, Assert.Single(service.GetSavedServers()).ServiceType);
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                Assert.True(json.RootElement[0].TryGetProperty("ServiceType", out _));
                Assert.DoesNotContain("fixture-secret", File.ReadAllText(path));
            }
            using var reloaded = new RemoteConnectionService(path, credentials);
            var server = Assert.Single(reloaded.GetSavedServers());
            Assert.Equal("legacy", server.Id);
            Assert.Equal(2222, server.Port);
            Assert.Equal(RemoteServiceType.Sftp, server.ServiceType);
            Assert.Equal("fixture-secret", server.Password);
        }
        finally { Directory.Delete(root, true); }
    }
}
