using FKFinder.Models;

namespace FKFinder.Services;

public interface IClipboardService
{
    void CopyFiles(string[] paths);
    void CutFiles(string[] paths);
    Task PasteFilesAsync(string targetDirectory);
    bool HasClipboardFiles { get; }
    ClipboardEntry? GetClipboardEntry();
    void Clear();
}
