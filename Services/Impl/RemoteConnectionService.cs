using System.Text.Json;
using MacExplorer.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace MacExplorer.Services.Impl;

public class RemoteConnectionService : IRemoteConnectionService, IDisposable
{
    private readonly Dictionary<string, SftpClient> _connections = new();
    private readonly HashSet<string> _ossConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteServerInfo> _servers = new();
    private readonly Dictionary<string, CancellationTokenSource> _reconnectTokens = new();
    private readonly string _configPath;
    private readonly ILogger<RemoteConnectionService>? _logger;

    private const int MaxReconnectAttempts = 3;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    public event EventHandler<string>? ConnectionLost;
    public event EventHandler<string>? Reconnecting;
    public event EventHandler<string>? ReconnectFailed;

    public RemoteConnectionService(ILogger<RemoteConnectionService>? logger = null)
    {
        _logger = logger;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MacExplorer", "remote-servers.json");
        LoadSavedServers();
    }

    public async Task ConnectAsync(RemoteServerInfo server, CancellationToken ct = default)
    {
        Disconnect(server.Id);

        if (server.IsOss)
        {
            await ConnectOssAsync(server, ct);
            return;
        }

        var client = CreateSftpClient(server);

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            client.Connect();
        }, ct);

        RegisterConnection(server, client);

        _logger?.LogInformation("Connected to {Server}", server.DisplayName);
    }

    public async Task GetOrConnectAsync(RemoteServerInfo server, CancellationToken ct = default)
    {
        if (IsConnected(server.Id)) return;
        await ConnectAsync(server, ct);
    }

    /// <summary>
    /// OSS is stateless HTTP, so "connecting" means proving the credentials can
    /// reach the bucket — that is what makes a bad key fail in the dialog rather
    /// than silently on the first listing.
    /// </summary>
    private async Task ConnectOssAsync(RemoteServerInfo server, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var client = OssClientFactory.Create(server);
            if (!client.DoesBucketExist(server.Bucket))
                throw new InvalidOperationException($"Bucket 不存在或无访问权限：{server.Bucket}");
        }, ct);

        _ossConnections.Add(server.Id);
        _servers[server.Id] = server;
        server.IsConnected = true;

        _logger?.LogInformation("Connected to OSS bucket {Bucket}", server.Bucket);
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
            try
            {
                if (client.IsConnected) client.Disconnect();
                client.Dispose();
            }
            catch { }
            _connections.Remove(serverId);
        }
        _ossConnections.Remove(serverId);
        if (_servers.TryGetValue(serverId, out var server))
        {
            server.IsConnected = false;
        }
    }

    public void DisconnectAll()
    {
        foreach (var id in _connections.Keys.Concat(_ossConnections).Distinct().ToList())
            Disconnect(id);
    }

    public bool IsConnected(string serverId)
    {
        if (_ossConnections.Contains(serverId)) return true;
        return _connections.TryGetValue(serverId, out var client) && client.IsConnected;
    }

    public RemoteServerInfo? GetServer(string serverId)
        => _servers.TryGetValue(serverId, out var server) ? server : null;

    public SftpClient? GetSftpClient(string serverId)
    {
        return _connections.TryGetValue(serverId, out var client) && client.IsConnected ? client : null;
    }

    public IReadOnlyList<RemoteServerInfo> GetSavedServers()
    {
        return _servers.Values.ToList().AsReadOnly();
    }

    public void SaveServer(RemoteServerInfo server)
    {
        _servers[server.Id] = server;
        SaveToDisk();
    }

    public void RemoveServer(string serverId)
    {
        Disconnect(serverId);
        _servers.Remove(serverId);
        SaveToDisk();
    }

    private SftpClient CreateSftpClient(RemoteServerInfo server)
    {
        var authMethods = new List<AuthenticationMethod>();
        if (server.AuthMethod == RemoteAuthMethod.PrivateKey && !string.IsNullOrEmpty(server.PrivateKeyPath))
        {
            var keyFile = new PrivateKeyFile(server.PrivateKeyPath);
            authMethods.Add(new PrivateKeyAuthenticationMethod(server.Username, keyFile));
        }
        else
        {
            authMethods.Add(new PasswordAuthenticationMethod(server.Username, server.Password));
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
        _servers[server.Id] = server;
        server.IsConnected = true;

        client.ErrorOccurred += (_, args) =>
        {
            _logger?.LogWarning("SFTP connection error for {Server}: {Exception}", server.DisplayName, args.Exception?.Message);
            server.IsConnected = false;
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
                var client = CreateSftpClient(server);
                await Task.Run(() => client.Connect(), cts.Token);
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
        ReconnectFailed?.Invoke(this, server.Id);
        _reconnectTokens.Remove(server.Id);
        cts.Dispose();
    }

    private void LoadSavedServers()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            var json = File.ReadAllText(_configPath);
            var servers = JsonSerializer.Deserialize<List<RemoteServerInfo>>(json);
            if (servers == null) return;
            foreach (var s in servers)
                _servers[s.Id] = s;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load saved remote servers");
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_servers.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save remote servers config");
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
