using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IArchiveService
{
    bool IsArchiveFile(string filePath);

    Task<IReadOnlyList<FileSystemEntry>> GetArchiveContentsAsync(
        string archivePath, string internalPath = "");

    Task ExtractAsync(
        string archivePath, string destinationPath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default);

    Task<string> ExtractEntryToTempAsync(
        string archivePath, string entryKey);

    /// <returns>The actual output file path (may differ from the requested name due to deduplication).</returns>
    Task<string> CompressAsync(
        CompressOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default);
}
