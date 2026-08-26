using MacExplorer.Models;
using Renci.SshNet;

namespace MacExplorer.Services;

public interface IRemoteConnectionService
{
    Task GetOrConnectAsync(RemoteServerInfo server, CancellationToken ct = default);
    Task ConnectAsync(RemoteServerInfo server, CancellationToken ct = default);
    void Disconnect(string serverId);
    void DisconnectAll();
    bool IsConnected(string serverId);

    /// <summary>The saved/connected server descriptor, used to resolve its protocol and credentials.</summary>
    RemoteServerInfo? GetServer(string serverId);

    /// <summary>The live SFTP session, or null when the server is not connected or is not an SFTP server.</summary>
    SftpClient? GetSftpClient(string serverId);

    IReadOnlyList<RemoteServerInfo> GetSavedServers();
    void SaveServer(RemoteServerInfo server);
    void RemoveServer(string serverId);
    event EventHandler<string>? ConnectionLost;
    event EventHandler<string>? Reconnecting;
    event EventHandler<string>? ReconnectFailed;
}
