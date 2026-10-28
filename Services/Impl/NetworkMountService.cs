using MacExplorer.Models;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Services.Impl;

public sealed class NetworkMountService : INetworkMountService
{
    private static readonly HttpClient HttpsProbe = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
    { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Func<IReadOnlyList<VolumeInfo>> _volumes;
    private readonly Func<string, string, string, CancellationToken, Task<string[]>> _mount;
    private readonly Func<string, Task<bool>> _unmount;
    public NetworkMountService() : this(
        () => OperatingSystem.IsMacOS() ? MacNetworkNative.GetVolumes().Where(v => v.IsNetwork).ToArray() : [],
        MountNativeAsync, path => Task.Run(() => MacSandboxNative.EjectVolume(path))) { }
    internal NetworkMountService(Func<IReadOnlyList<VolumeInfo>> volumes,
        Func<string, string, string, CancellationToken, Task<string[]>> mount, Func<string, Task<bool>> unmount)
    { _volumes = volumes; _mount = mount; _unmount = unmount; }
    private static async Task<string[]> MountNativeAsync(string uri, string user, string password, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(LocalizationText.Get("network.macos-only"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        if (new Uri(uri).Scheme == "https")
        {
            // NetFS can leave an unusable provisional WebDAV volume while waiting on a bad TLS handshake.
            // Validate with the system trust chain before asking it to mount; never send credentials here.
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Options, uri);
                using var response = await HttpsProbe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is >= 300 and < 400)
                    throw new IOException(LocalizationText.Get("network.webdav-redirect"));
            }
            catch (HttpRequestException ex) { throw new IOException(LocalizationText.Get("network.webdav-tls-error"), ex); }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            { throw new IOException(LocalizationText.Get("network.webdav-tls-error"), ex); }
        }
        try { return await MacNetworkNative.MountAsync(uri, user, password, timeout.Token); }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        { throw new IOException(LocalizationText.Get("network.mount-timeout"), ex); }
    }
    public IReadOnlyList<VolumeInfo> GetMountedVolumes() => _volumes();
    public async Task<string> MountAsync(RemoteServerInfo server, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = NetworkMountAddress.MountUri(server);
        var matches = GetMountedVolumes().Where(v => NetworkMountAddress.MatchesEndpoint(server, v.SourceUrl)
            && NetworkMountAddress.MatchesAccount(server, v.SourceUrl)).ToArray();
        var existing = matches.Length == 1 ? matches[0] : null;
        string? mountPath = existing?.Path;
        if (mountPath == null)
        {
            var user = server.Username;
            var uri = address.AbsoluteUri;
            if (server.ServiceType == RemoteServiceType.Smb && !string.IsNullOrWhiteSpace(server.Domain))
            {
                uri = "smb://" + Uri.EscapeDataString(server.Domain) + ";" + Uri.EscapeDataString(user) + "@" + uri[6..];
                user = "";
            }
            var paths = await _mount(uri, user, server.Password, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            mountPath = paths.FirstOrDefault();
        }
        if (string.IsNullOrEmpty(mountPath)) throw new IOException(LocalizationText.Get("network.no-mount"));
        return NetworkMountAddress.LocationWithin(server, mountPath);
    }
    public Task<bool> UnmountAsync(string mountPath) => _unmount(mountPath);
}
