using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IFileService
{
    Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default);
    IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(
        string path, int batchSize = 256, CancellationToken cancellationToken = default);
    Task<FileSystemEntry?> GetEntryAsync(string path);
    Task<bool> ExistsAsync(string path);
    Task<string> CreateFolderAsync(string parentPath, string name);
    Task<string> CreateFileAsync(string parentPath, string name);
    Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content);
    Task DeleteAsync(string path, bool moveToTrash = true);
    Task RenameAsync(string path, string newName);
    Task MoveAsync(string sourcePath, string destinationDirectory, bool overwrite = false);
    Task CopyAsync(string sourcePath, string destinationDirectory);
    string GetParentPath(string path);
    string CombinePath(string directory, string name);
    IReadOnlyList<string> GetVolumes();
    string HomeDirectory { get; }
    string RootDirectory { get; }
    string TrashDirectory { get; }
    Task DeletePermanentlyAsync(string path);
    Task EmptyTrashAsync();
    Task ResolveAppIconsAsync(
        IEnumerable<FileSystemEntry> entries,
        Action? onBatchResolved = null,
        CancellationToken cancellationToken = default);
    bool IsCrossVolume(string sourcePath, string destinationPath);
    Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default);
}
