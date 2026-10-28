using MacExplorer.Models;

namespace MacExplorer.Services;

public interface INetworkMountService
{
    Task<string> MountAsync(RemoteServerInfo server, CancellationToken cancellationToken = default);
    Task<bool> UnmountAsync(string mountPath);
    IReadOnlyList<VolumeInfo> GetMountedVolumes();
}

public interface IServerConnectionCoordinator
{
    Task<string> ConnectAsync(RemoteServerInfo server, CancellationToken cancellationToken = default);
    Task<bool> DisconnectAsync(RemoteServerInfo server);
    bool IsConnected(RemoteServerInfo server);
    VolumeInfo? GetMountedVolume(RemoteServerInfo server);
    void AdoptMount(RemoteServerInfo server, string path);
    event Action? Changed;
}
