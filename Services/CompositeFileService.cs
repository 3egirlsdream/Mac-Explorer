using System.Runtime.CompilerServices;
using MacExplorer.Models;

namespace MacExplorer.Services;

public class CompositeFileService : IFileService
{
    private readonly IFileService _localService;
    private readonly SftpFileService _remoteService;
    private readonly IBackgroundTaskManager? _taskManager;

    public CompositeFileService(IFileService localService, SftpFileService remoteService, IBackgroundTaskManager? taskManager = null)
    {
        _localService = localService;
        _remoteService = remoteService;
        _taskManager = taskManager;
    }

    private IFileService Resolve(string? path)
        => VirtualPath.IsRemotePath(path) ? _remoteService : _localService;

    public string HomeDirectory => _localService.HomeDirectory;
    public string RootDirectory => _localService.RootDirectory;
    public string TrashDirectory => _localService.TrashDirectory;

    public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
        => Resolve(path).GetDirectoryContentsAsync(path, cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(string path, int batchSize = 256, CancellationToken cancellationToken = default)
        => Resolve(path).EnumerateDirectoryBatchesAsync(path, batchSize, cancellationToken);

    public Task<FileSystemEntry?> GetEntryAsync(string path)
        => Resolve(path).GetEntryAsync(path);

    public Task<bool> ExistsAsync(string path)
        => Resolve(path).ExistsAsync(path);

    public Task<string> CreateFolderAsync(string parentPath, string name)
        => Resolve(parentPath).CreateFolderAsync(parentPath, name);

    public Task<string> CreateFileAsync(string parentPath, string name)
        => Resolve(parentPath).CreateFileAsync(parentPath, name);

    public Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content)
        => Resolve(parentPath).CreateFileWithContentAsync(parentPath, name, content);

    public Task DeleteAsync(string path, bool moveToTrash = true)
        => Resolve(path).DeleteAsync(path, moveToTrash);

    public Task RenameAsync(string path, string newName)
        => Resolve(path).RenameAsync(path, newName);

    public async Task MoveAsync(string sourcePath, string destinationDirectory, bool overwrite = false)
    {
        var srcIsRemote = VirtualPath.IsRemotePath(sourcePath);
        var dstIsRemote = VirtualPath.IsRemotePath(destinationDirectory);

        if (srcIsRemote == dstIsRemote)
        {
            await Resolve(sourcePath).MoveAsync(sourcePath, destinationDirectory, overwrite);
            return;
        }

        // Cross-service: copy then delete
        await CopyAsync(sourcePath, destinationDirectory);
        await DeleteAsync(sourcePath, moveToTrash: false);
    }

    public async Task CopyAsync(string sourcePath, string destinationDirectory)
    {
        var srcIsRemote = VirtualPath.IsRemotePath(sourcePath);
        var dstIsRemote = VirtualPath.IsRemotePath(destinationDirectory);

        if (srcIsRemote && dstIsRemote)
        {
            await _remoteService.CopyAsync(sourcePath, destinationDirectory);
            return;
        }

        if (!srcIsRemote && !dstIsRemote)
        {
            await _localService.CopyAsync(sourcePath, destinationDirectory);
            return;
        }

        var fileName = Path.GetFileName(srcIsRemote
            ? VirtualPath.ParseRemotePath(sourcePath).RemotePath
            : sourcePath);

        if (!srcIsRemote && dstIsRemote)
        {
            // Local → Remote: upload with progress
            var (_, remoteDest) = VirtualPath.ParseRemotePath(destinationDirectory);
            var remoteClient = _remoteService.GetConnectedClient();
            if (remoteClient == null) throw new InvalidOperationException("Remote server not connected");

            var remoteFilePath = remoteDest.TrimEnd('/') + "/" + fileName;
            var fileSize = new FileInfo(sourcePath).Length;
            var label = $"上传 {fileName}";
            var task = _taskManager?.AddTask(label);

            try
            {
                await Task.Run(() =>
                {
                    using var localStream = File.OpenRead(sourcePath);
                    using var remoteStream = remoteClient.OpenWrite(remoteFilePath);
                    CopyWithProgress(localStream, remoteStream, fileSize, task);
                });

                if (task != null)
                    _taskManager?.CompleteTask(task.Id);
            }
            catch (Exception ex)
            {
                if (task != null)
                    _taskManager?.FailTask(task.Id, ex.Message);
                throw;
            }
            return;
        }

        // Remote → Local: download with progress
        var (serverId, remoteSrc) = VirtualPath.ParseRemotePath(sourcePath);
        var client = _remoteService.GetConnectedClient();
        if (client == null) throw new InvalidOperationException("Remote server not connected");

        var localDestPath = Path.Combine(destinationDirectory, fileName);
        var dlFileSize = GetRemoteFileSize(client, remoteSrc);
        var dlLabel = $"下载 {fileName}";
        var dlTask = _taskManager?.AddTask(dlLabel);

        try
        {
            await Task.Run(() =>
            {
                using var remoteStream = client.OpenRead(remoteSrc);
                using var localStream = File.Create(localDestPath);
                CopyWithProgress(remoteStream, localStream, dlFileSize, dlTask);
            });

            if (dlTask != null)
                _taskManager?.CompleteTask(dlTask.Id);
        }
        catch (Exception ex)
        {
            if (dlTask != null)
                _taskManager?.FailTask(dlTask.Id, ex.Message);
            throw;
        }
    }

    private static long GetRemoteFileSize(Renci.SshNet.SftpClient client, string remotePath)
    {
        try
        {
            return client.GetAttributes(remotePath)?.Size ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private void CopyWithProgress(Stream source, Stream destination, long totalBytes, BackgroundTaskInfo? task)
    {
        const int bufferSize = 81920;
        var buffer = new byte[bufferSize];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;

            if (task != null && totalBytes > 0)
            {
                var progress = (double)totalRead / totalBytes * 100;
                _taskManager?.UpdateProgress(task.Id, progress, task.Label);
            }
        }
    }

    public string GetParentPath(string path)
        => Resolve(path).GetParentPath(path);

    public string CombinePath(string directory, string name)
        => Resolve(directory).CombinePath(directory, name);

    public IReadOnlyList<string> GetVolumes()
        => _localService.GetVolumes();

    public Task DeletePermanentlyAsync(string path)
        => Resolve(path).DeletePermanentlyAsync(path);

    public Task EmptyTrashAsync()
        => _localService.EmptyTrashAsync();

    public Task ResolveAppIconsAsync(IEnumerable<FileSystemEntry> entries, Action? onBatchResolved = null, CancellationToken cancellationToken = default)
        => _localService.ResolveAppIconsAsync(entries, onBatchResolved, cancellationToken);

    public bool IsCrossVolume(string sourcePath, string destinationPath)
    {
        var srcIsRemote = VirtualPath.IsRemotePath(sourcePath);
        var dstIsRemote = VirtualPath.IsRemotePath(destinationPath);
        if (srcIsRemote != dstIsRemote) return true;
        if (srcIsRemote) return false;
        return _localService.IsCrossVolume(sourcePath, destinationPath);
    }

    public async Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory, IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default)
    {
        var srcIsRemote = VirtualPath.IsRemotePath(sourcePaths.FirstOrDefault());
        var dstIsRemote = VirtualPath.IsRemotePath(destinationDirectory);

        if (srcIsRemote == dstIsRemote)
        {
            await Resolve(sourcePaths.FirstOrDefault()).MoveWithProgressAsync(sourcePaths, destinationDirectory, progress, ct);
            return;
        }

        // Cross-service move with progress
        foreach (var src in sourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            await CopyAsync(src, destinationDirectory);
            await DeleteAsync(src, moveToTrash: false);
        }
    }
}
