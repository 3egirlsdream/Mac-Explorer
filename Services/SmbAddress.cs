using MacExplorer.Models;

namespace MacExplorer.Services;

public sealed record SmbAddress(string Host, int Port, string Share, string Path)
{
    public static SmbAddress Parse(string address, int port = 445, string share = "", string path = "/")
    {
        var input = address.Trim();
        if (!input.Contains("://", StringComparison.Ordinal))
            input = System.Net.IPAddress.TryParse(input, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "smb://[" + input + "]" : "smb://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "smb"
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(Impl.LocalizationText.Get("network.invalid-address"));
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (parts.Any(p => p.Contains('/') || p.Contains('\\') || p.Any(char.IsControl)))
            throw new ArgumentException(Impl.LocalizationText.Get("network.invalid-address"));
        if (uri.Port > 0) port = uri.Port;
        if (port < 1 || port > 65535) throw new ArgumentException(Impl.LocalizationText.Get("network.invalid-address"));
        if (parts.Length > 0) share = parts[0];
        if (parts.Length > 1) path = "/" + string.Join('/', parts.Skip(1));
        share = share.Trim();
        if (share.Contains('/') || share.Contains('\\') || share is "." or ".." || share.Any(char.IsControl))
            throw new ArgumentException(Impl.LocalizationText.Get("network.invalid-address"));
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(p => p is "." or ".." || p.Any(char.IsControl)))
            throw new ArgumentException(Impl.LocalizationText.Get("network.invalid-address"));
        return new(uri.IdnHost.Trim('[', ']'), port, share, "/" + string.Join('/', segments));
    }

    public static SmbAddress FromServer(RemoteServerInfo server) => Parse(server.Host, server.Port, server.ShareName, server.DefaultPath);
    public Uri MountUri => new UriBuilder("smb", Host, Port, "/" + Uri.EscapeDataString(Share)).Uri;
    public string LocationWithin(string mountPath) => System.IO.Path.GetFullPath(System.IO.Path.Combine(mountPath, Path.TrimStart('/')));
    public bool Matches(string? url, bool requireShare = true)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "smb") return false;
        return string.Equals(uri.IdnHost.TrimEnd('.'), Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && (uri.Port > 0 ? uri.Port : 445) == Port
            && (!requireShare || string.Equals(Uri.UnescapeDataString(uri.AbsolutePath).Trim('/'), Share, StringComparison.OrdinalIgnoreCase));
    }
}
