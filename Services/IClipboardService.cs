using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IClipboardService
{
    void CopyFiles(string[] paths);
    void CutFiles(string[] paths);
    Task CopyTextAsync(string text);
    Task PasteFilesAsync(string targetDirectory);
    bool HasClipboardFiles { get; }

    /// <summary>应用内剪贴板或系统剪贴板是否有可粘贴内容（文件或图片）。需在 UI 线程调用。</summary>
    bool HasPasteableContent { get; }

    /// <summary>探测当前可粘贴的内容类型，供粘贴入口与菜单启用状态使用。需在 UI 线程调用。</summary>
    ClipboardPasteKind GetPasteKind();

    /// <summary>把外部文件 URL 采纳为应用内复制条目，便于复用现有粘贴管线；成功返回 true。需在 UI 线程调用。</summary>
    bool TryAdoptExternalFiles();

    /// <summary>读取系统剪贴板中的图片数据，无图片或读取失败时返回 null。需在 UI 线程调用。</summary>
    ClipboardImageData? ReadExternalImage();

    ClipboardEntry? GetClipboardEntry();
    void Clear();
}
