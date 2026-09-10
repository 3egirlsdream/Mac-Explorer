using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Views.Dialogs;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private readonly IFileConversionService? _fileConversionService;
    private readonly IBackgroundTaskManager? _conversionTaskManager;
    private bool _conversionInProgress;

    private ContextMenuAction? BuildConversionContextMenu(FileSystemEntry entry)
    {
        var entries = GetContextEntries(entry);
        if (_fileConversionService == null || IsTrashActive || IsArchiveView || entries.Count != 1
            || entry.IsDirectory || !IsUsableLocalEntry(entry)
            || entry.FullPath.StartsWith(TrashPath.TrimEnd('/') + "/", StringComparison.Ordinal)) return null;
        var formats = _fileConversionService.GetAvailableFormats(entry.FullPath);
        if (formats.Count == 0) return null;
        return new ContextMenuAction
        {
            Label = "转换", IconSvg = AppIcons.Convert,
            SubItems = formats.Select(format => new ContextMenuAction
            {
                Label = format == FileConversionFormat.Docx ? "转为 Word（.docx）" : "转为 " + format.ToString().ToUpperInvariant(),
                IconSvg = format switch
                {
                    FileConversionFormat.Docx => AppIcons.FileText,
                    FileConversionFormat.Pdf => AppIcons.FilePdf,
                    _ => AppIcons.FileImage
                },
                IsEnabled = !_conversionInProgress,
                Execute = () => ConvertFileAsync(entry.FullPath, format)
            }).ToArray()
        };
    }

    private async Task ConvertFileAsync(string path, FileConversionFormat format)
    {
        if (_fileConversionService == null || _conversionInProgress) return;
        _conversionInProgress = true;
        IsContextMenuVisible = false;
        BackgroundTaskInfo? task = null;
        try
        {
            ConversionImageSize? size = null;
            if (format is FileConversionFormat.Png or FileConversionFormat.Jpg)
            {
                if (_topLevelWindow == null) return;
                var originalSize = await _fileConversionService.GetImageSizeAsync(path);
                var dialog = new ImageConversionDialog(originalSize, format);
                using var modalBlock = _topLevelWindow is MacExplorer.Views.MainWindow mainWindow ? mainWindow.BlockModalParentInteraction() : null;
                size = await dialog.ShowDialog<ConversionImageSize?>(_topLevelWindow);
                if (size == null) return;
            }
            task = _conversionTaskManager?.AddTask("正在转换 " + Path.GetFileName(path));
            StatusText = "正在转换 " + Path.GetFileName(path) + "…";
            var result = await _fileConversionService.ConvertAsync(new(path, format, size), task?.Cts.Token ?? CancellationToken.None);
            // Once the complete output has been committed, cancellation no longer applies.
            if (task != null) { task.CanCancel = false; _conversionTaskManager!.CompleteTask(task.Id); }
            var directory = Path.GetDirectoryName(path)!;
            var isCurrentDirectory = string.Equals(CurrentPath, directory, StringComparison.Ordinal);
            _directoryChangeNotifier?.NotifyChanged([directory], isCurrentDirectory ? this : null);
            if (isCurrentDirectory)
            {
                await RefreshAsync();
                if (string.Equals(CurrentPath, directory, StringComparison.Ordinal))
                {
                    var output = Entries.FirstOrDefault(item => item.FullPath == result.OutputPath);
                    if (output != null) SelectEntry(output);
                }
            }
            StatusText = "已生成 " + Path.GetFileName(result.OutputPath)
                + (result.Warnings.Count == 0 ? "" : "。" + string.Join("；", result.Warnings));
        }
        catch (OperationCanceledException)
        {
            if (task != null) _conversionTaskManager!.CancelTask(task.Id);
            StatusText = "已取消转换";
        }
        catch (Exception ex)
        {
            if (task != null) _conversionTaskManager!.FailTask(task.Id, ex.Message);
            StatusText = "转换失败：" + ex.Message;
        }
        finally { _conversionInProgress = false; }
    }
}
