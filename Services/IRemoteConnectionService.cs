using MacExplorer.Models;
using Renci.SshNet;

namespace MacExplorer.Services;

public interface IRemoteConnectionService
{
    Task<SftpClient> GetOrConnectAsync(RemoteServerInfo server, CancellationToken ct = default);
    Task<SftpClient> ConnectAsync(RemoteServerInfo server, CancellationToken ct = default);
    void Disconnect(string serverId);
    void DisconnectAll();
    bool IsConnected(string serverId);
    SftpClient? GetClient(string serverId);
    IReadOnlyList<RemoteServerInfo> GetSavedServers();
    void SaveServer(RemoteServerInfo server);
    void RemoveServer(string serverId);
    event EventHandler<string>? ConnectionLost;
    event EventHandler<string>? Reconnecting;
    event EventHandler<string>? ReconnectFailed;
}
