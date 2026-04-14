using FKFinder.ViewModels;

namespace FKFinder.Services;

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
    /// Fired when files are dropped from an external source (Finder, another app, another FKFinder window).
    /// Parameters: source file paths, target directory path.
    /// </summary>
    event Action<string[], string>? ExternalDropReceived;

    /// <summary>
    /// Called by native drop delegate to notify the ViewModel about an external drop.
    /// </summary>
    void NotifyExternalDrop(string[] sourcePaths, string targetDirectory);
}
