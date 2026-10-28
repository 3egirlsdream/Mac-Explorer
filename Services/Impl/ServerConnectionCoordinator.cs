using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public sealed class ServerConnectionCoordinator : IServerConnectionCoordinator, IDisposable
{
    private readonly IRemoteConnectionService _sftp;
    private readonly INetworkMountService _mounts;
    private readonly IVolumeMonitorService _volumes;
    private readonly Dictionary<string, (string Path, string Key, string? SourceUrl, string? MountId)> _mounted = new();
    private readonly SemaphoreSlim _gate = new(1);
    public event Action? Changed;

    public ServerConnectionCoordinator(IRemoteConnectionService sftp, INetworkMountService mounts, IVolumeMonitorService volumes)
    {
        _sftp = sftp; _mounts = mounts; _volumes = volumes;
        _volumes.VolumesChanged += OnVolumesChanged;
        _sftp.Changed += OnVolumesChanged;
    }

    public async Task<string> ConnectAsync(RemoteServerInfo server, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (server.ServiceType == RemoteServiceType.Sftp)
            {
                await _sftp.GetOrConnectAsync(server, cancellationToken);
                RecordConnection(server);
                return VirtualPath.BuildRemotePath(server.Id, server.DefaultPath);
            }
            var key = MountKey(server);
            (string Path, string Key, string? SourceUrl, string? MountId) mounted;
            lock (_mounted) _mounted.TryGetValue(server.Id, out mounted);
            if (mounted.Key == key && _mounts.GetMountedVolumes().Any(v => MatchesCachedMount(mounted, v))) return mounted.Path;
            var path = await _mounts.MountAsync(server, cancellationToken);
            var volume = _mounts.GetMountedVolumes().FirstOrDefault(v => DirectoryAccess.IsWithin(path, v.Path));
            lock (_mounted) _mounted[server.Id] = (path, key, volume?.SourceUrl, volume?.MountId);
            server.IsConnected = true;
            RecordConnection(server);
            Changed?.Invoke();
            return path;
        }
        finally { _gate.Release(); }
    }

    public bool IsConnected(RemoteServerInfo server) => server.ServiceType == RemoteServiceType.Sftp
        ? _sftp.IsConnected(server.Id) : GetMountedVolume(server) != null;

    public VolumeInfo? GetMountedVolume(RemoteServerInfo server)
    {
        if (server.ServiceType == RemoteServiceType.Sftp) return null;
        var volumes = _mounts.GetMountedVolumes();
        (string Path, string Key, string? SourceUrl, string? MountId) cached;
        lock (_mounted) cached = _mounted.GetValueOrDefault(server.Id);
        var known = volumes.FirstOrDefault(v => cached.Key == MountKey(server) && MatchesCachedMount(cached, v));
        if (known != null) return known;
        var matches = volumes.Where(v => NetworkMountAddress.MatchesEndpoint(server, v.SourceUrl)
            && NetworkMountAddress.MatchesAccount(server, v.SourceUrl)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool MatchesCachedMount((string Path, string Key, string? SourceUrl, string? MountId) cached, VolumeInfo volume)
        => cached.Path != null && !string.IsNullOrEmpty(cached.SourceUrl)
            && DirectoryAccess.IsWithin(cached.Path, volume.Path)
            && string.Equals(cached.SourceUrl, volume.SourceUrl, StringComparison.Ordinal)
            && string.Equals(cached.MountId, volume.MountId, StringComparison.Ordinal);

    public async Task<bool> DisconnectAsync(RemoteServerInfo server)
    {
        if (server.ServiceType == RemoteServiceType.Sftp) { _sftp.Disconnect(server.Id); Changed?.Invoke(); return true; }
        var volume = GetMountedVolume(server);
        if (volume != null && !await _mounts.UnmountAsync(volume.Path)) return false;
        lock (_mounted) _mounted.Remove(server.Id);
        server.IsConnected = false; Changed?.Invoke(); return true;
    }

    private static string MountKey(RemoteServerInfo server) => $"{server.ServiceType}|{server.Host}:{server.Port}/{server.ShareName}|{server.WebDavUrl}|{server.Domain}|{server.Username}|{server.DefaultPath}";
    private void RecordConnection(RemoteServerInfo server)
    {
        server.LastConnectedAt = DateTimeOffset.UtcNow;
        if (_sftp.GetSavedServers().Any(s => s.Id == server.Id)) _sftp.SaveServer(server);
    }
    public void AdoptMount(RemoteServerInfo server, string path)
    {
        var volume = _mounts.GetMountedVolumes().FirstOrDefault(v => DirectoryAccess.IsWithin(path, v.Path) && NetworkMountAddress.MatchesEndpoint(server, v.SourceUrl)
                && (string.IsNullOrEmpty(new Uri(v.SourceUrl!).UserInfo) || NetworkMountAddress.MatchesAccount(server, v.SourceUrl)))
            ?? throw new IOException(LocalizationText.Get("network.no-mount"));
        lock (_mounted) _mounted[server.Id] = (path, MountKey(server), volume.SourceUrl, volume.MountId);
        server.IsConnected = true; Changed?.Invoke();
    }
    private void OnVolumesChanged() => Changed?.Invoke();
    public void Dispose()
    {
        _volumes.VolumesChanged -= OnVolumesChanged;
        _sftp.Changed -= OnVolumesChanged;
        _gate.Dispose();
    }
}
