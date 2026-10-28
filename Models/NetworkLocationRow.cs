using MacExplorer.Services.Impl;

namespace MacExplorer.Models;

public sealed record NetworkLocationRow(string Name, string Detail, RemoteServerInfo? Server = null,
    VolumeInfo? Volume = null, bool Connected = false, bool Discovered = false)
{
    public bool IsMounted => Volume != null;
    public bool IsConnected => IsMounted || Connected;
    public string DisplayName => Name;
    public string IconData => Assets.Icons.Network;
    public string Address
    {
        get
        {
            if (Server?.ServiceType == RemoteServiceType.WebDav) return Server.WebDavUrl;
            if (Server != null) return FormatHost(Server.Host, Server.Port, Server.ServiceType == RemoteServiceType.Sftp ? 22 : 445);
            if (!Uri.TryCreate(Volume?.SourceUrl, UriKind.Absolute, out var uri)) return "—";
            return new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
        }
    }
    public string Protocol => Server != null ? Server.ServiceType switch
    {
        RemoteServiceType.Sftp => "SFTP", RemoteServiceType.Smb => "SMB", RemoteServiceType.WebDav => "WebDAV", _ => "—"
    } : Uri.TryCreate(Volume?.SourceUrl, UriKind.Absolute, out var uri)
        ? uri.Scheme is "http" or "https" or "dav" or "davs" ? "WebDAV" : uri.Scheme.ToUpperInvariant() : "—";
    public string Status => LocalizationText.Get(IsConnected ? "已连接" : "未连接");
    public string ActionText => LocalizationText.Get(IsConnected ? "network.open" : "network.connect-short");
    public string LastConnected => Server?.LastConnectedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "—";
    public string LastConnectedDetail => Server?.LastConnectedAt?.ToLocalTime().ToString("F", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
    public string Identity => Server?.Id ?? Volume?.Path ?? "";
    private static string FormatHost(string host, int port, int defaultPort) => port == defaultPort ? host : $"{(host.Contains(':') ? $"[{host}]" : host)}:{port}";
}
