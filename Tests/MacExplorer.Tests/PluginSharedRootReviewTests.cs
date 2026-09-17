using MacExplorer.Services.Plugins;
using Xunit;

namespace MacExplorer.Tests;

public sealed class PluginSharedRootReviewTests
{
    [Fact]
    public async Task UpgradingAnotherManagerDoesNotDeleteAnOlderReferencedVersion()
    {
        using var environment = new PluginTestEnvironment();
        await environment.Manager.InstallAsync(environment.CreateFixture());
        var oldDirectory = environment.Manager.Plugins.Single(p => p.Manifest.Id == "test.fixture").Directory;
        await using (var other = environment.NewManager())
            await other.InstallAsync(environment.CreateFixture("2.0.0"));
        Assert.True(Directory.Exists(oldDirectory));
        await environment.Manager.SetEnabledAsync("test.fixture", true);
        var oldPlugin = environment.Manager.Plugins.Single(p => p.Manifest.Id == "test.fixture");
        Assert.True(oldPlugin.Enabled, oldPlugin.LastError);
        Assert.Equal("1.0.0", oldPlugin.Manifest.Version);
    }
}
