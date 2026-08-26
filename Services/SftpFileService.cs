using System.Runtime.CompilerServices;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace MacExplorer.Services;

public class SftpFileService : IRemoteFileService, IDisposable
{
    private readonly IRemoteConnectionService _connectionService;
    private readonly ILogger<SftpFileService>? _logger;
    private readonly string _tempDir;
    private string? _currentServerId;

    public SftpFileService(IRemoteConnectionService connectionService, ILogger<SftpFileService>? logger = null)
    {
        _connectionService = connectionService;
        _logger = logger;
        _tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MacExplorer", "remote-temp");
        if (!Directory.Exists(_tempDir))
            Directory.CreateDirectory(_tempDir);
    }

    public void SetCurrentServer(string serverId) => _currentServerId = serverId;

    private SftpClient GetClient()
        => GetClient(_currentServerId ?? throw new InvalidOperationException("No remote server selected"));

    private SftpClient GetClient(string serverId)
        => _connectionService.GetSftpClient(serverId)
           ?? throw new InvalidOperationException($"Server {serverId} is not connected");

    private string GetServerId() => _currentServerId ?? throw new InvalidOperationException("No remote server selected");

    private static string ToRemotePath(string path) => RemotePathHelper.ToRemotePath(path);

    public async Task<Stream> OpenReadAsync(string serverId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = GetClient(serverId);
        var path = ToRemotePath(remotePath);
        return await Task.Run(() => (Stream)client.OpenRead(path), cancellationToken);
    }

    public async Task<Stream> OpenWriteAsync(string serverId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = GetClient(serverId);
        var path = ToRemotePath(remotePath);
        return await Task.Run(() => (Stream)client.OpenWrite(path), cancellationToken);
    }

    public async Task<long> GetFileSizeAsync(string serverId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = GetClient(serverId);
        var path = ToRemotePath(remotePath);
        return await Task.Run(() =>
        {
            try
            {
                return client.GetAttributes(path)?.Size ?? 0;
            }
            catch
            {
                return 0;
            }
        }, cancellationToken);
    }

    public string HomeDirectory => "/";
    public string RootDirectory => "/";
    public string TrashDirectory => "__remote_trash__";

    public async Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
    {
        var entries = new List<FileSystemEntry>();
        await foreach (var batch in EnumerateDirectoryBatchesAsync(path, 256, cancellationToken))
            entries.AddRange(batch);
        return entries;
    }

    public async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(
        string path, int batchSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 32, 1024);
        var client = GetClient();
        var serverId = GetServerId();
        var remotePath = ToRemotePath(path);

        var batch = new List<FileSystemEntry>(batchSize);
        await foreach (var item in client.ListDirectoryAsync(remotePath, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Name == "." || item.Name == "..") continue;
            batch.Add(CreateEntry(item, remotePath, serverId));
            if (batch.Count < batchSize) continue;
            yield return batch.ToArray();
            batch.Clear();
        }

        if (batch.Count > 0)
            yield return batch.ToArray();
    }

    public async Task<FileSystemEntry?> GetEntryAsync(string path)
    {
        var client = GetClient();
        var serverId = GetServerId();
        var remotePath = VirtualPath.IsRemotePath(path) ? VirtualPath.ParseRemotePath(path).RemotePath : path;
        return await Task.Run(() =>
        {
            try
            {
                var attrs = client.GetAttributes(remotePath);
                return CreateEntryFromAttrs(Path.GetFileName(remotePath), attrs, path);
            }
            catch
            {
                return null;
            }
        });
    }

    public async Task<bool> ExistsAsync(string path)
    {
        var client = GetClient();
        var remotePath = ToRemotePath(path);
        return await Task.Run(() => client.Exists(remotePath));
    }

    public async Task<string> CreateFolderAsync(string parentPath, string name)
    {
        var client = GetClient();
        var serverId = GetServerId();
        return await Task.Run(() =>
        {
            var remoteParent = ToRemotePath(parentPath);
            var fullPath = CombinePath(remoteParent, name);
            client.CreateDirectory(fullPath);
            return VirtualPath.BuildRemotePath(serverId, fullPath);
        });
    }

    public async Task<string> CreateFileAsync(string parentPath, string name)
    {
        var client = GetClient();
        var serverId = GetServerId();
        return await Task.Run(() =>
        {
            var remoteParent = ToRemotePath(parentPath);
            var fullPath = CombinePath(remoteParent, name);
            using var stream = client.OpenWrite(fullPath);
            return VirtualPath.BuildRemotePath(serverId, fullPath);
        });
    }

    public async Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content)
    {
        var client = GetClient();
        var serverId = GetServerId();
        return await Task.Run(() =>
        {
            var remoteParent = ToRemotePath(parentPath);
            var fullPath = CombinePath(remoteParent, name);
            using var stream = client.OpenWrite(fullPath);
            stream.Write(content, 0, content.Length);
            return VirtualPath.BuildRemotePath(serverId, fullPath);
        });
    }

    public async Task DeleteAsync(string path, bool moveToTrash = true)
    {
        var client = GetClient();
        var remotePath = ToRemotePath(path);
        await Task.Run(() =>
        {
            var attrs = client.GetAttributes(remotePath);
            if (attrs.IsDirectory && !attrs.IsSymbolicLink)
                DeleteDirectoryTree(client, remotePath);
            else
                client.DeleteFile(remotePath);
        });
    }

    internal readonly record struct RemoteDeleteEntry(
        string Name,
        bool IsDirectory,
        bool IsSymbolicLink);

    private static void DeleteDirectoryTree(SftpClient client, string rootPath)
        => DeleteDirectoryTree(
            rootPath,
            directory => client.ListDirectory(directory)
                .Where(item => item.Name is not "." and not "..")
                .Select(item => new RemoteDeleteEntry(
                    item.Name,
                    item.IsDirectory,
                    item.IsSymbolicLink))
                .ToArray(),
            client.DeleteFile,
            client.DeleteDirectory);

    internal static void DeleteDirectoryTree(
        string rootPath,
        Func<string, IReadOnlyList<RemoteDeleteEntry>> listDirectory,
        Action<string> deleteFile,
        Action<string> deleteDirectory)
    {
        var normalizedRoot = rootPath.TrimEnd('/');
        if (normalizedRoot.Length == 0)
            throw new InvalidOperationException("Cannot delete the remote root directory");

        foreach (var entry in listDirectory(normalizedRoot))
        {
            var childPath = normalizedRoot + "/" + entry.Name;
            if (entry.IsDirectory && !entry.IsSymbolicLink)
                DeleteDirectoryTree(childPath, listDirectory, deleteFile, deleteDirectory);
            else
                deleteFile(childPath);
        }

        deleteDirectory(normalizedRoot);
    }

    public async Task RenameAsync(string path, string newName)
    {
        var client = GetClient();
        var remotePath = ToRemotePath(path);
        await Task.Run(() =>
        {
            var parentPath = GetParentPath(remotePath);
            var newPath = CombinePath(parentPath, newName);
            client.RenameFile(remotePath, newPath);
        });
    }

    public async Task MoveAsync(string sourcePath, string destinationDirectory, bool overwrite = false)
    {
        var client = GetClient();
        var remoteSrc = ToRemotePath(sourcePath);
        var remoteDest = ToRemotePath(destinationDirectory);
        await Task.Run(() =>
        {
            var name = Path.GetFileName(remoteSrc);
            var destPath = CombinePath(remoteDest, name);
            if (!overwrite && client.Exists(destPath))
                throw new IOException($"File already exists: {destPath}");
            client.RenameFile(remoteSrc, destPath);
        });
    }

    public async Task CopyAsync(string sourcePath, string destinationDirectory)
    {
        var client = GetClient();
        var remoteSrc = ToRemotePath(sourcePath);
        var remoteDest = ToRemotePath(destinationDirectory);
        await Task.Run(() =>
        {
            var name = Path.GetFileName(remoteSrc);
            var destPath = CombinePath(remoteDest, name);
            var attrs = client.GetAttributes(remoteSrc);
            if (attrs.IsDirectory)
                CopyDirectory(client, remoteSrc, destPath);
            else
                CopyFile(client, remoteSrc, destPath);
        });
    }

    private static void CopyFile(SftpClient client, string source, string dest)
    {
        using var srcStream = client.OpenRead(source);
        using var destStream = client.OpenWrite(dest);
        srcStream.CopyTo(destStream);
    }

    private static void CopyDirectory(SftpClient client, string source, string dest)
    {
        client.CreateDirectory(dest);
        foreach (var item in client.ListDirectory(source))
        {
            if (item.Name == "." || item.Name == "..") continue;
            var srcPath = source + "/" + item.Name;
            var destPath = dest + "/" + item.Name;
            if (item.IsDirectory)
                CopyDirectory(client, srcPath, destPath);
            else
                CopyFile(client, srcPath, destPath);
        }
    }

    public string GetParentPath(string path) => RemotePathHelper.GetParentPath(path);

    public string CombinePath(string directory, string name) => RemotePathHelper.CombinePath(directory, name);

    public IReadOnlyList<string> GetVolumes() => Array.Empty<string>();

    public Task DeletePermanentlyAsync(string path) => DeleteAsync(path, moveToTrash: false);

    public Task EmptyTrashAsync() => Task.CompletedTask;

    public Task ResolveAppIconsAsync(
        IEnumerable<FileSystemEntry> entries,
        Action? onBatchResolved = null,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public bool IsCrossVolume(string sourcePath, string destinationPath) => false;

    public async Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default)
    {
        var client = GetClient();
        var remoteDestDir = ToRemotePath(destinationDirectory);
        await Task.Run(() =>
        {
            long totalBytes = 0;
            var remoteSrcPaths = new List<string>();
            foreach (var src in sourcePaths)
            {
                ct.ThrowIfCancellationRequested();
                var remoteSrc = ToRemotePath(src);
                remoteSrcPaths.Add(remoteSrc);
                totalBytes += CalculateRemoteSize(client, remoteSrc);
            }
            if (totalBytes == 0) totalBytes = 1;

            long bytesCopied = 0;
            for (int i = 0; i < remoteSrcPaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var remoteSrc = remoteSrcPaths[i];
                var name = Path.GetFileName(remoteSrc);
                var destPath = CombinePath(remoteDestDir, name);
                var attrs = client.GetAttributes(remoteSrc);
                if (attrs.IsDirectory && !attrs.IsSymbolicLink)
                    CopyDirectoryWithProgress(client, remoteSrc, destPath, ref bytesCopied, totalBytes, progress, ct);
                else
                    CopyFileWithProgress(client, remoteSrc, destPath, ref bytesCopied, totalBytes, progress, ct);
            }

            foreach (var remoteSrc in remoteSrcPaths)
            {
                ct.ThrowIfCancellationRequested();
                var attrs = client.GetAttributes(remoteSrc);
                if (attrs.IsDirectory)
                    DeleteDirectoryTree(client, remoteSrc);
                else
                    client.DeleteFile(remoteSrc);
            }
        }, ct);
    }

    private static long CalculateRemoteSize(SftpClient client, string path)
    {
        var attrs = client.GetAttributes(path);
        if (!attrs.IsDirectory) return attrs.Size;
        long total = 0;
        foreach (var item in client.ListDirectory(path))
        {
            if (item.Name == "." || item.Name == "..") continue;
            total += item.IsDirectory ? CalculateRemoteSize(client, path + "/" + item.Name) : item.Length;
        }
        return total;
    }

    private static void CopyFileWithProgress(SftpClient client, string src, string dest,
        ref long bytesCopied, long totalBytes, IProgress<FileOperationProgress>? progress, CancellationToken ct)
    {
        using var srcStream = client.OpenRead(src);
        using var destStream = client.OpenWrite(dest);
        var buffer = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = srcStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destStream.Write(buffer, 0, bytesRead);
            bytesCopied += bytesRead;
            progress?.Report(new FileOperationProgress
            {
                Percentage = (double)bytesCopied / totalBytes * 100,
                CurrentFile = Path.GetFileName(src)
            });
        }
    }

    private static void CopyDirectoryWithProgress(SftpClient client, string src, string dest,
        ref long bytesCopied, long totalBytes, IProgress<FileOperationProgress>? progress, CancellationToken ct)
    {
        client.CreateDirectory(dest);
        foreach (var item in client.ListDirectory(src))
        {
            if (item.Name == "." || item.Name == "..") continue;
            var srcPath = src + "/" + item.Name;
            var destPath = dest + "/" + item.Name;
            ct.ThrowIfCancellationRequested();
            if (item.IsDirectory)
                CopyDirectoryWithProgress(client, srcPath, destPath, ref bytesCopied, totalBytes, progress, ct);
            else
                CopyFileWithProgress(client, srcPath, destPath, ref bytesCopied, totalBytes, progress, ct);
        }
    }

    private FileSystemEntry CreateEntry(ISftpFile item, string parentPath, string serverId)
    {
        var remotePath = parentPath.TrimEnd('/') + "/" + item.Name;
        var sentinelPath = VirtualPath.BuildRemotePath(serverId, remotePath);
        return CreateEntryFromAttrs(item.Name, item.Attributes, sentinelPath, isDirectory: item.IsDirectory);
    }

    private static FileSystemEntry CreateEntryFromAttrs(string name, SftpFileAttributes attrs, string fullPath, bool? isDirectory = null)
    {
        var isDir = isDirectory ?? attrs.IsDirectory;
        var ext = isDir ? "" : Path.GetExtension(name).ToLowerInvariant();

        return new FileSystemEntry
        {
            FullPath = fullPath,
            Name = name,
            IsDirectory = isDir,
            Size = isDir ? 0 : attrs.Size,
            LastModified = attrs.LastWriteTime,
            Created = attrs.LastAccessTime,
            Extension = ext,
            IsHidden = name.StartsWith('.'),
            IsSymbolicLink = attrs.IsSymbolicLink,
            IsReadable = attrs.OwnerCanRead,
            IsWritable = attrs.OwnerCanWrite,
            IconKey = isDir ? "folder" : FileIconResolver.ResolveIconKey(ext)
        };
    }

    public void Dispose()
    {
        // Cleanup temp directory
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }
}
