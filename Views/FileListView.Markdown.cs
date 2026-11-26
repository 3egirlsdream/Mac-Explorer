using Avalonia.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views;

public partial class FileListView
{
    private ContextMenuAction? CreateTextEditAction(FileSystemEntry? entry)
    {
        if (entry == null || entry.IsDirectory || entry.IsVirtual || !TextFileTypes.IsText(entry.Name)
            || ViewModel is not { } vm || vm.SelectedEntries.Count != 1 || vm.IsRemoteView
            || vm.IsTrashActive || VirtualPath.IsRemotePath(entry.FullPath))
            return null;

        var path = entry.FullPath;
        var archiveWriter = vm.IsArchiveView ? vm.GetArchiveTextWriter(entry) : null;
        if (vm.IsArchiveView ? archiveWriter == null : !Path.IsPathFullyQualified(path) || !entry.IsWritable)
            return null;

        return new ContextMenuAction
        {
            Label = LocalizationText.Get("编辑"),
            IconSvg = Assets.Icons.Edit,
            IsQuickAction = true,
            Execute = async () =>
            {
                var window = TopLevel.GetTopLevel(this) as MainWindow;
                DismissContextMenu();
                if (window == null) return;
                try { await window.OpenTextEditorAsync(path, archiveWriter); }
                catch (Exception ex) { vm.StatusText = LocalizationText.Get("打开失败：{0}", ex.Message); }
            }
        };
    }
}
