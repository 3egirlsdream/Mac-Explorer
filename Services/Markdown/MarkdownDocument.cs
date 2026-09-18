using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MacExplorer.Services.Markdown;

/// <summary>A loaded file and fingerprint used for optimistic save conflict detection.</summary>
internal sealed class MarkdownDocument
{
    public const int MaxReadBytes = 4 * 1024 * 1024;
    private readonly byte[] _fingerprint;
    private readonly Encoding _encoding;
    private readonly string _writePath;

    private MarkdownDocument(string path, string writePath, string text, Encoding encoding, byte[] fingerprint)
    {
        FilePath = path;
        _writePath = writePath;
        Text = text;
        _encoding = encoding;
        _fingerprint = fingerprint;
        NewLine = DetectNewLine(text);
    }

    public string FilePath { get; }
    public string Text { get; }
    public string NewLine { get; }
    public string EncodingName => _encoding.WebName + (_encoding.GetPreamble().Length > 0 ? " BOM" : string.Empty);

    // Intentionally .md only: folders, .mdx and arbitrary text files retain their current actions.
    public static bool IsMarkdown(string? path) =>
        string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase);

    public static Task<MarkdownDocument> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || !IsMarkdown(path))
            throw new ArgumentException("请选择本地 .md 文件。", nameof(path));
        return Task.Run(async () =>
        {
            var fullPath = Path.GetFullPath(path);
            var writePath = ResolveWritePath(fullPath);
            var bytes = await ReadBoundedAsync(writePath, cancellationToken).ConfigureAwait(false);
            var (text, encoding) = Decode(bytes);
            return new MarkdownDocument(fullPath, writePath, text, encoding, SHA256.HashData(bytes));
        }, cancellationToken);
    }

    public static Task<string> ReadPreviewAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(async () => Decode(await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false)).Text,
            cancellationToken);

    public Task<MarkdownDocument> SaveAsync(string text, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(FilePath, _writePath, text, _fingerprint, cancellationToken);

    /// <summary>The save picker must have obtained overwrite consent before this method is called.</summary>
    public Task<MarkdownDocument> SaveCopyAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path) || !IsMarkdown(path))
            throw new ArgumentException("另存为的文件名必须以 .md 结尾。", nameof(path));
        return Task.Run(async () =>
        {
            var fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, FilePath, StringComparison.Ordinal))
                return await SaveAsync(text, cancellationToken).ConfigureAwait(false);
            var writePath = ResolveWritePath(fullPath);
            // A symlink chosen in the save picker may point back to the original file.
            if (string.Equals(writePath, _writePath, StringComparison.Ordinal))
                return await SaveAsync(text, cancellationToken).ConfigureAwait(false);
            var expected = File.Exists(writePath)
                ? await FingerprintAsync(writePath, cancellationToken).ConfigureAwait(false)
                : null;
            return await SaveCoreAsync(fullPath, writePath, text, expected, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task<MarkdownDocument> SaveCoreAsync(string displayPath, string writePath, string text,
        byte[]? expectedFingerprint, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var directory = Path.GetDirectoryName(writePath)!;
        var tempPath = Path.Combine(directory, $".mac-explorer-md-{Guid.NewGuid():N}.fkfinder-tmp");
        var committed = false;
        try
        {
            await VerifyRevisionAsync(displayPath, writePath, expectedFingerprint, cancellationToken).ConfigureAwait(false);
            if (File.Exists(writePath) && (File.GetAttributes(writePath) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("文件为只读，请使用“另存为”。");

            var preamble = _encoding.GetPreamble();
            var payload = _encoding.GetBytes(text);
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await output.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(writePath))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(tempPath, File.GetUnixFileMode(writePath));
                // Preserve Finder tags and ACLs; copying data would undo the user's edits.
                if (OperatingSystem.IsMacOS() && CopyFileMetadata(writePath, tempPath, IntPtr.Zero, 0x0001 | 0x0004) != 0)
                    throw new IOException("无法保留文件的标签或访问权限，请使用“另存为”。",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            // Recheck after staging to detect ordinary external edits. This is optimistic
            // conflict detection, not an atomic compare-and-swap with other applications.
            await VerifyRevisionAsync(displayPath, writePath, expectedFingerprint, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, writePath, overwrite: expectedFingerprint != null);
            committed = true;
            // A cancellation after the rename must not turn a successful save into a reported failure.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(preamble);
            hash.AppendData(payload);
            return new MarkdownDocument(displayPath, writePath, text, _encoding, hash.GetHashAndReset());
        }
        finally
        {
            if (!committed)
            {
                try { File.Delete(tempPath); }
                catch (IOException) { /* Preserve the original write/permission failure. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }, cancellationToken);

    private static async Task VerifyRevisionAsync(string displayPath, string writePath, byte[]? expected,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(ResolveWritePath(displayPath), writePath, StringComparison.Ordinal))
            throw new MarkdownFileChangedException();
        if (expected == null)
        {
            if (File.Exists(writePath)) throw new MarkdownFileChangedException();
            return;
        }
        try
        {
            var actual = await FingerprintAsync(writePath, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new MarkdownFileChangedException();
        }
        catch (FileNotFoundException) { throw new MarkdownFileChangedException(); }
        catch (DirectoryNotFoundException) { throw new MarkdownFileChangedException(); }
    }

    private static string ResolveWritePath(string path)
    {
        var info = new FileInfo(path);
        // Atomic replacement must replace the destination, not destroy the symbolic link.
        return info.LinkTarget == null ? info.FullName
            : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("符号链接的目标不存在。");
    }

    private static async Task<byte[]> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxReadBytes) throw new IOException("仅支持打开 4 MB 以内的 Markdown 文件。");
        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0,
                   (int)Math.Min(buffer.Length, MaxReadBytes + 1L - output.Length)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaxReadBytes) throw new IOException("文件读取过程中超过了 4 MB 限制。");
        }
        return output.ToArray();
    }

    internal static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        Encoding encoding;
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
        { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
        { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        { encoding = new UTF8Encoding(true, true); skip = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else encoding = new UTF8Encoding(false, true);
        try
        {
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            if (text.Contains('\0')) throw new IOException("文件包含二进制内容，不能作为 Markdown 编辑。");
            return (text, encoding);
        }
        catch (DecoderFallbackException ex)
        {
            throw new IOException("无法无损解码此文件。支持 UTF-8，以及带 BOM 的 UTF-16/UTF-32；请先转换编码。", ex);
        }
    }

    internal static string DetectNewLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        if (index < 0) return "\n";
        return text[index] == '\r' ? (index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r") : "\n";
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "copyfile", SetLastError = true)]
    private static extern int CopyFileMetadata([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, IntPtr state, uint flags);
}

internal sealed class MarkdownFileChangedException : IOException
{
    public MarkdownFileChangedException() : base("磁盘文件已被其他程序修改、移动或删除。本次未覆盖原文件；请另存为保留当前编辑。") { }
}
