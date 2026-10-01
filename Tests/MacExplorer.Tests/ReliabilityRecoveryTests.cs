using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReliabilityRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(RuntimePaths.TestRoot ?? "/private/tmp", "fk-recovery-" + Guid.NewGuid().ToString("N"));
    public ReliabilityRecoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DisablePersistenceFailureStillStopsNetworkAndRetrySurvivesReload()
    {
        var factory = new DatabaseConnectionFactory(Path.Combine(_root, "settings.db"));
        using var db = factory.GetConnection(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT)"; command.ExecuteNonQuery();
        using var settings = new SettingsService(factory);
        settings.Set(ILocalSendService.EnabledKey, true);
        await using var service = new LocalSendService(settings, new BackgroundTaskManager());
        service.ScanInterfacesOverride = () => []; service.DiscoveryAddressesOverride = () => [IPAddress.Loopback];
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            service.DiscoveryUdpPortOverride = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        await service.StartAsync(); var port = service.ListeningPort;
        Assert.True(port > 0); Assert.True(service.DiscoveryUdpPort > 0);
        command.CommandText = "CREATE TRIGGER block BEFORE INSERT ON app_settings WHEN NEW.key = 'localsend_enabled' BEGIN SELECT RAISE(FAIL, 'read-only fixture'); END";
        command.ExecuteNonQuery();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(false));
        Assert.Contains("重启", error.Message); Assert.False(service.Enabled);
        Assert.Equal(0, service.ListeningPort); Assert.Equal(0, service.DiscoveryUdpPort); Assert.Null(service.DiscoveryCoordinator);
        await service.StartAsync(); Assert.Equal(0, service.ListeningPort);
        using (var reloaded = new SettingsService(factory)) Assert.True(reloaded.Get(ILocalSendService.EnabledKey, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(true));
        Assert.False(service.Enabled); Assert.Equal(0, service.ListeningPort);
        command.CommandText = "DROP TRIGGER block"; command.ExecuteNonQuery();
        await service.SetEnabledAsync(false);
        using var afterRetry = new SettingsService(factory);
        Assert.False(afterRetry.Get(ILocalSendService.EnabledKey, true));
    }

    [Fact]
    public void RestoreSaveFailureEntersUnauthorizedThenRetriesAndClosesOldScopes()
    {
        var store = Path.Combine(_root, "grants.json"); var backend = new Bookmarks();
        File.WriteAllText(store, JsonSerializer.Serialize(new Dictionary<string,string> { [_root] = _root }));
        var original = File.ReadAllText(store); Directory.CreateDirectory(store + ".tmp");
        using var access = new DirectoryAccess(store, true, backend, []);
        Assert.Empty(access.AuthorizedRoots); Assert.NotNull(access.RestoreError);
        Assert.Equal(original, File.ReadAllText(store)); Assert.Equal(backend.Opened, backend.Closed);
        var changes = 0; access.GrantsChanged += () => changes++;
        Directory.Delete(store + ".tmp"); access.RetryUnavailable();
        Assert.Null(access.RestoreError); Assert.True(access.CanAccess(_root)); Assert.Equal(1, changes);
        access.RetryUnavailable(); Assert.Equal(1, backend.Opened - backend.Closed);
        Directory.CreateDirectory(store + ".tmp"); access.RetryUnavailable();
        Assert.Empty(access.AuthorizedRoots); Assert.Equal(backend.Opened, backend.Closed);
        Assert.Equal(3, changes); Assert.NotNull(access.RestoreError);
        Directory.Delete(store + ".tmp"); access.RetryUnavailable(); Assert.True(access.CanAccess(_root));
    }

    [Fact]
    public void UnreadableRecordAndMalformedBackupFailureNeverOverwriteOriginal()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = Path.Combine(_root, "grants.json"); var backend = new Bookmarks();
        File.WriteAllText(store, "invalid original");
        var directoryMode = File.GetUnixFileMode(_root);
        var fileMode = File.GetUnixFileMode(store);
        try
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            using (var access = new DirectoryAccess(store, true, backend, []))
            {
                Assert.Contains("备份失败", access.RestoreError); Assert.Empty(access.AuthorizedRoots);
                Assert.Throws<IOException>(() => access.RememberSelection(_root));
                Assert.Equal("invalid original", File.ReadAllText(store));
                File.SetUnixFileMode(_root, directoryMode);
                access.RememberSelection(_root); Assert.True(access.CanAccess(_root));
            }
            var original = File.ReadAllText(store);
            File.SetUnixFileMode(store, UnixFileMode.None);
            using (var access = new DirectoryAccess(store, true, backend, []))
            {
                Assert.Contains("读取失败", access.RestoreError); Assert.Empty(access.AuthorizedRoots);
                Assert.Throws<IOException>(() => access.RememberSelection(_root));
                File.SetUnixFileMode(store, fileMode);
                Assert.Equal(original, File.ReadAllText(store));
                access.RetryUnavailable(); Assert.True(access.CanAccess(_root)); Assert.Null(access.RestoreError);
            }
        }
        finally { File.SetUnixFileMode(_root, directoryMode); File.SetUnixFileMode(store, fileMode); }
        Assert.Equal(backend.Opened, backend.Closed);
    }

    [Fact]
    public void InvalidJsonIsPreservedUntilExplicitReauthorization()
    {
        var store = Path.Combine(_root, "invalid.json"); File.WriteAllText(store, "original invalid JSON");
        using var access = new DirectoryAccess(store, true, new Bookmarks(), []);
        Assert.Empty(access.AuthorizedRoots); Assert.NotNull(access.RestoreError);
        Assert.Equal("original invalid JSON", File.ReadAllText(store));
        Assert.Single(Directory.GetFiles(_root, "invalid.json.invalid-*"));
        access.RememberSelection(_root); Assert.True(access.CanAccess(_root));
        Assert.Equal("original invalid JSON", File.ReadAllText(Directory.GetFiles(_root, "invalid.json.invalid-*").Single()));
    }

    private sealed class Bookmarks : IBookmarkAccess
    {
        public int Opened, Closed;
        public string Create(string path, bool explicitScope) => path;
        public (IntPtr Scope, string? Path, string? Refreshed, bool Stale) Open(string bookmark)
        { Opened++; return (new IntPtr(Opened), bookmark, null, false); }
        public void Close(IntPtr scope) => Closed++;
        public string RealPath(string path) => Path.GetFullPath(path);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
