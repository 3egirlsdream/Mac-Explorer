using FKFinder.Models;

namespace FKFinder.Services;

public interface IContextMenuService
{
    Task<IReadOnlyList<ContextMenuAction>> GetFileContextMenuActionsAsync(FileSystemEntry entry);
    Task<IReadOnlyList<ContextMenuAction>> GetBackgroundContextMenuActionsAsync(string currentDirectory);
    Task<IReadOnlyList<ContextMenuAction>> GetTrashFileContextMenuActionsAsync(FileSystemEntry entry);
    Task<IReadOnlyList<ContextMenuAction>> GetTrashBackgroundContextMenuActionsAsync();
    Task<IReadOnlyList<RegisteredApp>> GetApplicationsForFileAsync(string filePath);
    bool IsAppInstalled(string bundleIdentifier);
}
