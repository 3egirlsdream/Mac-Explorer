using Avalonia.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Markdown;

namespace MacExplorer.Views;

public partial class FileListView
{
    private ContextMenuAction? CreateMarkdownEditAction(FileSystemEntry? entry)
    {
        if (entry == null || entry.IsDirectory || entry.IsVirtual || !MarkdownDocument.IsMarkdown(entry.Name)
            || !Path.IsPathFullyQualified(entry.FullPath)
            || ViewModel == null || ViewModel.SelectedEntries.Count != 1
            || ViewModel.IsArchiveView || ViewModel.IsRemoteView)
            return null;
        var path = entry.FullPath; // Capture the clicked file, not a later mutable selection.
        return new ContextMenuAction
        {
            Label = "编辑",
            IconSvg = Assets.Icons.Edit,
            IsQuickAction = true,
            Execute = async () =>
            {
                var window = TopLevel.GetTopLevel(this) as MainWindow;
                DismissContextMenu();
                if (window == null) return;
                try { await window.OpenMarkdownEditorAsync(path); }
                catch (Exception ex)
                {
                    if (ViewModel != null) ViewModel.StatusText = "无法打开 Markdown 编辑器：" + ex.Message;
                }
            }
        };
    }
}
