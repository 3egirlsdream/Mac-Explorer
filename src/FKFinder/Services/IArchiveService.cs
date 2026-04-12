using FKFinder.Models;

namespace FKFinder.Services;

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

    Task CompressAsync(
        CompressOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default);
}
