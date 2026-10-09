using System.Text.Json;
using MacExplorer.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace MacExplorer.Services.Impl;

public class RemoteConnectionService : IRemoteConnectionService, IDisposable
{
    private readonly Dictionary<string, SftpClient> _connections = new();
    private readonly Dictionary<string, RemoteServerInfo> _servers = new();
    private readonly Dictionary<string, RemoteServerInfo> _connectedServers = new();
    private readonly Dictionary<string, CancellationTokenSource> _reconnectTokens = new();
    private readonly string _configPath;
    private readonly ICredentialStore _credentials;
    private readonly SftpHostKeyStore _hostKeys;
    private bool _configurationLoadFailed;
    public Func<SftpHostKey, CancellationToken, Task<bool>>? ConfirmHostKeyAsync { get; set; }
    public Func<RemoteServerInfo, CancellationToken, Task<(string Passphrase, bool Remember)?>>? RequestPrivateKeyPassphraseAsync { get; set; }
    public string? CredentialLoadError { get; private set; }
    public SftpHostKey? GetTrustedHostKey(string host, int port) => _hostKeys.Get(host, port);
    public void ForgetHostKey(string host, int port)
    {
        foreach (var server in _connectedServers.Values.ToList())
            if (SftpHostKeyStore.NormalizeHost(server.Host) == SftpHostKeyStore.NormalizeHost(host) && server.Port == port)
                Disconnect(server.Id);
        _hostKeys.Forget(host, port);
    }
    public void RetryCredentialMigration() => LoadSavedServers();
    private readonly ILogger<RemoteConnectionService>? _logger;

    private const int MaxReconnectAttempts = 3;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    public event EventHandler<string>? ConnectionLost;
    public event EventHandler<string>? Reconnecting;
    public event EventHandler<string>? ReconnectFailed;

    public RemoteConnectionService(ILogger<RemoteConnectionService>? logger = null)
        : this(Path.Combine(RuntimePaths.DataDirectory, "remote-servers.json"), new DatabaseCredentialStore("com.macexplorer.sftp"), logger) { }

    internal RemoteConnectionService(string configPath, ICredentialStore credentials, ILogger<RemoteConnectionService>? logger = null)
    {
        _logger = logger; _configPath = configPath; _credentials = credentials;
        _hostKeys = new SftpHostKeyStore(Path.Combine(Path.GetDirectoryName(configPath)!, "sftp-known-hosts.json"));
        LoadSavedServers();
    }

    public async Task<SftpClient> ConnectAsync(RemoteServerInfo server, CancellationToken ct = default)
    {
        Disconnect(server.Id);

        var candidate = CopyServer(server);
        var client = await ConnectVerifiedAsync(candidate, true, ct);

        try
        {
            ct.ThrowIfCancellationRequested();
            // Saving a prompted passphrase requires explicit opt-in and a successful connection.
            if (_servers.TryGetValue(server.Id, out var saved) && candidate.AuthMethod == RemoteAuthMethod.PrivateKey
                && (candidate.RememberPrivateKeyPassphrase || saved.RememberPrivateKeyPassphrase != candidate.RememberPrivateKeyPassphrase))
                SaveServer(candidate);
            server.PrivateKeyPassphrase = candidate.PrivateKeyPassphrase;
            server.RememberPrivateKeyPassphrase = candidate.RememberPrivateKeyPassphrase;
            RegisterConnection(server, client);
        }
        catch { client.Dispose(); throw; }

        _logger?.LogInformation("Connected to {Server}", server.DisplayName);
        return client;
    }

    public async Task<SftpClient> GetOrConnectAsync(RemoteServerInfo server, CancellationToken ct = default)
    {
        if (_connections.TryGetValue(server.Id, out var existing) && existing.IsConnected
            && existing.ConnectionInfo.Host == server.Host && existing.ConnectionInfo.Port == server.Port
            && existing.ConnectionInfo.Username == server.Username)
            return existing;

        return await ConnectAsync(server, ct);
    }

    public void Disconnect(string serverId)
    {
        // Cancel any pending reconnect
        if (_reconnectTokens.TryGetValue(serverId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _reconnectTokens.Remove(serverId);
        }

        if (_connections.TryGetValue(serverId, out var client))
        {
            _connections.Remove(serverId);
            try
            {
                if (client.IsConnected) client.Disconnect();
                client.Dispose();
            }
            catch { }
        }
        if (_servers.TryGetValue(serverId, out var server))
        {
            server.IsConnected = false;
        }
        if (_connectedServers.Remove(serverId, out var connected))
        {
            connected.IsConnected = false;
            if (!connected.RememberPrivateKeyPassphrase) connected.PrivateKeyPassphrase = "";
        }
    }

    public void DisconnectAll()
    {
        foreach (var id in _connections.Keys.Concat(_connectedServers.Keys).Distinct().ToList())
            Disconnect(id);
    }

    public bool IsConnected(string serverId)
    {
        return _connections.TryGetValue(serverId, out var client) && client.IsConnected;
    }

    public SftpClient? GetClient(string serverId)
    {
        return _connections.TryGetValue(serverId, out var client) && client.IsConnected ? client : null;
    }

    public IReadOnlyList<RemoteServerInfo> GetSavedServers()
    {
        return _servers.Values.ToList().AsReadOnly();
    }

    public void SaveServer(RemoteServerInfo server)
    {
        if (_configurationLoadFailed) throw new InvalidOperationException("原连接配置无法读取，尚未改写。请恢复配置并重试凭据加载后再保存。");
        var savedServer = CopyServer(server);
        if (server.AuthMethod != RemoteAuthMethod.Password) savedServer.Password = "";
        if (server.AuthMethod != RemoteAuthMethod.PrivateKey || !server.RememberPrivateKeyPassphrase)
            savedServer.PrivateKeyPassphrase = "";
        var candidate = new Dictionary<string, RemoteServerInfo>(_servers) { [server.Id] = savedServer };
        UpdateCredentials(server.Id, () =>
        {
            if (server.AuthMethod == RemoteAuthMethod.Password) _credentials.Save(server.Id, server.Password);
            else _credentials.Delete(server.Id);
            if (server.AuthMethod == RemoteAuthMethod.PrivateKey && server.RememberPrivateKeyPassphrase)
                _credentials.Save(KeyAccount(server.Id), server.PrivateKeyPassphrase);
            else _credentials.Delete(KeyAccount(server.Id));
        }, () => SaveToDisk(candidate.Values));
        savedServer.IsConnected = IsConnected(server.Id);
        _servers[server.Id] = savedServer;
    }

    public void RemoveServer(string serverId)
    {
        if (_configurationLoadFailed) throw new InvalidOperationException("原连接配置无法读取，尚未删除。请恢复配置并重试凭据加载。");
        var candidate = new Dictionary<string, RemoteServerInfo>(_servers);
        candidate.Remove(serverId);
        UpdateCredentials(serverId, () =>
        {
            _credentials.Delete(serverId);
            _credentials.Delete(KeyAccount(serverId));
        }, () => SaveToDisk(candidate.Values));
        _servers.Remove(serverId);
        Disconnect(serverId);
    }

    private static RemoteServerInfo CopyServer(RemoteServerInfo server) => new RemoteServerInfo
        {
            Id = server.Id, Name = server.Name, Host = server.Host, Port = server.Port, Username = server.Username,
            DefaultPath = server.DefaultPath, AuthMethod = server.AuthMethod, Password = server.Password,
            PrivateKeyPath = server.PrivateKeyPath, PrivateKeyPassphrase = server.PrivateKeyPassphrase,
            RememberPrivateKeyPassphrase = server.RememberPrivateKeyPassphrase
    };

    private static string KeyAccount(string id) => id + ":private-key-passphrase";

    private void UpdateCredentials(string id, Action update, Action persist)
    {
        var accounts = new[] { id, KeyAccount(id) };
        var previous = accounts.ToDictionary(account => account, account => _credentials.Read(account));
        try { update(); persist(); }
        catch
        {
            try
            {
                foreach (var (account, secret) in previous)
                {
                    if (_credentials.Read(account) == secret) continue;
                    if (secret != null) _credentials.Save(account, secret); else _credentials.Delete(account);
                }
            }
            catch (Exception rollback)
            {
                throw new InvalidOperationException("操作未完成，恢复原凭据也失败。原配置已保留，请检查应用数据库是否可写后重试。", rollback);
            }
            throw;
        }
    }

    private async Task<SftpClient> ConnectVerifiedAsync(RemoteServerInfo server, bool allowPrompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SftpClient client;
        try { client = CreateSftpClient(server); }
        catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException) when (allowPrompt && RequestPrivateKeyPassphraseAsync != null)
        {
            var answer = await RequestPrivateKeyPassphraseAsync(server, ct);
            ct.ThrowIfCancellationRequested();
            if (answer == null) throw new OperationCanceledException("私钥口令输入已取消。", ct);
            server.PrivateKeyPassphrase = answer.Value.Passphrase;
            server.RememberPrivateKeyPassphrase = answer.Value.Remember;
            client = CreateSftpClient(server);
        }
        Exception? verificationError = null;
        var host = client.ConnectionInfo.Host;
        var port = client.ConnectionInfo.Port;
        client.HostKeyReceived += (_, args) =>
        {
            args.CanTrust = false;
            try
            {
                _hostKeys.VerifyAsync(host, port, args.HostKeyName, args.HostKey,
                    allowPrompt ? ConfirmHostKeyAsync : null, ct).GetAwaiter().GetResult();
                ct.ThrowIfCancellationRequested();
                args.CanTrust = true;
            }
            catch (Exception ex) { verificationError = ex; }
        };
        try
        {
            await Task.Run(() => { ct.ThrowIfCancellationRequested(); client.Connect(); }, ct);
            ct.ThrowIfCancellationRequested();
            return client;
        }
        catch
        {
            client.Dispose();
            if (verificationError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(verificationError).Throw();
            throw;
        }
    }

    private SftpClient CreateSftpClient(RemoteServerInfo server)
    {
        var authMethods = new List<AuthenticationMethod>();
        if (server.AuthMethod == RemoteAuthMethod.PrivateKey && !string.IsNullOrEmpty(server.PrivateKeyPath))
        {
            var keyPath = DirectoryAccess.Current.ResolvePath(server.PrivateKeyPath);
            DirectoryAccess.Current.EnsureAccess(keyPath);
            var passphrase = server.PrivateKeyPassphrase;
            if (string.IsNullOrEmpty(passphrase) && server.RememberPrivateKeyPassphrase
                && _servers.TryGetValue(server.Id, out var saved) && saved.PrivateKeyPath == server.PrivateKeyPath)
                passphrase = _credentials.Read(KeyAccount(server.Id)) ?? "";
            PrivateKeyFile keyFile;
            try { keyFile = new PrivateKeyFile(keyPath, passphrase); }
            catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException) { throw; }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or System.Security.Cryptography.CryptographicException)
            { throw new InvalidOperationException("无法解密或读取私钥，请检查私钥口令及文件格式。", ex); }
            authMethods.Add(new PrivateKeyAuthenticationMethod(server.Username, keyFile));
        }
        else
        {
            authMethods.Add(new PasswordAuthenticationMethod(server.Username,
                string.IsNullOrEmpty(server.Password) && _servers.ContainsKey(server.Id) ? _credentials.Read(server.Id) ?? "" : server.Password));
        }

        var connectionInfo = new ConnectionInfo(server.Host, server.Port, server.Username, authMethods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        return new SftpClient(connectionInfo);
    }

    private void RegisterConnection(RemoteServerInfo server, SftpClient client)
    {
        _connections[server.Id] = client;
        _connectedServers[server.Id] = server;
        server.IsConnected = true;
        if (_servers.TryGetValue(server.Id, out var saved)) saved.IsConnected = true;

        client.ErrorOccurred += (_, args) =>
        {
            if (!_connections.TryGetValue(server.Id, out var active) || !ReferenceEquals(active, client)) return;
            _logger?.LogWarning("SFTP connection error for {Server}: {Exception}", server.DisplayName, args.Exception?.Message);
            server.IsConnected = false;
            if (_servers.TryGetValue(server.Id, out var savedServer)) savedServer.IsConnected = false;
            _connections.Remove(server.Id);
            ConnectionLost?.Invoke(this, server.Id);

            // Attempt auto-reconnect
            _ = TryReconnectAsync(server);
        };
    }

    private async Task TryReconnectAsync(RemoteServerInfo server)
    {
        // Cancel any existing reconnect attempt for this server
        if (_reconnectTokens.TryGetValue(server.Id, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _reconnectTokens[server.Id] = cts;

        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            if (cts.IsCancellationRequested) return;

            _logger?.LogInformation("Reconnect attempt {Attempt}/{Max} for {Server}", attempt, MaxReconnectAttempts, server.DisplayName);
            Reconnecting?.Invoke(this, server.Id);

            try
            {
                await Task.Delay(ReconnectDelay, cts.Token);
                var client = await ConnectVerifiedAsync(server, false, cts.Token);
                RegisterConnection(server, client);

                _logger?.LogInformation("Reconnected to {Server}", server.DisplayName);
                _reconnectTokens.Remove(server.Id);
                cts.Dispose();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Reconnect attempt {Attempt} failed for {Server}", attempt, server.DisplayName);
            }
        }

        _logger?.LogWarning("All reconnect attempts failed for {Server}", server.DisplayName);
        if (!server.RememberPrivateKeyPassphrase) server.PrivateKeyPassphrase = "";
        ReconnectFailed?.Invoke(this, server.Id);
        _reconnectTokens.Remove(server.Id);
        cts.Dispose();
    }

    private void LoadSavedServers()
    {
        CredentialLoadError = null;
        _configurationLoadFailed = true;
        try
        {
            if (!File.Exists(_configPath)) { _configurationLoadFailed = false; return; }
            var json = File.ReadAllText(_configPath);
            var servers = JsonSerializer.Deserialize<List<RemoteServerInfo>>(json);
            if (servers == null) throw new JsonException("连接配置为空，未执行迁移。");
            _configurationLoadFailed = false;
            foreach (var saved in servers) _servers[saved.Id] = saved;
            var migrated = false;
            foreach (var s in servers)
            {
                if (!string.IsNullOrEmpty(s.Password)) { _credentials.Save(s.Id, s.Password); migrated = true; }
                else if (s.AuthMethod == RemoteAuthMethod.Password) s.Password = _credentials.Read(s.Id) ?? "";
                if (s.RememberPrivateKeyPassphrase) s.PrivateKeyPassphrase = _credentials.Read(KeyAccount(s.Id)) ?? "";
                _servers[s.Id] = s;
            }
            if (migrated) SaveToDisk(_servers.Values);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load saved remote servers");
            CredentialLoadError = $"凭据加载或迁移尚未完成：{ex.Message} 原配置已保留，请检查应用数据库是否可访问后重试。";
        }
    }

    private void SaveToDisk(IEnumerable<RemoteServerInfo> servers)
    {
        try
        {
            // A previous multi-server upgrade may have failed partway through.
            // Persist every remaining secret before omitting any from the JSON.
            var snapshot = servers.ToList();
            foreach (var server in snapshot)
                if (!string.IsNullOrEmpty(server.Password) && server.AuthMethod == RemoteAuthMethod.Password) _credentials.Save(server.Id, server.Password);
            var dir = Path.GetDirectoryName(_configPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info =>
            {
                if (info.Type != typeof(RemoteServerInfo)) return;
                foreach (var property in info.Properties)
                    if (property.Name == nameof(RemoteServerInfo.Password)) property.ShouldSerialize = (_, _) => false;
            });
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                { WriteIndented = true, TypeInfoResolver = resolver });
            var temporary = _configPath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _configPath, true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save remote servers config");
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var cts in _reconnectTokens.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { }
        }
        _reconnectTokens.Clear();
        DisconnectAll();
    }
}
