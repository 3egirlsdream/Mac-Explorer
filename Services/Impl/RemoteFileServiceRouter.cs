using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

/// <summary>
/// Dispatches remote file operations to the backend that owns the server, so the
/// rest of the app keeps depending on a single <see cref="IRemoteFileService"/>.
/// The server is taken from the path's <see cref="VirtualPath.RemotePrefix"/>
/// sentinel when it has one, and from the current server otherwise.
/// </summary>
public class RemoteFileServiceRouter : IRemoteFileService
{
    private readonly IRemoteConnectionService _connectionService;
    private readonly SftpFileService _sftp;
    private readonly OssFileService _oss;
    private string? _currentServerId;

    public RemoteFileServiceRouter(
        IRemoteConnectionService connectionService,
        SftpFileService sftp,
        OssFileService oss)
    {
        _connectionService = connectionService;
        _sftp = sftp;
        _oss = oss;
    }

    public void SetCurrentServer(string serverId)
    {
        _currentServerId = serverId;
        // Both backends track it so path-less members (HomeDirectory, ...) stay consistent.
        _sftp.SetCurrentServer(serverId);
        _oss.SetCurrentServer(serverId);
    }

    private IRemoteFileService ForServer(string? serverId)
    {
        if (serverId == null) return _sftp;
        var server = _connectionService.GetServer(serverId);
        return server?.Protocol == RemoteProtocol.AliyunOss ? _oss : _sftp;
    }

    private IRemoteFileService ForPath(string? path)
        => ForServer(VirtualPath.IsRemotePath(path) ? VirtualPath.ParseRemotePath(path!).ServerId : _currentServerId);

    private IRemoteFileService Current => ForServer(_currentServerId);

    public string HomeDirectory => Current.HomeDirectory;
    public string RootDirectory => Current.RootDirectory;
    public string TrashDirectory => Current.TrashDirectory;

    public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
        => ForPath(path).GetDirectoryContentsAsync(path, cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(string path, int batchSize = 256, CancellationToken cancellationToken = default)
        => ForPath(path).EnumerateDirectoryBatchesAsync(path, batchSize, cancellationToken);

    public Task<FileSystemEntry?> GetEntryAsync(string path) => ForPath(path).GetEntryAsync(path);

    public Task<bool> ExistsAsync(string path) => ForPath(path).ExistsAsync(path);

    public Task<string> CreateFolderAsync(string parentPath, string name) => ForPath(parentPath).CreateFolderAsync(parentPath, name);

    public Task<string> CreateFileAsync(string parentPath, string name) => ForPath(parentPath).CreateFileAsync(parentPath, name);

    public Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content)
        => ForPath(parentPath).CreateFileWithContentAsync(parentPath, name, content);

    public Task DeleteAsync(string path, bool moveToTrash = true) => ForPath(path).DeleteAsync(path, moveToTrash);

    public Task RenameAsync(string path, string newName) => ForPath(path).RenameAsync(path, newName);

    public Task MoveAsync(string sourcePath, string destinationDirectory, bool overwrite = false)
        => ForPath(sourcePath).MoveAsync(sourcePath, destinationDirectory, overwrite);

    public Task CopyAsync(string sourcePath, string destinationDirectory)
        => ForPath(sourcePath).CopyAsync(sourcePath, destinationDirectory);

    public string GetParentPath(string path) => ForPath(path).GetParentPath(path);

    public string CombinePath(string directory, string name) => ForPath(directory).CombinePath(directory, name);

    public IReadOnlyList<string> GetVolumes() => Current.GetVolumes();

    public Task DeletePermanentlyAsync(string path) => ForPath(path).DeletePermanentlyAsync(path);

    public Task EmptyTrashAsync() => Current.EmptyTrashAsync();

    public Task ResolveAppIconsAsync(IEnumerable<FileSystemEntry> entries, Action? onBatchResolved = null, CancellationToken cancellationToken = default)
        => Current.ResolveAppIconsAsync(entries, onBatchResolved, cancellationToken);

    public bool IsCrossVolume(string sourcePath, string destinationPath) => ForPath(sourcePath).IsCrossVolume(sourcePath, destinationPath);

    public Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default)
        => ForPath(sourcePaths.FirstOrDefault()).MoveWithProgressAsync(sourcePaths, destinationDirectory, progress, ct);

    public Task<Stream> OpenReadAsync(string serverId, string remotePath, CancellationToken cancellationToken = default)
        => ForServer(serverId).OpenReadAsync(serverId, remotePath, cancellationToken);

    public Task<Stream> OpenWriteAsync(string serverId, string remotePath, CancellationToken cancellationToken = default)
        => ForServer(serverId).OpenWriteAsync(serverId, remotePath, cancellationToken);

    public Task<long> GetFileSizeAsync(string serverId, string remotePath, CancellationToken cancellationToken = default)
        => ForServer(serverId).GetFileSizeAsync(serverId, remotePath, cancellationToken);
}
