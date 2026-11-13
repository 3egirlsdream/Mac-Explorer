using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IPinnedFolderService
{
    event EventHandler? Changed;
    Task<IReadOnlyList<PinnedFolder>> GetAllAsync();
    Task<IReadOnlyList<PinnedFolderGroup>> GetGroupsAsync();
    Task<int> CreateGroupAsync(string name);
    Task RenameGroupAsync(int groupId, string name);
    Task DeleteGroupAsync(int groupId);
    Task PinAsync(string folderPath, string displayName, int groupId = 0);
    Task UnpinAsync(string folderPath, int groupId = 0);
    Task<bool> IsPinnedAsync(string folderPath, int groupId = 0);
    Task UpdateFolderPathAsync(string oldPath, string newPath, string newDisplayName);
    Task ReorderAsync(IReadOnlyList<string> orderedFolderPaths, int groupId = 0);
}
