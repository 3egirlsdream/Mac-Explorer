using MacExplorer.Models;
using MacExplorer.Services;
using Foundation;
using UIKit;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// Mac Catalyst implementation of IClipboardService.
/// Manages internal clipboard state for copy/cut/paste operations.
/// </summary>
public class MacClipboardService : IClipboardService
{
    private ClipboardEntry? _entry;

    public void CopyFiles(string[] paths)
    {
        _entry = new ClipboardEntry
        {
            SourcePaths = paths.ToList(),
            Operation = ClipboardOperation.Copy
        };

        WriteToSystemPasteboard(paths);
    }

    public void CutFiles(string[] paths)
    {
        _entry = new ClipboardEntry
        {
            SourcePaths = paths.ToList(),
            Operation = ClipboardOperation.Cut
        };

        WriteToSystemPasteboard(paths);
    }

    /// <summary>
    /// Writes file URLs to the system pasteboard so other apps (e.g. Finder) can paste them.
    /// </summary>
    private static void WriteToSystemPasteboard(string[] paths)
    {
        var pasteboard = UIPasteboard.General;
        var urls = paths.Select(p => NSUrl.FromFilename(p)).ToArray();
        pasteboard.Urls = urls;
    }

    public async Task PasteFilesAsync(string targetDirectory)
    {
        if (_entry == null || _entry.IsEmpty) return;

        // The actual file operations are delegated to IFileService
        // This method is called from the ViewModel which handles the file operations
        await Task.CompletedTask;
    }

    public bool HasClipboardFiles => _entry is { IsEmpty: false };

    public ClipboardEntry? GetClipboardEntry() => _entry;

    public void Clear()
    {
        _entry = null;
    }
}
