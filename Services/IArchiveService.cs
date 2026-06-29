using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IArchiveService
{
    bool IsArchiveFile(string filePath);

    Task<IReadOnlyList<FileSystemEntry>> GetArchiveContentsAsync(
        string archivePath, string internalPath = "", string? password = null);

    Task ExtractAsync(
        string archivePath, string destinationPath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default,
        string? password = null);

    Task<string> ExtractEntryToTempAsync(
        string archivePath, string entryKey, string? password = null);

    /// <summary>检测压缩包是否加密（通过尝试读取第一个文件条目流）。</summary>
    bool IsEncrypted(string archivePath);

    /// <returns>The actual output file path (may differ from the requested name due to deduplication).</returns>
    Task<string> CompressAsync(
        CompressOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default);
}
