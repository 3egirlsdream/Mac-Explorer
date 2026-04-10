using FKFinder.Models;
using FKFinder.Services;

namespace FKFinder.Platforms.MacCatalyst.Services;

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
    }

    public void CutFiles(string[] paths)
    {
        _entry = new ClipboardEntry
        {
            SourcePaths = paths.ToList(),
            Operation = ClipboardOperation.Cut
        };
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
