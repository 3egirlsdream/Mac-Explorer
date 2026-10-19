using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public sealed class ServerConnectionCoordinator : IServerConnectionCoordinator, IDisposable
{
    private readonly IRemoteConnectionService _sftp;
    private readonly ISmbMountService _smb;
    private readonly IVolumeMonitorService _volumes;
    private readonly Dictionary<string, (string Path, string Key, string? SourceUrl)> _mounted = new();
    private readonly SemaphoreSlim _gate = new(1);
    public event Action? Changed;

    public ServerConnectionCoordinator(IRemoteConnectionService sftp, ISmbMountService smb, IVolumeMonitorService volumes)
    {
        _sftp = sftp; _smb = smb; _volumes = volumes;
        _volumes.VolumesChanged += OnVolumesChanged;
    }

    public async Task<string> ConnectAsync(RemoteServerInfo server, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (server.ServiceType == RemoteServiceType.Sftp)
            {
                await _sftp.GetOrConnectAsync(server, cancellationToken);
                return VirtualPath.BuildRemotePath(server.Id, server.DefaultPath);
            }
            var key = MountKey(server);
            (string Path, string Key, string? SourceUrl) mounted;
            lock (_mounted) _mounted.TryGetValue(server.Id, out mounted);
            if (mounted.Key == key && _smb.GetMountedVolumes().Any(v => MatchesCachedMount(mounted, v))) return mounted.Path;
            var path = await _smb.MountAsync(server, cancellationToken);
            var source = _smb.GetMountedVolumes().FirstOrDefault(v => DirectoryAccess.IsWithin(path, v.Path))?.SourceUrl;
            lock (_mounted) _mounted[server.Id] = (path, key, source);
            server.IsConnected = true;
            Changed?.Invoke();
            return path;
        }
        finally { _gate.Release(); }
    }

    public bool IsConnected(RemoteServerInfo server) => server.ServiceType == RemoteServiceType.Sftp
        ? _sftp.IsConnected(server.Id) : GetMountedVolume(server) != null;

    public VolumeInfo? GetMountedVolume(RemoteServerInfo server)
    {
        if (server.ServiceType != RemoteServiceType.Smb) return null;
        var volumes = _smb.GetMountedVolumes();
        (string Path, string Key, string? SourceUrl) cached;
        lock (_mounted) cached = _mounted.GetValueOrDefault(server.Id);
        var address = SmbAddress.FromServer(server);
        return volumes.FirstOrDefault(v => cached.Key == MountKey(server) && MatchesCachedMount(cached, v))
            ?? volumes.FirstOrDefault(v => address.Matches(v.SourceUrl, requireShare: !string.IsNullOrEmpty(address.Share)));
    }

    private static bool MatchesCachedMount((string Path, string Key, string? SourceUrl) cached, VolumeInfo volume)
        => cached.Path != null && !string.IsNullOrEmpty(cached.SourceUrl)
            && DirectoryAccess.IsWithin(cached.Path, volume.Path)
            && string.Equals(cached.SourceUrl, volume.SourceUrl, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> DisconnectAsync(RemoteServerInfo server)
    {
        if (server.ServiceType == RemoteServiceType.Sftp) { _sftp.Disconnect(server.Id); Changed?.Invoke(); return true; }
        var volume = GetMountedVolume(server);
        if (volume != null && !await _smb.UnmountAsync(volume.Path)) return false;
        lock (_mounted) _mounted.Remove(server.Id);
        server.IsConnected = false; Changed?.Invoke(); return true;
    }

    private static string MountKey(RemoteServerInfo server) => $"{server.Host}:{server.Port}/{server.ShareName}|{server.Domain}|{server.Username}|{server.DefaultPath}";
    public void AdoptMount(RemoteServerInfo server, string path)
    {
        var volume = _smb.GetMountedVolumes().FirstOrDefault(v => DirectoryAccess.IsWithin(path, v.Path))
            ?? throw new IOException(LocalizationText.Get("network.no-mount"));
        lock (_mounted) _mounted[server.Id] = (path, MountKey(server), volume.SourceUrl);
        server.IsConnected = true; Changed?.Invoke();
    }
    private void OnVolumesChanged() => Changed?.Invoke();
    public void Dispose() => _volumes.VolumesChanged -= OnVolumesChanged;
}
