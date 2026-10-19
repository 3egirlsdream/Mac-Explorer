using MacExplorer.Models;
using Renci.SshNet;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class RemoteFileEditService : IRemoteFileEditService, IDisposable
{
    private readonly IRemoteConnectionService _connectionService;
    private readonly ILogger<RemoteFileEditService>? _logger;
    private readonly string _tempDir;
    private readonly IBackgroundTaskManager? _tasks;
    private readonly IFileOperationHistoryService? _history;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, (string RemotePath, string ServerId)> _watchMap = new();
    private readonly Dictionary<string, string> _remoteToLocalMap = new();
    private readonly HashSet<string> _uploading = new();

    public RemoteFileEditService(IRemoteConnectionService connectionService, ILogger<RemoteFileEditService>? logger = null,
        IBackgroundTaskManager? tasks = null, IFileOperationHistoryService? history = null)
    {
        _connectionService = connectionService;
        _logger = logger; _tasks = tasks; _history = history;
        _tempDir = Path.Combine(
            RuntimePaths.DataDirectory, "remote-edit");
        if (!Directory.Exists(_tempDir))
            Directory.CreateDirectory(_tempDir);
    }

    private static string MakeRemoteKey(string serverId, string remotePath)
        => $"{serverId}::{remotePath}";

    private SftpClient Client(string path, FileTaskContext context)
    {
        var serverId = VirtualPath.ParseRemotePath(path).ServerId;
        return context.Resource("sftp:" + serverId, () => _connectionService.LeaseClientAsync(serverId, context.CancellationToken).GetAwaiter().GetResult());
    }

    public Task<string> DownloadForEditAsync(string remotePath, string serverId)
    {
        var source = VirtualPath.BuildRemotePath(serverId, remotePath);
        var directory = Path.Combine(_tempDir, serverId,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(remotePath)))[..16]);
        return FileTaskContext.RunAsync(_tasks, new(BackgroundTaskKind.RemoteDownload, LocalizationText.Get("下载远程文件"), [source], directory), async context =>
        {
            Directory.CreateDirectory(directory);
            var output = await SftpTransfer.CopyAsync(source, directory, context, path => Client(path, context), null);
            var key = MakeRemoteKey(serverId, remotePath);
            if (_remoteToLocalMap.TryGetValue(key, out var previous)) StopWatching(previous);
            _remoteToLocalMap[key] = output;
            return output;
        });
    }

    public async Task UploadAsync(string localPath, string remotePath, string serverId)
    {
        lock (_uploading)
        {
            if (_uploading.Contains(localPath)) return;
            _uploading.Add(localPath);
        }
        try
        {
            var target = VirtualPath.BuildRemotePath(serverId, remotePath);
            var directory = VirtualPath.BuildRemotePath(serverId, Path.GetDirectoryName(remotePath) ?? "/");
            await FileTaskContext.RunAsync(_tasks, new(BackgroundTaskKind.RemoteUpload, LocalizationText.Get("上传远程文件"), [localPath], target), async context =>
            {
                await SftpTransfer.CopyAsync(localPath, directory, context, path => Client(path, context), null, target, true);
                if (_history != null) await _history.RecordIrreversibleAsync(target, LocalizationText.Get("远程修改不可撤销。"));
                return true;
            });
        }
        finally { lock (_uploading) _uploading.Remove(localPath); }
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
