using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.Logging;

namespace MacExplorer.ViewModels;

public partial class FileOpsViewModel : ObservableObject
{
    private readonly IClipboardService? _clipboardService;
    private readonly IFileService _fileService;
    private readonly IBackgroundTaskManager? _taskManager;
    private readonly IDirectoryChangeNotifier? _directoryChangeNotifier;
    private readonly IAiTagService? _aiTagService;
    private readonly IPinnedFolderService? _pinnedFolderService;
    private readonly IFileIndexWriter? _fileIndexWriter;
    private readonly IFileIndex? _fileIndex;
    private readonly Microsoft.Extensions.Logging.ILogger<FileOpsViewModel>? _logger;

    /// <summary>被剪切文件的完整路径集合，用于 UI 半透明显示</summary>
    public HashSet<string> CutPaths { get; } = [];

    public FileOpsViewModel(
        IClipboardService? clipboardService = null,
        IFileService? fileService = null,
        IBackgroundTaskManager? taskManager = null,
        IDirectoryChangeNotifier? directoryChangeNotifier = null,
        IAiTagService? aiTagService = null,
        IPinnedFolderService? pinnedFolderService = null,
        IFileIndexWriter? fileIndexWriter = null,
        IFileIndex? fileIndex = null,
        Microsoft.Extensions.Logging.ILogger<FileOpsViewModel>? logger = null)
    {
        _clipboardService = clipboardService;
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _taskManager = taskManager;
        _directoryChangeNotifier = directoryChangeNotifier;
        _aiTagService = aiTagService;
        _pinnedFolderService = pinnedFolderService;
        _fileIndexWriter = fileIndexWriter;
        _fileIndex = fileIndex;
        _logger = logger;
    }

    // Rename support: event to notify the view to start inline rename
    public event Action<FileSystemEntry>? RequestRename;

    public void RaiseRequestRename(FileSystemEntry entry)
    {
        RequestRename?.Invoke(entry);
    }

    [RelayCommand]
    public void CopySelected(IReadOnlyList<FileSystemEntry> selectedEntries)
    {
        if (_clipboardService == null || selectedEntries.Count == 0) return;
        _clipboardService.CopyFiles(selectedEntries.Select(e => e.FullPath).ToArray());
        if (CutPaths.Count > 0) { CutPaths.Clear(); OnPropertyChanged(nameof(CutPaths)); }
    }

    [RelayCommand]
    public void CutSelected(IReadOnlyList<FileSystemEntry> selectedEntries)
    {
        if (_clipboardService == null || selectedEntries.Count == 0) return;
        _clipboardService.CutFiles(selectedEntries.Select(e => e.FullPath).ToArray());
        CutPaths.Clear();
        foreach (var e in selectedEntries) CutPaths.Add(e.FullPath);
        OnPropertyChanged(nameof(CutPaths));
    }

    public List<string> GetPasteConflicts(string currentPath)
    {
        if (_clipboardService == null || !_clipboardService.HasClipboardFiles) return [];
        var entry = _clipboardService.GetClipboardEntry();
        if (entry == null || entry.Operation != ClipboardOperation.Cut) return [];

        return entry.SourcePaths
            .Select(p => Path.GetFileName(p))
            .Where(name => File.Exists(Path.Combine(currentPath, name)) || Directory.Exists(Path.Combine(currentPath, name)))
            .ToList();
    }

    public async Task PasteAsync(string currentPath, bool isCollectionView, int? currentCollectionId, bool overwrite = false)
    {
        if (_clipboardService == null || !_clipboardService.HasClipboardFiles) return;
        var entry = _clipboardService.GetClipboardEntry();
        if (entry == null) return;

        try
        {
            foreach (var sourcePath in entry.SourcePaths)
            {
                if (entry.Operation == ClipboardOperation.Copy)
                    await _fileService.CopyAsync(sourcePath, currentPath);
                else
                    await _fileService.MoveAsync(sourcePath, currentPath, overwrite);
            }
            if (entry.Operation == ClipboardOperation.Cut) { _clipboardService.Clear(); CutPaths.Clear(); OnPropertyChanged(nameof(CutPaths)); }

            // Notify other windows: current dir + source directories
            var affectedDirs = new HashSet<string> { currentPath };
            foreach (var sp in entry.SourcePaths)
            {
                var dir = Path.GetDirectoryName(sp);
                if (!string.IsNullOrEmpty(dir)) affectedDirs.Add(dir);
            }
            _directoryChangeNotifier?.NotifyChanged(affectedDirs.ToArray(), null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Paste failed");
            throw;
        }
    }

    public async Task DeleteSelectedAsync(
        IReadOnlyList<FileSystemEntry> selectedEntries,
        string currentPath,
        bool isCollectionView,
        int? currentCollectionId,
        Action<string>? setStatus = null)
    {
        if (selectedEntries.Count == 0) return;
        try
        {
            var deletedPaths = selectedEntries.Select(e => e.FullPath).ToList();
            foreach (var entry in selectedEntries)
                await _fileService.DeleteAsync(entry.FullPath, moveToTrash: true);

            // Clean up AI analysis data for deleted files
            if (_aiTagService != null)
            {
                try { await _aiTagService.DeleteAnalysisForFilesAsync(deletedPaths); }
                catch (Exception ex) { _logger?.LogError(ex, "Failed to delete AI analysis data for {Count} files", deletedPaths.Count); }
            }

            _directoryChangeNotifier?.NotifyChanged([currentPath], null);
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"删除失败: {ex.Message}");
            throw;
        }
    }

    public async Task MoveEntryAsync(
        FileSystemEntry source,
        FileSystemEntry targetFolder,
        Action<string>? setStatus = null)
    {
        if (!targetFolder.IsDirectory) return;
        try
        {
            await _fileService.MoveAsync(source.FullPath, targetFolder.FullPath);
            _directoryChangeNotifier?.NotifyChanged([Path.GetDirectoryName(source.FullPath) ?? "", targetFolder.FullPath], null);
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"移动失败: {ex.Message}");
            throw;
        }
    }

    public List<string> GetMoveConflicts(IReadOnlyList<FileSystemEntry> entries, string targetDirectory)
    {
        var conflicts = new List<string>();
        foreach (var entry in entries)
        {
            var destPath = Path.Combine(targetDirectory, entry.Name);
            var exists = File.Exists(destPath) || Directory.Exists(destPath);
            System.IO.File.AppendAllText("/tmp/fkfinder_drag_debug.log",
                $"{DateTime.Now:HH:mm:ss.fff} [GetMoveConflicts] destPath={destPath}, exists={exists}\n");
            if (exists)
                conflicts.Add(entry.Name);
        }
        return conflicts;
    }

    public async Task MoveEntriesAsync(
        IReadOnlyList<FileSystemEntry> entries,
        FileSystemEntry targetFolder,
        Action<string>? setStatus = null,
        Action<string?>? setActiveTaskId = null,
        bool overwrite = false)
    {
        if (!targetFolder.IsDirectory) return;

        var sourcePaths = entries.Where(e => e != targetFolder)
            .Select(e => e.FullPath).ToList();
        if (sourcePaths.Count == 0) return;

        bool crossVolume = _fileService.IsCrossVolume(sourcePaths[0], targetFolder.FullPath);

        if (!crossVolume)
        {
            try
            {
                foreach (var path in sourcePaths)
                    await _fileService.MoveAsync(path, targetFolder.FullPath, overwrite);
                _directoryChangeNotifier?.NotifyChanged([Path.GetDirectoryName(sourcePaths[0]) ?? "", targetFolder.FullPath], null);
            }
            catch (Exception ex)
            {
                setStatus?.Invoke($"移动失败: {ex.Message}");
                throw;
            }
            return;
        }

        // 跨卷：后台任务 + 进度弹窗
        if (_taskManager == null) return;

        var taskInfo = _taskManager.AddTask("正在移动...", async () => { });

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<Models.FileOperationProgress>(p =>
                {
                    _taskManager.UpdateProgress(taskInfo.Id, p.Percentage, p.CurrentFile);
                });
                await _fileService.MoveWithProgressAsync(sourcePaths, targetFolder.FullPath,
                    progress, taskInfo.Cts.Token);
                _taskManager.CompleteTask(taskInfo.Id);
            }
            catch (OperationCanceledException)
            {
                _taskManager.RemoveTask(taskInfo.Id);
            }
            catch (Exception ex)
            {
                _taskManager.FailTask(taskInfo.Id, ex.Message);
            }
        });
    }

    public async Task RenameEntryAsync(
        FileSystemEntry entry,
        string newName,
        bool isAiView,
        Action<string>? setStatus = null)
    {
        // Virtual face cluster rename - handled by AiViewModel
        if (entry.IsVirtual)
            return;

        try
        {
            var oldPath = entry.FullPath;
            await _fileService.RenameAsync(oldPath, newName);

            var dir = Path.GetDirectoryName(oldPath) ?? "";
            var newPath = Path.Combine(dir, newName);

            if (_aiTagService != null)
                await _aiTagService.UpdateFilePathAsync(oldPath, newPath);

            // Update file index so FTS5 search reflects the new name
            if (_fileIndexWriter != null)
                await _fileIndexWriter.RenameEntryAsync(oldPath, newPath, newName);

            // Sync update PIN folder paths
            if (_pinnedFolderService != null && entry.IsDirectory)
            {
                await _pinnedFolderService.UpdateFolderPathAsync(oldPath, newPath, newName);
            }

            _directoryChangeNotifier?.NotifyChanged([Path.GetDirectoryName(oldPath) ?? ""], null);
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"重命名失败: {ex.Message}");
            throw;
        }
    }

    private string GetUniqueNameInCurrentDir(string baseName, bool isDirectory, IReadOnlyList<FileSystemEntry> rawEntries)
    {
        var existingNames = new HashSet<string>(rawEntries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains(baseName)) return baseName;

        // For files, separate name and extension
        string nameWithoutExt, ext;
        if (!isDirectory)
        {
            ext = Path.GetExtension(baseName);
            nameWithoutExt = string.IsNullOrEmpty(ext) ? baseName : baseName[..^ext.Length];
        }
        else
        {
            ext = "";
            nameWithoutExt = baseName;
        }

        for (int i = 2; ; i++)
        {
            var candidate = $"{nameWithoutExt} {i}{ext}";
            if (!existingNames.Contains(candidate)) return candidate;
        }
    }

    public async Task CreateNewFolderAsync(
        string currentPath,
        IReadOnlyList<FileSystemEntry> rawEntries,
        Action<string>? setStatus = null,
        Func<string, Task>? refreshCallback = null)
    {
        try
        {
            var name = GetUniqueNameInCurrentDir("未命名文件夹", isDirectory: true, rawEntries);
            var fullPath = await _fileService.CreateFolderAsync(currentPath, name);
            if (refreshCallback != null)
                await refreshCallback(name);
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"创建文件夹失败: {ex.Message}");
            throw;
        }
    }

    public async Task CreateNewFileAsync(
        string currentPath,
        IReadOnlyList<FileSystemEntry> rawEntries,
        string? extension = null,
        Action<string>? setStatus = null,
        Func<string, Task>? refreshCallback = null)
    {
        try
        {
            var ext = extension ?? ".txt";
            var baseName = ext.ToLowerInvariant() switch
            {
                ".docx" => "未命名文稿.docx",
                ".xlsx" => "未命名表格.xlsx",
                ".pptx" => "未命名演示文稿.pptx",
                ".pages" => "未命名文稿.pages",
                ".numbers" => "未命名表格.numbers",
                ".key" => "未命名演示文稿.key",
                ".txt" => "未命名.txt",
                _ => $"未命名{ext}"
            };
            var name = GetUniqueNameInCurrentDir(baseName, isDirectory: false, rawEntries);

            var template = FileTemplateProvider.GetTemplate(ext);
            var fullPath = template != null
                ? await _fileService.CreateFileWithContentAsync(currentPath, name, template)
                : await _fileService.CreateFileAsync(currentPath, name);

            if (refreshCallback != null)
                await refreshCallback(name);
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"创建文件失败: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> IsFolderPinnedAsync(string path)
    {
        if (_pinnedFolderService == null) return false;
        return await _pinnedFolderService.IsPinnedAsync(path);
    }

    public async Task PinFolderAsync(string path, string displayName)
    {
        if (_pinnedFolderService == null) return;
        await _pinnedFolderService.PinAsync(path, displayName);
    }

    public async Task UnpinFolderAsync(string path)
    {
        if (_pinnedFolderService == null) return;
        await _pinnedFolderService.UnpinAsync(path);
    }
}