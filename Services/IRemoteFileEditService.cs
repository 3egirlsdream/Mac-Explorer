namespace MacExplorer.Services;

public interface IRemoteFileEditService
{
    Task<string> DownloadForEditAsync(string remotePath, string serverId);
    Task UploadAsync(string localPath, string remotePath, string serverId);
    void WatchForChanges(string localTempPath, string remotePath, string serverId);
    void StopWatching(string localTempPath);
    void CleanupTempFiles();
}
