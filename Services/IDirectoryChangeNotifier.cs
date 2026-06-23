using MacExplorer.ViewModels;

namespace MacExplorer.Services;

/// <summary>
/// Layer 2 of the unified refresh pipeline: Change Router.
/// All change sources (file operations, drag-drop, FSEvents) funnel through
/// NotifyChanged, which debounces and dispatches refresh to matching ViewModels.
/// </summary>
public interface IDirectoryChangeNotifier
{
    /// <summary>
    /// Notify that one or more directories have changed content.
    /// </summary>
    /// <param name="directoryPaths">Affected directory paths.</param>
    /// <param name="excludeVm">VM that already refreshed itself locally (to avoid double refresh). Null if none.</param>
    void NotifyChanged(string[] directoryPaths, FileListViewModel? excludeVm = null);

    /// <summary>
    /// Temporarily ignore file system refreshes for directories that are changed
    /// only by metadata writes, such as Finder tag xattrs.
    /// </summary>
    void SuppressRefresh(string[] directoryPaths, TimeSpan duration);

    /// <summary>Register a VM (call on window init).</summary>
    void Subscribe(FileListViewModel vm);

    /// <summary>Unregister a VM (call on window dispose).</summary>
    void Unsubscribe(FileListViewModel vm);
}
