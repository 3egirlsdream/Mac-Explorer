using MacExplorer.Models;
using Renci.SshNet;

namespace MacExplorer.Services;

public interface IRemoteConnectionService
{
    Func<Impl.SftpHostKey, CancellationToken, Task<bool>>? ConfirmHostKeyAsync { get; set; }
    Func<RemoteServerInfo, CancellationToken, Task<(string Passphrase, bool Remember)?>>? RequestPrivateKeyPassphraseAsync { get; set; }
    Impl.SftpHostKey? GetTrustedHostKey(string host, int port);
    void ForgetHostKey(string host, int port);
    string? CredentialLoadError { get; }
    void RetryCredentialMigration();
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
