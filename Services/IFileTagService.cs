using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IFileTagService
{
    event EventHandler? TagsChanged;
    event EventHandler<TagRenamedEventArgs>? TagRenamed;

    Task<FileTag> CreateTagAsync(string name, CancellationToken cancellationToken = default);
    Task RenameTagAsync(FileTag tag, string newName, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(FileTag tag, CancellationToken cancellationToken = default);
    Task SetTagColorAsync(FileTag tag, int colorId, CancellationToken cancellationToken = default);
    Task SetTagPinnedAsync(FileTag tag, bool pinned, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileTag>> GetFileTagsAsync(string filePath, CancellationToken cancellationToken = default);
    Task<TagSyncResult> SetTagAsync(IReadOnlyList<string> filePaths, FileTag tag, bool applied, CancellationToken cancellationToken = default);
    Task<TagSyncResult> RetryPendingAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileTag>> GetSidebarTagsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> FindFilePathsAsync(FileTag tag, CancellationToken cancellationToken = default);
    Task ReplaceFileTagsAsync(string filePath, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);
    Task UpdatePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);
    Task CopyPathAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task DeletePathAsync(string path, CancellationToken cancellationToken = default);
}

public interface IFinderTagQueryService
{
    Task<IReadOnlyList<string>> FindFilePathsAsync(FileTag tag, CancellationToken cancellationToken = default);
}

public sealed record TagSyncResult(int SyncedFiles, int PendingFiles);
public sealed class TagRenamedEventArgs(string oldName, string? newName) : EventArgs
{
    public string OldName { get; } = oldName;
    public string? NewName { get; } = newName;
}

public sealed record NativeFileTag(string Name, int ColorId = 0);

public interface IFileTagStore
{
    Task<IReadOnlyList<NativeFileTag>> ReadAsync(string filePath, CancellationToken cancellationToken = default);
    Task WriteAsync(string filePath, IReadOnlyList<NativeFileTag> tags, CancellationToken cancellationToken = default);
}
