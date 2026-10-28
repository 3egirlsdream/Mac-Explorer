using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Network")]
public sealed class NetworkLifecycleTests
{
    [Fact]
    public void DiscoveryIsSharedAndReleasesNativeAndNetworkWorkAfterLastPageCloses()
    {
        var tokens = new List<CancellationToken>(); var closed = 0; var started = 0;
        using var discovery = new NetworkDiscoveryService(_ => { started++; return new CallbackLease(() => closed++); },
            token => { tokens.Add(token); return Task.CompletedTask; });
        var first = discovery.Acquire(); var second = discovery.Acquire();
        Assert.Equal(1, started);
        first.Dispose(); first.Dispose();
        Assert.False(tokens[0].IsCancellationRequested); Assert.Equal(0, closed);
        discovery.Refresh();
        Assert.True(tokens[0].IsCancellationRequested); Assert.Equal(2, started); Assert.Equal(1, closed);
        second.Dispose();
        Assert.True(tokens[1].IsCancellationRequested); Assert.Equal(2, closed);
    }

    [Fact]
    public void PermissionErrorsCanRecoverAndCallbacksFromOldSessionsAreIgnored()
    {
        var callbacks = new List<Action<string>>();
        using var discovery = new NetworkDiscoveryService(callback => { callbacks.Add(callback); return new CallbackLease(() => { }); }, _ => Task.CompletedTask);
        using var lease = discovery.Acquire();
        callbacks[0]("{\"Error\":-65570}");
        Assert.Equal(LocalizationText.Get("network.discovery-permission"), discovery.Error);
        discovery.Refresh();
        callbacks[0]("invalid json from cancelled session");
        Assert.Null(discovery.Error);
        callbacks[1]("{\"Error\":-65570}");
        callbacks[1]("{\"Ready\":true}");
        Assert.Null(discovery.Error);
    }

    [Fact]
    public async Task SameNameSharesUseTheActualSystemMountPath()
    {
        var existing = new VolumeInfo { Path = "/Volumes/Files", SourceUrl = "smb://fixture@first/Files", IsNetwork = true };
        var mount = new NetworkMountService(() => [existing], (_, _, _, _) => Task.FromResult(new[] { "/Volumes/Files-1" }), _ => Task.FromResult(true));
        var server = new RemoteServerInfo { ServiceType = RemoteServiceType.Smb, Host = "second", Port = 445, ShareName = "Files", Username = "fixture", DefaultPath = "/Sub" };
        Assert.Equal("/Volumes/Files-1/Sub", await mount.MountAsync(server));
    }

    [Fact]
    public async Task ExistingMountIsReusedForTheSameAccountAndCancellationIsHonored()
    {
        var calls = 0;
        var volume = new VolumeInfo { Path = "/Volumes/Files", SourceUrl = "smb://fixture@nas/Files", IsNetwork = true };
        var mount = new NetworkMountService(() => [volume], (_, _, _, _) => { calls++; return Task.FromResult(new[] { volume.Path }); }, _ => Task.FromResult(true));
        var server = new RemoteServerInfo { ServiceType = RemoteServiceType.Smb, Host = "nas", ShareName = "Files", Port = 445, Username = "fixture", DefaultPath = "/Sub" };
        Assert.Equal("/Volumes/Files/Sub", await mount.MountAsync(server)); Assert.Equal(0, calls);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mount.MountAsync(server, cts.Token));
        server.Username = "another-account";
        await mount.MountAsync(server); Assert.Equal(1, calls);
        server.Username = "fixture"; server.Domain = "OTHER";
        await mount.MountAsync(server); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReplacedMountAtTheSamePathIsNotDisconnectedAsTheOldServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "smb-replaced-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var remote = new RemoteConnectionService(Path.Combine(root, "servers.json"), new PrivacySecurityFollowupTests.MemoryCredentials());
            var volume = new VolumeInfo { Path = "/Volumes/Files", SourceUrl = "smb://fixture@first/Files", IsNetwork = true };
            var unmounts = 0;
            var mount = new NetworkMountService(() => [volume], (_, _, _, _) => Task.FromResult(new[] { volume.Path }), _ => { unmounts++; return Task.FromResult(true); });
            using var coordinator = new ServerConnectionCoordinator(remote, mount, new Volumes());
            var server = new RemoteServerInfo { ServiceType = RemoteServiceType.Smb, Host = "first", Port = 445, ShareName = "Files", Username = "fixture" };
            await coordinator.ConnectAsync(server);
            volume = new VolumeInfo { Path = volume.Path, SourceUrl = "smb://fixture@second/Files", IsNetwork = true };
            Assert.False(coordinator.IsConnected(server));
            Assert.True(await coordinator.DisconnectAsync(server));
            Assert.Equal(0, unmounts);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BusyUnmountDoesNotLoseConnectionAndChangedAccountRequiresANewMount()
    {
        var root = Path.Combine(Path.GetTempPath(), "smb-coordinator-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var remote = new RemoteConnectionService(Path.Combine(root, "servers.json"), new PrivacySecurityFollowupTests.MemoryCredentials());
            var volume = new VolumeInfo { Path = "/Volumes/Files", IsNetwork = true, SourceUrl = "smb://nas/Files" };
            var calls = 0; var canUnmount = false;
            var mount = new NetworkMountService(() => [volume], (_, _, _, _) => { calls++; return Task.FromResult(new[] { volume.Path }); }, _ => Task.FromResult(canUnmount));
            using var coordinator = new ServerConnectionCoordinator(remote, mount, new Volumes());
            var server = new RemoteServerInfo { ServiceType = RemoteServiceType.Smb, Host = "nas", Port = 445, ShareName = "Files", Username = "first" };
            await coordinator.ConnectAsync(server); await coordinator.ConnectAsync(server); Assert.Equal(1, calls);
            server.Username = "second"; await coordinator.ConnectAsync(server); Assert.Equal(2, calls);
            Assert.False(await coordinator.DisconnectAsync(server)); Assert.True(coordinator.IsConnected(server));
            canUnmount = true; Assert.True(await coordinator.DisconnectAsync(server));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task LateDnsResolutionCannotResurrectARemovedServer()
    {
        var completion = new TaskCompletionSource<System.Net.IPAddress[]>();
        using var discovery = new NetworkDiscoveryService(_ => null, _ => Task.CompletedTask, (_, _) => completion.Task);
        const string found = "{\"Id\":\"fixture\",\"Name\":\"Fixture\",\"Host\":\"fixture.local\",\"Port\":445}";
        var resolving = discovery.ProcessBonjourAsync(found, CancellationToken.None);
        Assert.Single(discovery.Servers);
        await discovery.ProcessBonjourAsync("{\"Id\":\"fixture\",\"Removed\":true}", CancellationToken.None);
        completion.SetResult([System.Net.IPAddress.Loopback]);
        await resolving;
        Assert.Empty(discovery.Servers);
    }

    private sealed class CallbackLease(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class Volumes : IVolumeMonitorService
    {
        public IReadOnlyList<VolumeInfo> ExternalVolumes => [];
        public bool IsExternalVolume(string path) => false;
        public Task<bool> EjectVolumeAsync(string path) => Task.FromResult(false);
        public event Action? VolumesChanged { add { } remove { } }
        public void Dispose() { }
    }
}
