using MacExplorer.ViewModels;

namespace MacExplorer.Services;

/// <summary>
/// Bidirectional bridge between Blazor layer and native drag-and-drop.
/// Allows native UIDragInteraction/UIDropInteraction to query selected files
/// and notify the ViewModel when files are dropped from external sources.
/// </summary>
public interface IDragDropBridge
{
    /// <summary>Register a ViewModel instance (call on page init).</summary>
    void Register(FileListViewModel vm);

    /// <summary>Mark a ViewModel as the currently active one (call on window focus).</summary>
    void SetActive(FileListViewModel vm);

    /// <summary>Unregister a ViewModel instance (call on disposal).</summary>
    void Unregister(FileListViewModel vm);

    /// <summary>
    /// Called by native drag delegate to get file paths for the drag session.
    /// Returns the currently selected file paths from the active ViewModel.
    /// </summary>
    string[] GetDragFilePaths();

    /// <summary>
    /// Called by native drop delegate to get the current directory path.
    /// </summary>
    string GetCurrentDirectory();

    /// <summary>
    /// Called by native drop delegate to get the current directory path for a specific window.
    /// The windowId is the NSWindow pointer value (as string) on Mac Catalyst.
    /// </summary>
    string GetCurrentDirectoryForWindow(string windowId);

    /// <summary>
    /// Called by native drop delegate to execute the file move and notify affected directories.
    /// The nsWindow identifies which window received the drop for targeted refresh.
    /// </summary>
    void HandleExternalDrop(string[] sourcePaths, string targetDirectory, IntPtr nsWindow);
}
