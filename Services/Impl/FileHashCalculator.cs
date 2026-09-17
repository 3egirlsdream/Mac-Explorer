using System.Buffers;
using System.Security.Cryptography;

namespace MacExplorer.Services.Impl;

internal static class FileHashCalculator
{
    // A UI policy, not a limit on files the user may explicitly choose to hash.
    internal const long AutomaticByteLimit = 64L * 1024 * 1024;

    /// <summary>Returns null when an automatic request exceeds its byte budget.</summary>
    internal static async Task<string?> ComputeAsync(string path, CancellationToken token = default,
        long byteLimit = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);
        token.ThrowIfCancellationRequested();
        var before = new FileInfo(path);
        if (!before.Exists) throw new FileNotFoundException("文件已不存在。", path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        if (length > byteLimit) return null;

        // ComputeStreamAsync owns buffering; do not prefetch a second buffer past its budget.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        // The file can grow between the metadata check and a read. Enforce the
        // automatic budget on actual I/O, not only on the initial file size.
        var hash = await ComputeStreamAsync(stream, byteLimit, token).ConfigureAwait(false);
        if (hash == null) return null;
        token.ThrowIfCancellationRequested();
        var after = new FileInfo(path);
        // Detect ordinary edits/replacements. This is not a filesystem snapshot or
        // a guarantee against a writer deliberately preserving size and timestamps.
        if (!after.Exists || stream.Length != length || after.Length != length || after.LastWriteTimeUtc != modified)
            throw new IOException("计算期间文件发生变化，请重新计算。");
        return hash;
    }

    internal static async Task<string?> ComputeStreamAsync(Stream stream, long byteLimit,
        CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);
        token.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long read = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = byteLimit - read;
                // One look-ahead byte distinguishes EOF at the limit from a growing
                // or non-seekable stream. Never request another full buffer past it.
                var count = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
                var received = await stream.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (received == 0) return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (received > remaining) return null;
                hash.AppendData(buffer, 0, received);
                read += received;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
