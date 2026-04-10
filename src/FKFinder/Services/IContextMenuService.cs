using FKFinder.Models;

namespace FKFinder.Services;

public interface IContextMenuService
{
    Task<IReadOnlyList<ContextMenuAction>> GetFileContextMenuActionsAsync(FileSystemEntry entry);
    Task<IReadOnlyList<ContextMenuAction>> GetBackgroundContextMenuActionsAsync(string currentDirectory);
    Task<IReadOnlyList<RegisteredApp>> GetApplicationsForFileAsync(string filePath);
    bool IsAppInstalled(string bundleIdentifier);
}
