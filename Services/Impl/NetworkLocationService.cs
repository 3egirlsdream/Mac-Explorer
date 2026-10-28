using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public sealed class NetworkLocationService : INetworkLocationService, IDisposable
{
    private readonly IRemoteConnectionService _connections;
    private readonly IServerConnectionCoordinator _coordinator;
    private readonly IVolumeMonitorService _volumes;
    private readonly INetworkDiscoveryService _discovery;
    public event Action? Changed;

    public NetworkLocationService(IRemoteConnectionService connections, IServerConnectionCoordinator coordinator,
        IVolumeMonitorService volumes, INetworkDiscoveryService discovery)
    {
        _connections = connections; _coordinator = coordinator; _volumes = volumes; _discovery = discovery;
        _coordinator.Changed += OnChanged;
        _discovery.Changed += OnChanged;
    }

    public IReadOnlyList<NetworkLocationRow> GetLocations(bool includeDiscovered = false) => Build(
        _connections.GetSavedServers().Concat(_connections.GetConnectedServers()).DistinctBy(s => s.Id),
        _volumes.ExternalVolumes, includeDiscovered ? _discovery.Servers : [], _coordinator);

    internal static IReadOnlyList<NetworkLocationRow> Build(IEnumerable<RemoteServerInfo> servers,
        IEnumerable<VolumeInfo> volumes, IEnumerable<NetworkServer> discovered, IServerConnectionCoordinator? coordinator)
    {
        var rows = new List<NetworkLocationRow>();
        var networkVolumes = volumes.Where(v => v.IsNetwork).ToArray();
        var representedVolumes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            var volume = coordinator?.GetMountedVolume(server);
            if (volume != null) representedVolumes.Add(volume.Path);
            rows.Add(new(server.DisplayName, server.Host, server, volume,
                server.ServiceType == RemoteServiceType.Sftp && (coordinator?.IsConnected(server) ?? false)));
        }
        foreach (var volume in networkVolumes.Where(v => !representedVolumes.Contains(v.Path)))
            rows.Add(new(volume.DisplayName, LocalizationText.Get("network.mounted"), Volume: volume));
        foreach (var device in discovered)
        {
            // Discovery identifies an SMB host, not a share or an account. Keep unrelated protocols visible.
            if (rows.Any(row => row.Server?.ServiceType == RemoteServiceType.Smb && row.Server.Port == device.Port
                && (string.Equals(row.Server.Host.TrimEnd('.'), device.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
                    || device.Addresses.Contains(row.Server.Host, StringComparer.OrdinalIgnoreCase)))) continue;
            if (networkVolumes.Any(v => Uri.TryCreate(v.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme == "smb"
                && (uri.Port > 0 ? uri.Port : 445) == device.Port
                && (string.Equals(uri.Host.TrimEnd('.'), device.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
                    || device.Addresses.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)))) continue;
            rows.Add(new(device.Name, device.Host, new RemoteServerInfo
                { Id = "discovered:" + device.Id, Name = device.Name, Host = device.Host, Port = device.Port, ServiceType = RemoteServiceType.Smb }, Discovered: true));
        }
        return rows;
    }

    private void OnChanged() => Changed?.Invoke();
    public void Dispose()
    {
        _coordinator.Changed -= OnChanged;
        _discovery.Changed -= OnChanged;
    }
}
