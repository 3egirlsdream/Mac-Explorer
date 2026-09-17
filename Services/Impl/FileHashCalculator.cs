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
        token.ThrowIfCancellationRequested();
        var before = new FileInfo(path);
        if (!before.Exists) throw new FileNotFoundException("文件已不存在。", path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        if (length > byteLimit) return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var after = new FileInfo(path);
        // Detect ordinary edits/replacements. This is not a filesystem snapshot or
        // a guarantee against a writer deliberately preserving size and timestamps.
        if (!after.Exists || stream.Length != length || after.Length != length || after.LastWriteTimeUtc != modified)
            throw new IOException("计算期间文件发生变化，请重新计算。");
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
