using FKFinder.Models;

namespace FKFinder.Services;

public interface IPinnedFolderService
{
    Task<IReadOnlyList<PinnedFolder>> GetAllAsync();
    Task PinAsync(string folderPath, string displayName);
    Task UnpinAsync(string folderPath);
    Task<bool> IsPinnedAsync(string folderPath);
    Task UpdateFolderPathAsync(string oldPath, string newPath, string newDisplayName);
}
