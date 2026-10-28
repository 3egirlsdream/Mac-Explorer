using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Network")]
[Trait("Area", "Remote")]
public sealed class WebDavNetworkTests
{
    [Theory]
    [InlineData("dav.example.test/目录 Files/", "https", 443, "/%E7%9B%AE%E5%BD%95%20Files/")]
    [InlineData("http://localhost:8080/dav/a%20b/", "http", 8080, "/dav/a%20b/")]
    [InlineData("https://[::1]:8443/", "https", 8443, "/")]
    public void ParsesFullUrlWithoutDoubleEncoding(string input, string scheme, int port, string path)
    {
        var address = WebDavAddress.Parse(input).Uri;
        Assert.Equal(scheme, address.Scheme); Assert.Equal(port, address.Port); Assert.Equal(path, address.AbsolutePath);
    }

    [Theory]
    [InlineData("ftp://host/dav/")]
    [InlineData("https://user:secret@host/dav/")]
    [InlineData("https://host/dav/?token=secret")]
    [InlineData("https://host/dav/#fragment")]
    [InlineData("https://host/dav/../other")]
    [InlineData("https://host/dav/%2e%2e/other")]
    [InlineData("https://host/dav/%0a")]
    [InlineData("https://host:99999/dav/")]
    public void RejectsCredentialsAndAmbiguousUrls(string input) => Assert.Throws<ArgumentException>(() => WebDavAddress.Parse(input));

    [Fact]
    public void MatchingRespectsProtocolPortPathAndAccount()
    {
        var server = Dav("https://DAV.example.test:443/Files/", "alice");
        Assert.True(NetworkMountAddress.MatchesEndpoint(server, "https://alice@dav.example.test/Files"));
        Assert.True(NetworkMountAddress.MatchesAccount(server, "https://alice@dav.example.test/Files"));
        Assert.False(NetworkMountAddress.MatchesEndpoint(server, "http://dav.example.test/Files/"));
        Assert.False(NetworkMountAddress.MatchesEndpoint(server, "https://dav.example.test:8443/Files/"));
        Assert.False(NetworkMountAddress.MatchesEndpoint(server, "https://dav.example.test/files/"));
        Assert.False(NetworkMountAddress.MatchesEndpoint(server, "https://dav.example.test/Files/sub/"));
        Assert.False(NetworkMountAddress.MatchesAccount(server, "https://bob@dav.example.test/Files/"));
        Assert.False(NetworkMountAddress.MatchesAccount(server, "https://dav.example.test/Files/"));
    }

    [Fact]
    public void SaveReloadPreservesWebDavIdentityAndKeepsPasswordOutOfJson()
    {
        using var fixture = new Fixture();
        var server = Dav("https://dav.example.test:8443/Files/"); server.Password = "fixture-dav-secret";
        fixture.Remote.SaveServer(server);
        var json = File.ReadAllText(fixture.Config);
        Assert.DoesNotContain(server.Password, json);
        using var reloaded = new RemoteConnectionService(fixture.Config, fixture.Credentials);
        var saved = Assert.Single(reloaded.GetSavedServers());
        Assert.Equal(server.Id, saved.Id); Assert.Equal(RemoteServiceType.WebDav, saved.ServiceType);
        Assert.Equal(server.WebDavUrl, saved.WebDavUrl); Assert.Equal(server.Password, saved.Password);
        Assert.Equal(0, (int)RemoteServiceType.Sftp); Assert.Equal(1, (int)RemoteServiceType.Smb);
    }

    [Fact]
    public void PersistenceRejectsWebDavCredentialsEmbeddedInAddress()
    {
        using var fixture = new Fixture();
        var server = Dav("https://alice:secret@dav.example.test/Files/");
        Assert.Throws<ArgumentException>(() => fixture.Remote.SaveServer(server));
        Assert.Empty(fixture.Remote.GetSavedServers()); Assert.False(File.Exists(fixture.Config));
    }

    [Fact]
    public async Task ReusesOnlyUniqueMatchingMountAndPassesCredentialsSeparately()
    {
        var volume = new VolumeInfo { Path = "/Volumes/dav", IsNetwork = true, SourceUrl = "https://alice@dav.example.test/Files/" };
        IReadOnlyList<VolumeInfo> volumes = [volume];
        var calls = 0;
        var service = new NetworkMountService(() => volumes, (uri, user, secret, _) =>
        {
            calls++; Assert.Equal("https://dav.example.test/Files/", uri);
            Assert.Equal("alice", user); Assert.Equal("fixture-secret", secret);
            return Task.FromResult(new[] { "/Volumes/dav-1" });
        }, _ => Task.FromResult(true));
        var server = Dav("https://dav.example.test/Files/", "alice"); server.Password = "fixture-secret";
        Assert.Equal(volume.Path, await service.MountAsync(server)); Assert.Equal(0, calls);
        volumes = [volume, new VolumeInfo { Path = "/Volumes/dav-copy", SourceUrl = volume.SourceUrl, IsNetwork = true }];
        Assert.Equal("/Volumes/dav-1", await service.MountAsync(server)); Assert.Equal(1, calls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.MountAsync(server, cancelled.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SavedProfilesMergeWithTheirMountWithoutCollapsingDifferentAccountsOrProtocols()
    {
        using var fixture = new Fixture();
        var first = Dav("https://dav.example.test/Files/", "alice");
        var second = Dav(first.WebDavUrl, "bob");
        var sftp = new RemoteServerInfo { Host = "dav.example.test", Username = "alice" };
        foreach (var server in new[] { first, second, sftp }) fixture.Remote.SaveServer(server);
        fixture.Volumes.Items = [new VolumeInfo { Path = "/Volumes/dav", SourceUrl = "https://alice@dav.example.test/Files/", IsNetwork = true }];
        var rows = fixture.Locations.GetLocations();
        Assert.Equal(3, rows.Count);
        var connected = Assert.Single(rows.Where(row => row.IsConnected));
        Assert.Equal(first.Id, connected.Identity); Assert.Equal("WebDAV", connected.Protocol);
        fixture.Volumes.Items = [];
        var disconnected = fixture.Locations.GetLocations().Single(row => row.Identity == first.Id);
        Assert.False(disconnected.IsConnected); Assert.Null(disconnected.Volume);
    }

    [Fact]
    public void UnknownExternalMountIsIndependentAndSharedChangesReachAllSubscribers()
    {
        using var fixture = new Fixture();
        var first = 0; var second = 0;
        fixture.Locations.Changed += () => first++;
        fixture.Locations.Changed += () => second++;
        fixture.Remote.SaveServer(Dav("https://dav.example.test/Files/", "alice"));
        fixture.Volumes.Items = [new VolumeInfo { Path = "/Volumes/external", SourceUrl = "https://dav.example.test/Files/", IsNetwork = true }];
        fixture.Volumes.Notify();
        var rows = fixture.Locations.GetLocations();
        Assert.Equal(2, rows.Count); Assert.Single(rows.Where(r => r.IsConnected));
        Assert.Equal(2, first); Assert.Equal(first, second);
        var id = fixture.Remote.GetSavedServers()[0].Id;
        fixture.Remote.RemoveServer(id);
        Assert.Single(fixture.Locations.GetLocations()); Assert.True(first > 2); Assert.Equal(first, second);
    }

    [Fact]
    public async Task ChangedWebDavRootCannotReuseOrDisconnectPreviousMountAndBusyMountRemainsConnected()
    {
        using var fixture = new Fixture();
        var server = Dav("https://dav.example.test/Files/", "alice");
        fixture.Volumes.Items = [new VolumeInfo { Path = "/Volumes/dav", SourceUrl = "https://alice@dav.example.test/Files/", IsNetwork = true }];
        await fixture.Coordinator.ConnectAsync(server);
        Assert.False(await fixture.Coordinator.DisconnectAsync(server));
        Assert.True(fixture.Coordinator.IsConnected(server));
        server.WebDavUrl = "https://dav.example.test/files/";
        Assert.False(fixture.Coordinator.IsConnected(server));
        Assert.True(await fixture.Coordinator.DisconnectAsync(server));
        Assert.Throws<IOException>(() => fixture.Coordinator.AdoptMount(server, "/Volumes/dav"));
    }

    [Fact]
    public async Task RemountedPathWithUnknownAccountDoesNotInheritPreviousConnection()
    {
        using var fixture = new Fixture();
        var server = Dav("https://dav.example.test/Files/", "alice");
        fixture.Volumes.Items = [new VolumeInfo { Path = "/Volumes/dav", SourceUrl = server.WebDavUrl, MountId = "1:2", IsNetwork = true }];
        await fixture.Coordinator.ConnectAsync(server);
        Assert.True(fixture.Coordinator.IsConnected(server));
        fixture.Volumes.Items = [new VolumeInfo { Path = "/Volumes/dav", SourceUrl = server.WebDavUrl, MountId = "1:3", IsNetwork = true }];
        Assert.False(fixture.Coordinator.IsConnected(server));
    }

    [Fact]
    public void LegacyProfilesKeepIdsProtocolsAndStoredCredentials()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Config, """
            [{"Id":"legacy-sftp","Host":"ssh.example.test","Port":22},
             {"Id":"legacy-smb","Host":"nas.example.test","Port":445,"ServiceType":1,"ShareName":"Files"}]
            """);
        fixture.Credentials.Save("legacy-sftp", "sftp-fixture-secret");
        fixture.Credentials.Save("legacy-smb", "smb-fixture-secret");
        using var reloaded = new RemoteConnectionService(fixture.Config, fixture.Credentials);
        var servers = reloaded.GetSavedServers();
        Assert.Equal(RemoteServiceType.Sftp, servers.Single(s => s.Id == "legacy-sftp").ServiceType);
        Assert.Equal(RemoteServiceType.Smb, servers.Single(s => s.Id == "legacy-smb").ServiceType);
        Assert.Equal("sftp-fixture-secret", servers.Single(s => s.Id == "legacy-sftp").Password);
        Assert.Equal("smb-fixture-secret", servers.Single(s => s.Id == "legacy-smb").Password);
        Assert.All(servers, s => Assert.Equal("", s.WebDavUrl));
    }

    private static RemoteServerInfo Dav(string url, string user = "") => new()
    { ServiceType = RemoteServiceType.WebDav, Host = new Uri(url).Host, Port = new Uri(url).Port, WebDavUrl = url, Username = user };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "webdav-unit-" + Guid.NewGuid().ToString("N"));
        public string Config => Path.Combine(_root, "servers.json");
        public PrivacySecurityFollowupTests.MemoryCredentials Credentials { get; } = new();
        public RemoteConnectionService Remote { get; }
        public Volumes Volumes { get; } = new();
        public ServerConnectionCoordinator Coordinator { get; }
        public NetworkLocationService Locations { get; }
        private readonly NetworkDiscoveryService _discovery = new(_ => null, _ => Task.CompletedTask);
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Remote = new(Config, Credentials);
            var mounts = new NetworkMountService(() => Volumes.Items, (_, _, _, _) => Task.FromResult(new[] { "/Volumes/dav" }), _ => Task.FromResult(false));
            Coordinator = new(Remote, mounts, Volumes);
            Locations = new(Remote, Coordinator, Volumes, _discovery);
        }
        public void Dispose()
        { Locations.Dispose(); Coordinator.Dispose(); Remote.Dispose(); _discovery.Dispose(); Directory.Delete(_root, true); }
    }

    private sealed class Volumes : IVolumeMonitorService
    {
        public IReadOnlyList<VolumeInfo> Items { get; set; } = [];
        public IReadOnlyList<VolumeInfo> ExternalVolumes => Items;
        public event Action? VolumesChanged;
        public void Notify() => VolumesChanged?.Invoke();
        public bool IsExternalVolume(string path) => false;
        public Task<bool> EjectVolumeAsync(string path) => Task.FromResult(false);
        public void Dispose() { }
    }
}
