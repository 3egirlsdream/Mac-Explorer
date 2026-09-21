using MacExplorer.Indexing;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class RuntimePathsTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(RuntimePaths.TestRootVariable);
    private readonly string? _previousDatabase = Environment.GetEnvironmentVariable("MACEXPLORER_DB_PATH");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fkfinder-profile-" + Guid.NewGuid().ToString("N"));

    public RuntimePathsTests() => Environment.SetEnvironmentVariable(RuntimePaths.TestRootVariable, _root);

    [Fact]
    public void TestProfileOverridesInheritedDatabaseAndKeepsSettingsSeparate()
    {
        var ordinaryDatabase = Path.Combine(_root, "ordinary", "index.db");
        using (var index = new SqliteFileIndex(ordinaryDatabase)) { }
        using (var settings = new SettingsService(new DatabaseConnectionFactory(ordinaryDatabase)))
            settings.Set("LastDirectory", "/Users/example/Documents");
        Environment.SetEnvironmentVariable("MACEXPLORER_DB_PATH", ordinaryDatabase);

        var config = new IndexConfiguration();
        Assert.Equal(Path.Combine(_root, ".macexplorer", "index.db"), config.DatabasePath);
        using (var index = new SqliteFileIndex(config.DatabasePath)) { }
        using (var settings = new SettingsService(new DatabaseConnectionFactory(config.DatabasePath)))
            Assert.Null(settings.Get("LastDirectory"));
        using (var settings = new SettingsService(new DatabaseConnectionFactory(ordinaryDatabase)))
            Assert.Equal("/Users/example/Documents", settings.Get("LastDirectory"));
    }

    [Fact]
    public async Task DefaultFoldersAndEnumerationUseTheFixtureTree()
    {
        RuntimePaths.PrepareTestRoot();
        var files = new MacFileService();
        Assert.Equal(_root, files.HomeDirectory);
        Assert.Equal(_root, files.RootDirectory);
        Assert.Equal(new[] { _root }, RuntimePaths.StartupIndexRoots);
        foreach (var name in new[] { "Desktop", "Documents", "Downloads" })
        {
            var directory = Path.Combine(files.HomeDirectory, name);
            await File.WriteAllTextAsync(Path.Combine(directory, "fixture.txt"), "test");
            var entry = Assert.Single(await files.GetDirectoryContentsAsync(directory));
            Assert.Equal(Path.Combine(directory, "fixture.txt"), entry.FullPath);
        }
    }

    [Fact]
    public void BroadSearchUsesTestRootAndSiblingPrefixDoesNotEscapeIt()
    {
        Assert.Equal(_root, RuntimePaths.ResolveSearchRoot("/"));
        Assert.Equal(_root, RuntimePaths.ResolveSearchRoot(_root));
        var child = Path.Combine(_root, "Documents");
        Assert.Equal(child, RuntimePaths.ResolveSearchRoot(child));
        Assert.Throws<ArgumentException>(() => RuntimePaths.ResolveSearchRoot(_root + "-sibling"));
        Assert.Throws<ArgumentException>(() => RuntimePaths.ResolveSearchRoot(Path.Combine(_root, "..", "outside")));
    }

    [Fact]
    public void OrdinaryLaunchKeepsExistingDefaultsAndDatabaseOverride()
    {
        Environment.SetEnvironmentVariable(RuntimePaths.TestRootVariable, null);
        Environment.SetEnvironmentVariable("MACEXPLORER_DB_PATH", null);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, RuntimePaths.HomeDirectory);
        Assert.Equal(Path.Combine(home, "Documents", "MacExplorer", "index.db"), new IndexConfiguration().DatabasePath);
        Assert.Equal(new[] { "/Applications", "/System/Applications", home }, RuntimePaths.StartupIndexRoots);
        Assert.Equal("/", RuntimePaths.ResolveSearchRoot("/"));
        Environment.SetEnvironmentVariable("MACEXPLORER_DB_PATH", "/tmp/explicit-index.db");
        Assert.Equal("/tmp/explicit-index.db", new IndexConfiguration().DatabasePath);
    }

    [Fact]
    public void RelativeTestRootFailsInsteadOfFallingBackToRealUserData()
    {
        Environment.SetEnvironmentVariable(RuntimePaths.TestRootVariable, "relative-fixture");
        Assert.Throws<ArgumentException>(() => new IndexConfiguration());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimePaths.TestRootVariable, _previousRoot);
        Environment.SetEnvironmentVariable("MACEXPLORER_DB_PATH", _previousDatabase);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
