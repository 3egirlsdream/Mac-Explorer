using System.Runtime.CompilerServices;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace MacExplorer.Services;

public class SftpFileService : IFileService, IDisposable
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
    {
        var id = _currentServerId ?? throw new InvalidOperationException("No remote server selected");
        return _connectionService.GetClient(id) ?? throw new InvalidOperationException($"Server {id} is not connected");
    }

    private string GetServerId() => _currentServerId ?? throw new InvalidOperationException("No remote server selected");

    private string ToRemotePath(string path) => VirtualPath.IsRemotePath(path) ? VirtualPath.ParseRemotePath(path).RemotePath : path;

    public Renci.SshNet.SftpClient? GetConnectedClient()
    {
        var id = _currentServerId;
        if (id == null) return null;
        return _connectionService.GetClient(id);
    }

    public string HomeDirectory => "/";
    public string RootDirectory => "/";
    public string TrashDirectory => "__remote_trash__";

    public async Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        var serverId = GetServerId();
        var remotePath = ToRemotePath(path);
        return await Task.Run(() =>
        {
            var entries = new List<FileSystemEntry>();
            var items = client.ListDirectory(remotePath);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Name == "." || item.Name == "..") continue;
                entries.Add(CreateEntry(item, remotePath, serverId));
            }
            return (IReadOnlyList<FileSystemEntry>)entries;
        }, cancellationToken);
    }

    public async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(
        string path, int batchSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 32, 1024);
        var client = GetClient();
        var serverId = GetServerId();
        var remotePath = ToRemotePath(path);

        List<FileSystemEntry>? batch = null;
        await Task.Run(() =>
        {
            batch = new List<FileSystemEntry>(batchSize);
            var items = client.ListDirectory(remotePath);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Name == "." || item.Name == "..") continue;
                batch!.Add(CreateEntry(item, remotePath, serverId));
                if (batch.Count >= batchSize)
                {
                    // yield not supported in Task.Run, accumulate
                }
            }
        }, cancellationToken);

        if (batch != null && batch.Count > 0)
            yield return batch;
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
            if (attrs.IsDirectory)
                client.DeleteDirectory(remotePath);
            else
                client.DeleteFile(remotePath);
        });
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

    public string GetParentPath(string path)
    {
        if (VirtualPath.IsRemotePath(path))
        {
            var (serverId, remotePath) = VirtualPath.ParseRemotePath(path);
            if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
                return VirtualPath.BuildRemotePath(serverId, "/");
            var normalized = remotePath.TrimEnd('/');
            var lastSlash = normalized.LastIndexOf('/');
            var parentRemote = lastSlash <= 0 ? "/" : normalized[..lastSlash];
            return VirtualPath.BuildRemotePath(serverId, parentRemote);
        }
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var norm = path.TrimEnd('/');
        var ls = norm.LastIndexOf('/');
        return ls <= 0 ? "/" : norm[..ls];
    }

    public string CombinePath(string directory, string name)
    {
        if (VirtualPath.IsRemotePath(directory))
        {
            var (serverId, remotePath) = VirtualPath.ParseRemotePath(directory);
            var combined = CombinePath(remotePath, name);
            return VirtualPath.BuildRemotePath(serverId, combined);
        }
        if (string.IsNullOrEmpty(directory) || directory == "/")
            return "/" + name;
        return directory.TrimEnd('/') + "/" + name;
    }

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
                if (attrs.IsDirectory)
                    CopyDirectoryWithProgress(client, remoteSrc, destPath, ref bytesCopied, totalBytes, progress, ct);
                else
                    CopyFileWithProgress(client, remoteSrc, destPath, ref bytesCopied, totalBytes, progress, ct);
            }

            foreach (var remoteSrc in remoteSrcPaths)
            {
                ct.ThrowIfCancellationRequested();
                var attrs = client.GetAttributes(remoteSrc);
                if (attrs.IsDirectory)
                    client.DeleteDirectory(remoteSrc);
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
