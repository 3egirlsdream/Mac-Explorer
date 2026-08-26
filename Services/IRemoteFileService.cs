namespace MacExplorer.Services;

/// <summary>
/// A remote file backend (SFTP, Aliyun OSS, ...). Transfer is expressed as plain
/// streams so callers stay protocol-agnostic.
/// </summary>
public interface IRemoteFileService : IFileService
{
    void SetCurrentServer(string serverId);

    /// <summary>Opens a read-only stream over a remote file. The stream may be forward-only.</summary>
    Task<Stream> OpenReadAsync(string serverId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Opens a write stream; the remote file is committed when the stream is disposed.</summary>
    Task<Stream> OpenWriteAsync(string serverId, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Size in bytes, or 0 when it cannot be determined.</summary>
    Task<long> GetFileSizeAsync(string serverId, string remotePath, CancellationToken cancellationToken = default);
}
