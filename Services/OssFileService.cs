using System.Runtime.CompilerServices;
using Aliyun.OSS;
using Aliyun.OSS.Common;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services;

/// <summary>
/// Aliyun OSS backend. OSS is a flat key/value store, so "directories" are
/// synthesised from the common prefixes returned by a delimiter listing, plus the
/// zero-byte <c>foo/</c> placeholder objects the OSS console creates.
/// </summary>
public class OssFileService : IRemoteFileService, IDisposable
{
    /// <summary>OSS caps a single ListObjects page at 1000 keys.</summary>
    private const int MaxKeysPerPage = 1000;

    /// <summary>Server-side CopyObject is limited to 1 GB; larger objects need a multipart copy.</summary>
    private const long MaxSingleCopySize = 1024L * 1024 * 1024;

    private readonly IRemoteConnectionService _connectionService;
    private readonly ILogger<OssFileService>? _logger;
    private readonly string _tempDir;
    private readonly Dictionary<string, (string Signature, IOss Client)> _clients = new();
    private string? _currentServerId;

    public OssFileService(IRemoteConnectionService connectionService, ILogger<OssFileService>? logger = null)
    {
        _connectionService = connectionService;
        _logger = logger;
        _tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MacExplorer", "oss-temp");
        if (!Directory.Exists(_tempDir))
            Directory.CreateDirectory(_tempDir);
    }

    public void SetCurrentServer(string serverId) => _currentServerId = serverId;

    public string HomeDirectory => "/";
    public string RootDirectory => "/";
    public string TrashDirectory => "__remote_trash__";

    // ---------------------------------------------------------------- key mapping

    /// <summary>Sentinel or POSIX path to an OSS object key: "/a/b.txt" → "a/b.txt", "/" → "".</summary>
    internal static string ToObjectKey(string path)
    {
        var remotePath = RemotePathHelper.ToRemotePath(path).Replace('\\', '/');
        return remotePath.TrimStart('/');
    }

    /// <summary>Key to the prefix used to enumerate its children: "a/b" → "a/b/", "" → "".</summary>
    internal static string ToPrefix(string key)
        => key.Length == 0 ? string.Empty : key.TrimEnd('/') + "/";

    /// <summary>Last path segment of a key, with any trailing slash removed.</summary>
    internal static string KeyToName(string key)
    {
        var trimmed = key.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    private (IOss Client, string Bucket) GetContext(string serverId)
    {
        var server = _connectionService.GetServer(serverId)
            ?? throw new InvalidOperationException($"Server {serverId} is not connected");
        if (!server.IsOss)
            throw new InvalidOperationException($"Server {server.DisplayName} is not an OSS server");

        // Rebuild the client when credentials change so an edited server takes effect.
        var signature = $"{server.Endpoint}|{server.AccessKeyId}|{server.AccessKeySecret}|{server.Bucket}";
        lock (_clients)
        {
            if (_clients.TryGetValue(serverId, out var cached) && cached.Signature == signature)
                return (cached.Client, server.Bucket);

            var client = OssClientFactory.Create(server);
            _clients[serverId] = (signature, client);
            return (client, server.Bucket);
        }
    }

    private (IOss Client, string Bucket, string ServerId) GetContext()
    {
        var id = _currentServerId ?? throw new InvalidOperationException("No remote server selected");
        var (client, bucket) = GetContext(id);
        return (client, bucket, id);
    }

    /// <summary>Resolves the server from the path when it carries one, else the current server.</summary>
    private (IOss Client, string Bucket, string ServerId) GetContextForPath(string path)
    {
        if (!VirtualPath.IsRemotePath(path)) return GetContext();
        var serverId = VirtualPath.ParseRemotePath(path).ServerId;
        var (client, bucket) = GetContext(serverId);
        return (client, bucket, serverId);
    }

    // ---------------------------------------------------------------- listing

    public async Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
    {
        var entries = new List<FileSystemEntry>();
        await foreach (var batch in EnumerateDirectoryBatchesAsync(path, MaxKeysPerPage, cancellationToken))
            entries.AddRange(batch);
        return entries;
    }

    public async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(
        string path, int batchSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 32, 1024);
        var (client, bucket, serverId) = GetContextForPath(path);
        var prefix = ToPrefix(ToObjectKey(path));

        var batch = new List<FileSystemEntry>(batchSize);
        string? marker = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentMarker = marker;
            var listing = await Task.Run(() => client.ListObjects(new ListObjectsRequest(bucket)
            {
                Prefix = prefix,
                Delimiter = "/",
                Marker = currentMarker,
                MaxKeys = MaxKeysPerPage
            }), cancellationToken);

            foreach (var commonPrefix in listing.CommonPrefixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.Add(CreateDirectoryEntry(commonPrefix, serverId));
                if (batch.Count < batchSize) continue;
                yield return batch.ToArray();
                batch.Clear();
            }

            foreach (var summary in listing.ObjectSummaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The placeholder object standing for the directory itself.
                if (summary.Key == prefix) continue;
                batch.Add(CreateFileEntry(summary, serverId));
                if (batch.Count < batchSize) continue;
                yield return batch.ToArray();
                batch.Clear();
            }

            marker = listing.IsTruncated ? listing.NextMarker : null;
        }
        while (!string.IsNullOrEmpty(marker));

        if (batch.Count > 0)
            yield return batch.ToArray();
    }

    public async Task<FileSystemEntry?> GetEntryAsync(string path)
    {
        var (client, bucket, serverId) = GetContextForPath(path);
        var key = ToObjectKey(path);

        if (key.Length == 0)
        {
            return new FileSystemEntry
            {
                FullPath = VirtualPath.BuildRemotePath(serverId, "/"),
                Name = "/",
                IsDirectory = true,
                IconKey = "folder",
                IsReadable = true,
                IsWritable = true
            };
        }

        return await Task.Run(() =>
        {
            try
            {
                var metadata = client.GetObjectMetadata(bucket, key);
                return CreateEntryFromMetadata(KeyToName(key), key, metadata, serverId);
            }
            catch (OssException)
            {
                // Not an object — it may still be a prefix with children under it.
                return IsDirectoryKey(client, bucket, key)
                    ? CreateDirectoryEntry(ToPrefix(key), serverId)
                    : null;
            }
        });
    }

    public async Task<bool> ExistsAsync(string path)
    {
        var (client, bucket, _) = GetContextForPath(path);
        var key = ToObjectKey(path);
        if (key.Length == 0) return true;
        return await Task.Run(() => client.DoesObjectExist(bucket, key) || IsDirectoryKey(client, bucket, key));
    }

    private static bool IsDirectoryKey(IOss client, string bucket, string key)
    {
        var prefix = ToPrefix(key);
        if (prefix.Length == 0) return true;
        var listing = client.ListObjects(new ListObjectsRequest(bucket) { Prefix = prefix, MaxKeys = 1 });
        return listing.ObjectSummaries.Any();
    }

    // ---------------------------------------------------------------- create

    public async Task<string> CreateFolderAsync(string parentPath, string name)
    {
        var (client, bucket, serverId) = GetContextForPath(parentPath);
        var key = ToPrefix(ToObjectKey(parentPath)) + name.TrimEnd('/');
        return await Task.Run(() =>
        {
            // A zero-byte "foo/" object is how OSS represents an empty directory.
            using var empty = new MemoryStream();
            client.PutObject(bucket, key + "/", empty);
            return VirtualPath.BuildRemotePath(serverId, "/" + key);
        });
    }

    public Task<string> CreateFileAsync(string parentPath, string name)
        => CreateFileWithContentAsync(parentPath, name, []);

    public async Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content)
    {
        var (client, bucket, serverId) = GetContextForPath(parentPath);
        var key = ToPrefix(ToObjectKey(parentPath)) + name;
        return await Task.Run(() =>
        {
            using var stream = new MemoryStream(content);
            client.PutObject(bucket, key, stream);
            return VirtualPath.BuildRemotePath(serverId, "/" + key);
        });
    }

    // ---------------------------------------------------------------- delete

    public async Task DeleteAsync(string path, bool moveToTrash = true)
    {
        // OSS has no trash; deletes are always permanent.
        var (client, bucket, _) = GetContextForPath(path);
        var key = ToObjectKey(path);
        if (key.Length == 0)
            throw new InvalidOperationException("Cannot delete the bucket root");

        await Task.Run(() =>
        {
            var childKeys = ListAllKeys(client, bucket, ToPrefix(key)).ToList();
            if (childKeys.Count > 0)
            {
                DeleteKeys(client, bucket, childKeys);
                return;
            }
            client.DeleteObject(bucket, key);
        });
    }

    public Task DeletePermanentlyAsync(string path) => DeleteAsync(path, moveToTrash: false);

    public Task EmptyTrashAsync() => Task.CompletedTask;

    private static void DeleteKeys(IOss client, string bucket, IReadOnlyList<string> keys)
    {
        for (var i = 0; i < keys.Count; i += MaxKeysPerPage)
        {
            var page = keys.Skip(i).Take(MaxKeysPerPage).ToList();
            client.DeleteObjects(new DeleteObjectsRequest(bucket, page, quiet: true));
        }
    }

    /// <summary>Every key under a prefix, recursively (no delimiter), following pagination.</summary>
    private static IEnumerable<string> ListAllKeys(IOss client, string bucket, string prefix)
    {
        string? marker = null;
        do
        {
            var listing = client.ListObjects(new ListObjectsRequest(bucket)
            {
                Prefix = prefix,
                Marker = marker,
                MaxKeys = MaxKeysPerPage
            });

            foreach (var summary in listing.ObjectSummaries)
                yield return summary.Key;

            marker = listing.IsTruncated ? listing.NextMarker : null;
        }
        while (!string.IsNullOrEmpty(marker));
    }

    // ---------------------------------------------------------------- move / copy

    public Task RenameAsync(string path, string newName)
    {
        var parentPath = GetParentPath(path);
        var newPath = CombinePath(parentPath, newName);
        return TransferAsync(path, newPath, deleteSource: true);
    }

    public Task MoveAsync(string sourcePath, string destinationDirectory, bool overwrite = false)
    {
        var destPath = CombinePath(destinationDirectory, KeyToName(ToObjectKey(sourcePath)));
        return TransferAsync(sourcePath, destPath, deleteSource: true, overwrite: overwrite);
    }

    public Task CopyAsync(string sourcePath, string destinationDirectory)
    {
        var destPath = CombinePath(destinationDirectory, KeyToName(ToObjectKey(sourcePath)));
        return TransferAsync(sourcePath, destPath, deleteSource: false);
    }

    private async Task TransferAsync(string sourcePath, string destPath, bool deleteSource, bool overwrite = true)
    {
        var (client, bucket, _) = GetContextForPath(sourcePath);
        var sourceKey = ToObjectKey(sourcePath);
        var destKey = ToObjectKey(destPath);
        if (sourceKey.Length == 0)
            throw new InvalidOperationException("Cannot move the bucket root");
        if (string.Equals(sourceKey, destKey, StringComparison.Ordinal)) return;

        await Task.Run(() =>
        {
            var childKeys = ListAllKeys(client, bucket, ToPrefix(sourceKey)).ToList();
            if (childKeys.Count > 0)
            {
                var sourcePrefix = ToPrefix(sourceKey);
                var destPrefix = ToPrefix(destKey);
                foreach (var childKey in childKeys)
                {
                    var relative = childKey[sourcePrefix.Length..];
                    CopyKey(client, bucket, childKey, destPrefix + relative);
                }
                if (deleteSource)
                    DeleteKeys(client, bucket, childKeys);
                return;
            }

            if (!overwrite && client.DoesObjectExist(bucket, destKey))
                throw new IOException($"File already exists: {destKey}");

            CopyKey(client, bucket, sourceKey, destKey);
            if (deleteSource)
                client.DeleteObject(bucket, sourceKey);
        });
    }

    private static void CopyKey(IOss client, string bucket, string sourceKey, string destKey)
    {
        var request = new CopyObjectRequest(bucket, sourceKey, bucket, destKey);
        var size = TryGetSize(client, bucket, sourceKey);
        if (size > MaxSingleCopySize)
        {
            var checkpointDir = Path.Combine(Path.GetTempPath(), "MacExplorer", "oss-copy");
            Directory.CreateDirectory(checkpointDir);
            client.ResumableCopyObject(request, checkpointDir);
            return;
        }
        client.CopyObject(request);
    }

    private static long TryGetSize(IOss client, string bucket, string key)
    {
        try
        {
            return client.GetObjectMetadata(bucket, key).ContentLength;
        }
        catch (OssException)
        {
            return 0;
        }
    }

    public async Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default)
    {
        var (client, bucket, _) = GetContextForPath(destinationDirectory);
        var destPrefix = ToPrefix(ToObjectKey(destinationDirectory));

        await Task.Run(() =>
        {
            // Server-side copies report no byte progress, so weight by object size.
            var plan = new List<(string SourceKey, string DestKey, long Size)>();
            foreach (var sourcePath in sourcePaths)
            {
                ct.ThrowIfCancellationRequested();
                var sourceKey = ToObjectKey(sourcePath);
                if (sourceKey.Length == 0) continue;

                var childKeys = ListAllKeys(client, bucket, ToPrefix(sourceKey)).ToList();
                if (childKeys.Count > 0)
                {
                    var sourcePrefix = ToPrefix(sourceKey);
                    var targetPrefix = destPrefix + KeyToName(sourceKey) + "/";
                    foreach (var childKey in childKeys)
                        plan.Add((childKey, targetPrefix + childKey[sourcePrefix.Length..], TryGetSize(client, bucket, childKey)));
                }
                else
                {
                    plan.Add((sourceKey, destPrefix + KeyToName(sourceKey), TryGetSize(client, bucket, sourceKey)));
                }
            }

            var totalBytes = Math.Max(1, plan.Sum(item => item.Size));
            long movedBytes = 0;

            foreach (var (sourceKey, destKey, size) in plan)
            {
                ct.ThrowIfCancellationRequested();
                CopyKey(client, bucket, sourceKey, destKey);
                movedBytes += size;
                progress?.Report(new FileOperationProgress
                {
                    Percentage = (double)movedBytes / totalBytes * 100,
                    CurrentFile = KeyToName(sourceKey)
                });
            }

            if (plan.Count > 0)
                DeleteKeys(client, bucket, plan.Select(item => item.SourceKey).ToList());
        }, ct);
    }

    // ---------------------------------------------------------------- streams

    public async Task<Stream> OpenReadAsync(string serverId, string remotePath, CancellationToken cancellationToken = default)
    {
        var (client, bucket) = GetContext(serverId);
        var key = ToObjectKey(remotePath);
        return await Task.Run(() => client.GetObject(bucket, key).Content, cancellationToken);
    }

    public Task<Stream> OpenWriteAsync(string serverId, string remotePath, CancellationToken cancellationToken = default)
    {
        var (client, bucket) = GetContext(serverId);
        var key = ToObjectKey(remotePath);
        Stream stream = new OssUploadStream(client, bucket, key, _tempDir);
        return Task.FromResult(stream);
    }

    public async Task<long> GetFileSizeAsync(string serverId, string remotePath, CancellationToken cancellationToken = default)
    {
        var (client, bucket) = GetContext(serverId);
        var key = ToObjectKey(remotePath);
        return await Task.Run(() => TryGetSize(client, bucket, key), cancellationToken);
    }

    /// <summary>
    /// OSS uploads need the full payload up front, so writes are spooled to a temp
    /// file and committed with a single PutObject when the stream is disposed.
    /// </summary>
    private sealed class OssUploadStream : Stream
    {
        private readonly IOss _client;
        private readonly string _bucket;
        private readonly string _key;
        private readonly string _tempPath;
        private readonly FileStream _buffer;
        private bool _committed;

        public OssUploadStream(IOss client, string bucket, string key, string tempDir)
        {
            _client = client;
            _bucket = bucket;
            _key = key;
            _tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".upload");
            _buffer = new FileStream(_tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);
        public override void Flush() => _buffer.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!disposing || _committed)
            {
                base.Dispose(disposing);
                return;
            }

            _committed = true;
            try
            {
                _buffer.Flush();
                _buffer.Position = 0;
                _client.PutObject(_bucket, _key, _buffer);
            }
            finally
            {
                _buffer.Dispose();
                try { File.Delete(_tempPath); } catch { }
                base.Dispose(disposing);
            }
        }
    }

    // ---------------------------------------------------------------- misc

    public string GetParentPath(string path) => RemotePathHelper.GetParentPath(path);

    public string CombinePath(string directory, string name) => RemotePathHelper.CombinePath(directory, name);

    public IReadOnlyList<string> GetVolumes() => [];

    public Task ResolveAppIconsAsync(
        IEnumerable<FileSystemEntry> entries,
        Action? onBatchResolved = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public bool IsCrossVolume(string sourcePath, string destinationPath) => false;

    private static FileSystemEntry CreateDirectoryEntry(string prefix, string serverId)
    {
        var name = KeyToName(prefix);
        return new FileSystemEntry
        {
            FullPath = VirtualPath.BuildRemotePath(serverId, "/" + prefix.TrimEnd('/')),
            Name = name,
            IsDirectory = true,
            Size = 0,
            Extension = "",
            IsHidden = name.StartsWith('.'),
            IsReadable = true,
            IsWritable = true,
            IconKey = "folder"
        };
    }

    private static FileSystemEntry CreateFileEntry(OssObjectSummary summary, string serverId)
    {
        var name = KeyToName(summary.Key);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return new FileSystemEntry
        {
            FullPath = VirtualPath.BuildRemotePath(serverId, "/" + summary.Key),
            Name = name,
            IsDirectory = false,
            Size = summary.Size,
            LastModified = summary.LastModified.ToLocalTime(),
            Created = summary.LastModified.ToLocalTime(),
            Extension = ext,
            IsHidden = name.StartsWith('.'),
            IsReadable = true,
            IsWritable = true,
            IconKey = FileIconResolver.ResolveIconKey(ext)
        };
    }

    private static FileSystemEntry CreateEntryFromMetadata(string name, string key, ObjectMetadata metadata, string serverId)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return new FileSystemEntry
        {
            FullPath = VirtualPath.BuildRemotePath(serverId, "/" + key),
            Name = name,
            IsDirectory = false,
            Size = metadata.ContentLength,
            LastModified = metadata.LastModified.ToLocalTime(),
            Created = metadata.LastModified.ToLocalTime(),
            Extension = ext,
            IsHidden = name.StartsWith('.'),
            IsReadable = true,
            IsWritable = true,
            IconKey = FileIconResolver.ResolveIconKey(ext)
        };
    }

    public void Dispose()
    {
        lock (_clients) _clients.Clear();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }
}
