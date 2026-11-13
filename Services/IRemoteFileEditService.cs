namespace MacExplorer.Services;

public interface IRemoteFileEditService
{
    IReadOnlyList<Models.RemoteEditSession> Sessions => [];
    bool HasPendingChanges => Sessions.Any(s => s.HasPendingChanges);
    event Action? Changed { add { } remove { } }
    Task RetryAsync(string localPath) => Task.CompletedTask;
    Task ReconnectAsync(string localPath) => RetryAsync(localPath);
    Task ResolveConflictAsync(string localPath, bool overwriteRemote) => Task.CompletedTask;
    Task StopAsync() => Task.CompletedTask;
    Task<string> DownloadForEditAsync(string remotePath, string serverId);
    Task UploadAsync(string localPath, string remotePath, string serverId);
    void WatchForChanges(string localTempPath, string remotePath, string serverId);
    void StopWatching(string localTempPath);
    void CleanupTempFiles();
}
