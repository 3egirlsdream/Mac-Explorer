using MacExplorer.Models;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Services.Impl;

public sealed class SmbMountService : ISmbMountService
{
    private readonly Func<IReadOnlyList<VolumeInfo>> _volumes;
    private readonly Func<string, string, string, CancellationToken, Task<string[]>> _mount;
    private readonly Func<string, Task<bool>> _unmount;
    public SmbMountService() : this(
        () => OperatingSystem.IsMacOS() ? MacNetworkNative.GetVolumes().Where(v => v.IsNetwork).ToArray() : [],
        MountNativeAsync, path => Task.Run(() => MacSandboxNative.EjectVolume(path))) { }
    internal SmbMountService(Func<IReadOnlyList<VolumeInfo>> volumes,
        Func<string, string, string, CancellationToken, Task<string[]>> mount, Func<string, Task<bool>> unmount)
    { _volumes = volumes; _mount = mount; _unmount = unmount; }
    private static Task<string[]> MountNativeAsync(string uri, string user, string password, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException(LocalizationText.Get("network.macos-only"));
        return MacNetworkNative.MountAsync(uri, user, password, token);
    }
    public IReadOnlyList<VolumeInfo> GetMountedVolumes() => _volumes();
    public async Task<string> MountAsync(RemoteServerInfo server, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = SmbAddress.FromServer(server);
        var existing = GetMountedVolumes().FirstOrDefault(v => address.Matches(v.SourceUrl) && MatchesUser(v.SourceUrl, server.Username, server.Domain));
        string? mountPath = existing?.Path;
        if (mountPath == null)
        {
            var user = server.Username;
            var uri = address.MountUri.AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(server.Domain))
            {
                uri = "smb://" + Uri.EscapeDataString(server.Domain) + ";" + Uri.EscapeDataString(user) + "@" + uri[6..];
                user = "";
            }
            var paths = await _mount(uri, user, server.Password, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            mountPath = paths.FirstOrDefault();
        }
        if (string.IsNullOrEmpty(mountPath)) throw new IOException(LocalizationText.Get("network.no-mount"));
        return address.LocationWithin(mountPath);
    }
    private static bool MatchesUser(string? source, string username, string domain)
    {
        if (string.IsNullOrEmpty(username)) return false;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        var account = Uri.UnescapeDataString(uri.UserInfo).Replace('\\', ';').Split(';', 2);
        return string.Equals(account[^1], username, StringComparison.OrdinalIgnoreCase)
            && string.Equals(account.Length == 2 ? account[0] : "", domain, StringComparison.OrdinalIgnoreCase);
    }
    public Task<bool> UnmountAsync(string mountPath) => _unmount(mountPath);
}
