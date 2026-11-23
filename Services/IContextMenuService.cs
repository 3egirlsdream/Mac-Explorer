using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IContextMenuService
{
    Task<IReadOnlyList<ContextMenuAction>> GetFileContextMenuActionsAsync(FileSystemEntry entry);
    Task<IReadOnlyList<ContextMenuAction>> GetBackgroundContextMenuActionsAsync(string currentDirectory);
    Task<IReadOnlyList<ContextMenuAction>> GetTrashFileContextMenuActionsAsync(FileSystemEntry entry);
    Task<IReadOnlyList<ContextMenuAction>> GetTrashBackgroundContextMenuActionsAsync();
    Task<IReadOnlyList<RegisteredApp>> GetApplicationsForFileAsync(string filePath);
    Task<ContextMenuIconGroup> GetDefaultApplicationIconsAsync(IReadOnlyList<string> filePaths);
    Task<IReadOnlyList<ContextMenuAction>> GetOpenWithActionsAsync(string filePath);
    Task<IReadOnlyList<ContextMenuAction>> GetOpenWithActionsAsync(string filePath, IReadOnlyList<string>? allPaths)
        => GetOpenWithActionsAsync(filePath);
    Task<IReadOnlyList<ContextMenuAction>> GetTopLevelOpenWithActionsAsync(string path);
    Task<IReadOnlyList<ContextMenuAction>> GetTopLevelOpenWithActionsAsync(string path, IReadOnlyList<string>? allPaths)
        => GetTopLevelOpenWithActionsAsync(path);
    bool IsAppInstalled(string bundleIdentifier);
}
