using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IClipboardService
{
    void CopyFiles(string[] paths);
    void CutFiles(string[] paths);
    Task CopyTextAsync(string text);
    Task PasteFilesAsync(string targetDirectory);
    bool HasClipboardFiles { get; }
    ClipboardEntry? GetClipboardEntry();
    void Clear();
}
