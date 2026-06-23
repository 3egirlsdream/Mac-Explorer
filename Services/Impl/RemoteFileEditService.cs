using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class RemoteFileEditService : IRemoteFileEditService, IDisposable
{
    private readonly IRemoteConnectionService _connectionService;
    private readonly ILogger<RemoteFileEditService>? _logger;
    private readonly string _tempDir;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, (string RemotePath, string ServerId)> _watchMap = new();
    private readonly Dictionary<string, string> _remoteToLocalMap = new();
    private readonly HashSet<string> _downloading = new();
    private readonly HashSet<string> _uploading = new();

    public RemoteFileEditService(IRemoteConnectionService connectionService, ILogger<RemoteFileEditService>? logger = null)
    {
        _connectionService = connectionService;
        _logger = logger;
        _tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MacExplorer", "remote-edit");
        if (!Directory.Exists(_tempDir))
            Directory.CreateDirectory(_tempDir);
    }

    private static string MakeRemoteKey(string serverId, string remotePath)
        => $"{serverId}::{remotePath}";

    public async Task<string> DownloadForEditAsync(string remotePath, string serverId)
    {
        var client = _connectionService.GetClient(serverId)
            ?? throw new InvalidOperationException("Server not connected");

        var serverDir = Path.Combine(_tempDir, serverId);
        if (!Directory.Exists(serverDir))
            Directory.CreateDirectory(serverDir);

        var remoteKey = MakeRemoteKey(serverId, remotePath);
        string localPath;

        if (_remoteToLocalMap.TryGetValue(remoteKey, out var existingLocalPath) && File.Exists(existingLocalPath))
        {
            localPath = existingLocalPath;
        }
        else
        {
            var fileName = Path.GetFileName(remotePath);
            localPath = Path.Combine(serverDir, fileName);
            _remoteToLocalMap[remoteKey] = localPath;
        }

        // Mark as downloading to prevent watcher from uploading
        lock (_downloading) _downloading.Add(localPath);

        // Temporarily disable watcher
        var watcherDisabled = false;
        if (_watchers.TryGetValue(localPath, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcherDisabled = true;
        }

        try
        {
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        using var remoteStream = client.OpenRead(remotePath);
                        using var localStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                        var buffer = new byte[81920];
                        int bytesRead;
                        while ((bytesRead = remoteStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            localStream.Write(buffer, 0, bytesRead);
                        }
                    }, CancellationToken.None);
                    break;
                }
                catch (IOException) when (retry < 2)
                {
                    await Task.Delay(500);
                }
            }

            _logger?.LogInformation("Downloaded {Remote} to {Local}", remotePath, localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to download {Remote}", remotePath);
            // Remove mapping on failure
            _remoteToLocalMap.Remove(remoteKey);
            throw;
        }
        finally
        {
            lock (_downloading) _downloading.Remove(localPath);
            // Re-enable watcher
            if (watcherDisabled && watcher != null)
            {
                watcher.EnableRaisingEvents = true;
            }
        }
    }

    public async Task UploadAsync(string localPath, string remotePath, string serverId)
    {
        // Skip upload if currently downloading
        lock (_downloading)
        {
            if (_downloading.Contains(localPath)) return;
        }

        var key = localPath;
        lock (_uploading)
        {
            if (_uploading.Contains(key)) return;
            _uploading.Add(key);
        }

        try
        {
            var client = _connectionService.GetClient(serverId)
                ?? throw new InvalidOperationException("Server not connected");

            await Task.Run(() =>
            {
                using var localStream = File.OpenRead(localPath);
                using var remoteStream = client.OpenWrite(remotePath);
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = localStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    remoteStream.Write(buffer, 0, bytesRead);
                }
            });

            _logger?.LogInformation("Uploaded {Local} to {Remote}", localPath, remotePath);
        }
        finally
        {
            lock (_uploading) { _uploading.Remove(key); }
        }
    }

    public void WatchForChanges(string localTempPath, string remotePath, string serverId)
    {
        StopWatching(localTempPath);

        var directory = Path.GetDirectoryName(localTempPath)!;
        var fileName = Path.GetFileName(localTempPath);

        var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        watcher.Changed += async (_, e) =>
        {
            if (e.FullPath != localTempPath) return;

            // Skip if currently downloading
            lock (_downloading)
            {
                if (_downloading.Contains(localTempPath)) return;
            }

            try
            {
                // Debounce - wait a bit for the write to finish
                await Task.Delay(500);
                await UploadAsync(localTempPath, remotePath, serverId);
                _logger?.LogInformation("Auto-synced {Local} to {Remote}", localTempPath, remotePath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to auto-sync {Local}", localTempPath);
            }
        };

        _watchers[localTempPath] = watcher;
        _watchMap[localTempPath] = (remotePath, serverId);
    }

    public void StopWatching(string localTempPath)
    {
        if (_watchers.TryGetValue(localTempPath, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watchers.Remove(localTempPath);
            _watchMap.Remove(localTempPath);
        }
    }

    public void CleanupTempFiles()
    {
        foreach (var watcher in _watchers.Values)
        {
            try { watcher.Dispose(); } catch { }
        }
        _watchers.Clear();
        _watchMap.Clear();
        _remoteToLocalMap.Clear();

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    public void Dispose()
    {
        CleanupTempFiles();
    }
}
