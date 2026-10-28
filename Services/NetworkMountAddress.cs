using MacExplorer.Models;

namespace MacExplorer.Services;

internal static class NetworkMountAddress
{
    internal static Uri MountUri(RemoteServerInfo server) => server.ServiceType switch
    {
        RemoteServiceType.Smb => SmbAddress.FromServer(server).MountUri,
        RemoteServiceType.WebDav => WebDavAddress.Parse(server.WebDavUrl).Uri,
        _ => throw new ArgumentException("This protocol does not use a system mount.")
    };

    internal static bool MatchesEndpoint(RemoteServerInfo server, string? source)
    {
        try
        {
            return server.ServiceType switch
            {
                RemoteServiceType.Smb => SmbAddress.FromServer(server).Matches(source, requireShare: !string.IsNullOrEmpty(server.ShareName)),
                RemoteServiceType.WebDav => WebDavAddress.Parse(server.WebDavUrl).Matches(source),
                _ => false
            };
        }
        catch (ArgumentException) { return false; }
    }

    internal static bool MatchesAccount(RemoteServerInfo server, string? source)
    {
        // A remount URL without an account does not prove which credentials the OS used.
        if (string.IsNullOrEmpty(server.Username)) return false;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        var account = Uri.UnescapeDataString(uri.UserInfo);
        if (server.ServiceType == RemoteServiceType.WebDav)
            return string.Equals(account, server.Username, StringComparison.Ordinal);
        var parts = account.Replace('\\', ';').Split(';', 2);
        return string.Equals(parts[^1], server.Username, StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts.Length == 2 ? parts[0] : "", server.Domain, StringComparison.OrdinalIgnoreCase);
    }

    internal static string LocationWithin(RemoteServerInfo server, string mountPath) => server.ServiceType == RemoteServiceType.Smb
        ? SmbAddress.FromServer(server).LocationWithin(mountPath) : mountPath;
}
