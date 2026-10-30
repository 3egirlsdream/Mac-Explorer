using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;

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
    private readonly IFileOperationHistoryService? _fileOperationHistoryService;
    private readonly IFileTagService? _fileTagService;
    private readonly FileOperationService _operations;
    private readonly Microsoft.Extensions.Logging.ILogger<FileOpsViewModel>? _logger;
    public FileOperationResult? LastOperationResult { get; private set; }

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
        IFileOperationHistoryService? fileOperationHistoryService = null,
        IFileTagService? fileTagService = null,
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
        _fileOperationHistoryService = fileOperationHistoryService;
        _fileTagService = fileTagService;
        _logger = logger;
        _operations = new FileOperationService(_fileService, directoryChangeNotifier, aiTagService,
            fileTagService, fileOperationHistoryService, taskManager, logger);
    }

    // Rename support: event to notify the view to start inline rename
    public event Action<FileSystemEntry>? RequestRename;

    public void RaiseRequestRename(FileSystemEntry entry)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        RequestRename?.Invoke(entry);
    }

    [RelayCommand]
    public void CopySelected(IReadOnlyList<FileSystemEntry> selectedEntries)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        if (_clipboardService == null || selectedEntries.Count == 0) return;
        _clipboardService.CopyFiles(selectedEntries.Select(e => e.FullPath).ToArray());
        if (CutPaths.Count > 0) { CutPaths.Clear(); OnPropertyChanged(nameof(CutPaths)); }
    }

    [RelayCommand]
    public void CutSelected(IReadOnlyList<FileSystemEntry> selectedEntries)
        => CutSelectedFromTag(selectedEntries, null);

    internal void CutSelectedFromTag(IReadOnlyList<FileSystemEntry> selectedEntries, FileTag? sourceTag)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        if (_clipboardService == null || selectedEntries.Count == 0) return;
        _clipboardService.CutFiles(selectedEntries.Select(e => e.FullPath).ToArray(), sourceTag);
        CutPaths.Clear();
        foreach (var e in selectedEntries) CutPaths.Add(e.FullPath);
        OnPropertyChanged(nameof(CutPaths));
    }

    internal void ConsumeTagCut(ClipboardEntry entry, IReadOnlyList<string> paths)
    {
        foreach (var path in paths) entry.SourcePaths.Remove(path);
        if (!ReferenceEquals(_clipboardService?.GetClipboardEntry(), entry)) return;
        CutPaths.ExceptWith(paths);
        OnPropertyChanged(nameof(CutPaths));
        if (entry.IsEmpty) _clipboardService.Clear();
    }

    public List<string> GetPasteConflicts(string currentPath)
    {
        if (_clipboardService == null || !_clipboardService.HasClipboardFiles) return [];
        var entry = _clipboardService.GetClipboardEntry();
        if (entry == null || entry.Operation != ClipboardOperation.Cut) return [];

        // For remote paths, skip local conflict detection (SFTP will handle it)
        if (VirtualPath.IsRemotePath(currentPath)) return [];

        return entry.SourcePaths
            .Select(p => Path.GetFileName(p))
            .Where(name => File.Exists(Path.Combine(currentPath, name)) || Directory.Exists(Path.Combine(currentPath, name)))
            .ToList();
    }

    public Task PasteAsync(string currentPath, bool overwrite = false)
    {
        var clipboardEntry = _clipboardService?.GetClipboardEntry();
        var paths = clipboardEntry?.SourcePaths.ToArray() ?? [];
        var kind = clipboardEntry?.Operation == ClipboardOperation.Cut ? BackgroundTaskKind.Move : BackgroundTaskKind.Copy;
        return FileTaskContext.RunAsync(_taskManager, new(kind, Services.Impl.LocalizationText.Get("粘贴文件"), paths, currentPath),
            async _ => { await PasteCoreAsync(currentPath, overwrite, clipboardEntry, paths); return true; });
    }

    private async Task PasteCoreAsync(string currentPath, bool overwrite, ClipboardEntry? entry, string[] sourcePaths)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        LastOperationResult = null;
        if (_clipboardService == null || entry == null) return;
        if (entry.Operation == ClipboardOperation.Copy)
        {
            try { LastOperationResult = await _operations.CopyDetailedAsync(sourcePaths, currentPath); }
            catch (FileOperationPartialException ex)
            {
                LastOperationResult = ex.Result;
                ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                throw;
            }
            return;
        }

        var affectedDirs = new HashSet<string>(StringComparer.Ordinal) { currentPath };
        foreach (var sourcePath in sourcePaths)
        {
            var directory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(directory)) affectedDirs.Add(directory);
        }
        var completed = new List<string>();
        var warnings = new List<string>();
        Exception? metadataError = null;
        try
        {
            foreach (var sourcePath in sourcePaths)
            {
                await FileTaskContext.Current!.StepAsync("paste-cut:" + sourcePath, async () =>
                { await _fileService.MoveAsync(sourcePath, currentPath, overwrite); return true; });
                var movedPath = CombineDestinationPath(currentPath, Path.GetFileName(sourcePath));
                completed.Add(movedPath);
                // Consume only confirmed moves so a retry cannot move a missing source.
                entry.SourcePaths.Remove(sourcePath);
                if (ReferenceEquals(_clipboardService.GetClipboardEntry(), entry) && CutPaths.Remove(sourcePath))
                    OnPropertyChanged(nameof(CutPaths));
                if (_fileTagService != null && !VirtualPath.IsRemotePath(currentPath))
                {
                    try
                    {
                        await _fileTagService.UpdatePathAsync(sourcePath, movedPath);
                        if (entry.SourceTag is { } sourceTag)
                        {
                            var sync = await _fileTagService.SetTagAsync([movedPath], sourceTag, false);
                            if (sync.PendingFiles > 0)
                                warnings.Add(Services.Impl.LocalizationText.Get("标签已保存，{0} 个文件等待同步 Finder", sync.PendingFiles));
                        }
                    }
                    catch (Exception ex)
                    {
                        metadataError ??= ex;
                        warnings.Add($"{movedPath}：标签未同步（{ex.Message}）");
                        _logger?.LogError(ex, "Failed to update pasted file tags");
                    }
                }
            }
            LastOperationResult = new(completed, [], warnings);
        }
        catch (Exception ex)
        {
            LastOperationResult = new(completed, sourcePaths.Skip(completed.Count).Take(1).ToArray(),
                warnings, sourcePaths.Skip(completed.Count + 1).ToArray());
            _logger?.LogError(ex, "Paste failed");
            throw;
        }
        finally
        {
            if (entry.IsEmpty && ReferenceEquals(_clipboardService.GetClipboardEntry(), entry))
            {
                _clipboardService.Clear();
                CutPaths.Clear();
                OnPropertyChanged(nameof(CutPaths));
            }
            _directoryChangeNotifier?.NotifyChanged(affectedDirs.ToArray(), null);
        }
        if (metadataError != null) ExceptionDispatchInfo.Capture(metadataError).Throw();
    }

    public Task<IReadOnlyList<string>> CopyEntriesAsync(
        IReadOnlyList<string> sourcePaths, string destinationDirectory)
        => _operations.CopyAsync(sourcePaths, destinationDirectory);

    public Task<FileOperationResult> CopyEntriesDetailedAsync(
        IReadOnlyList<string> sourcePaths, string destinationDirectory)
        => _operations.CopyDetailedAsync(sourcePaths, destinationDirectory);

    private sealed class FileProgress(Action<FileOperationProgress> report) : IProgress<FileOperationProgress>
    {
        // The manager dispatches UI updates. Report synchronously to avoid queued
        // progress arriving after CompleteTask and resetting its final percentage.
        public void Report(FileOperationProgress value) => report(value);
    }

    public Task DeleteSelectedAsync(
        IReadOnlyList<FileSystemEntry> selectedEntries,
        string currentPath,
        Action<string>? setStatus = null,
        FileListViewModel? refreshedViewModel = null)
        => _operations.DeleteToTrashAsync(selectedEntries, currentPath, setStatus, refreshedViewModel);

    public Task MoveEntryAsync(
        FileSystemEntry source,
        FileSystemEntry targetFolder,
        Action<string>? setStatus = null)
        => _operations.MoveAsync(source, targetFolder, setStatus);

    public Task<FileOperationResult> MoveEntryDetailedAsync(
        FileSystemEntry source, FileSystemEntry targetFolder, Action<string>? setStatus = null)
        => _operations.MoveDetailedAsync(source, targetFolder, setStatus);

    public List<string> GetMoveConflicts(IReadOnlyList<FileSystemEntry> entries, string targetDirectory)
    {
        // For remote paths, skip local conflict detection (SFTP will handle it)
        if (VirtualPath.IsRemotePath(targetDirectory)) return [];

        var conflicts = new List<string>();
        foreach (var entry in entries)
        {
            var destPath = Path.Combine(targetDirectory, entry.Name);
            if (PathsEqual(entry.FullPath, destPath))
                continue;
            var exists = File.Exists(destPath) || Directory.Exists(destPath);
            if (exists)
                conflicts.Add(entry.Name);
        }
        return conflicts;
    }

    public async Task MoveEntriesAsync(
        IReadOnlyList<FileSystemEntry> entries,
        FileSystemEntry targetFolder,
        Action<string>? setStatus = null,
        bool overwrite = false)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        LastOperationResult = null;
        if (!targetFolder.IsDirectory) return;

        var allSourcePaths = entries.Select(e => e.FullPath).ToList();
        if (IsInvalidMoveTarget(allSourcePaths, targetFolder.FullPath)) return;

        var sourcePaths = entries
            .Where(entry => entry != targetFolder)
            .Select(entry => entry.FullPath)
            .Where(path => !PathsEqual(path, Path.Combine(targetFolder.FullPath, Path.GetFileName(path))))
            .ToList();
        if (sourcePaths.Count == 0) return;
        var affectedDirectories = GetAffectedMoveDirectories(entries, targetFolder.FullPath);

        await FileTaskContext.RunAsync(_taskManager, new(BackgroundTaskKind.Move,
            Services.Impl.LocalizationText.Get("移动 {0} 项", sourcePaths.Count), sourcePaths, targetFolder.FullPath), async context =>
        {
            var completed = new List<string>(); var warnings = new List<string>();
            try
            {
                foreach (var path in sourcePaths)
                {
                    await context.CheckpointAsync();
                    var output = CombineDestinationPath(targetFolder.FullPath, Path.GetFileName(path));
                    var replaced = context.Remember("move-replaced:" + output,
                        () => overwrite && _fileService.ExistsAsync(output).GetAwaiter().GetResult());
                    await context.StepAsync("move-selection:" + path, async () =>
                    {
                        if (_fileService.IsCrossVolume(path, targetFolder.FullPath))
                            await _fileService.MoveWithProgressAsync([path], targetFolder.FullPath,
                                new FileProgress(p => _taskManager?.UpdateProgress(context.Task.Id, p.Percentage, p.CurrentFile)), context.CancellationToken);
                        else await _fileService.MoveAsync(path, targetFolder.FullPath, overwrite);
                        if (_fileOperationHistoryService != null)
                        {
                            if (replaced) await _fileOperationHistoryService.RecordIrreversibleAsync(output,
                                Services.Impl.LocalizationText.Get("覆盖操作不可撤销。"));
                            else await _fileOperationHistoryService.RecordMoveAsync(path, output);
                        }
                        return true;
                    });
                    completed.Add(output); context.ReportItem(path, output, FileTaskItemState.Completed);
                    if (_fileTagService != null)
                        try { await _fileTagService.UpdatePathAsync(path, output); }
                        catch (Exception ex) { warnings.Add(ex.Message); }
                }
                LastOperationResult = new(completed, [], warnings);
                return true;
            }
            catch (Exception ex)
            {
                LastOperationResult = new(completed, sourcePaths.Skip(completed.Count).Take(1).ToArray(), warnings,
                    sourcePaths.Skip(completed.Count + 1).ToArray());
                setStatus?.Invoke(Services.Impl.LocalizationText.Get("移动失败: {0}", ex.Message));
                throw;
            }
            finally { _directoryChangeNotifier?.NotifyChanged(affectedDirectories, null); }
        });
    }

    private static string[] GetAffectedMoveDirectories(IEnumerable<FileSystemEntry> entries, string targetDirectory)
    {
        var dirs = new HashSet<string>(StringComparer.Ordinal) { targetDirectory };
        foreach (var entry in entries)
        {
            var sourceDir = Path.GetDirectoryName(entry.FullPath);
            if (!string.IsNullOrEmpty(sourceDir))
                dirs.Add(sourceDir);
            if (entry.IsDirectory)
            {
                dirs.Add(entry.FullPath);
                dirs.Add(Path.Combine(targetDirectory, entry.Name));
            }
        }
        return dirs.ToArray();
    }

    private string CombineDestinationPath(string directory, string name)
        => VirtualPath.IsRemotePath(directory)
            ? _fileService.CombinePath(directory, name)
            : Path.Combine(directory, name);

    private static bool IsInvalidMoveTarget(IEnumerable<string> sourcePaths, string targetDirectory)
    {
        foreach (var sourcePath in sourcePaths)
        {
            if (string.Equals(sourcePath, targetDirectory, StringComparison.Ordinal))
                return true;
            if (IsDescendantPath(targetDirectory, sourcePath))
                return true;
        }
        return false;
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(first, second, StringComparison.Ordinal);
        }
    }

    private static bool IsDescendantPath(string candidatePath, string ancestorPath)
    {
        var normalizedAncestor = ancestorPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedAncestor.Length > 0
            && candidatePath.Length > normalizedAncestor.Length
            && candidatePath.StartsWith(normalizedAncestor + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public Task RenameEntryAsync(FileSystemEntry entry, string newName, bool isAiView, Action<string>? setStatus = null)
        => FileTaskContext.RunAsync(_taskManager, new(BackgroundTaskKind.Rename, Services.Impl.LocalizationText.Get("重命名"),
            [entry.FullPath], CombineDestinationPath(Path.GetDirectoryName(entry.FullPath) ?? "", newName)),
            async _ => { await RenameEntryCoreAsync(entry, newName, isAiView, setStatus); return true; });

    private async Task RenameEntryCoreAsync(
        FileSystemEntry entry,
        string newName,
        bool isAiView,
        Action<string>? setStatus = null)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        // Virtual face cluster rename - handled by AiViewModel
        if (entry.IsVirtual)
            return;

        try
        {
            var oldPath = entry.FullPath;
            await FileTaskContext.Current!.StepAsync("inline-rename:" + oldPath, async () =>
            { await _fileService.RenameAsync(oldPath, newName); return true; });

            var dir = Path.GetDirectoryName(oldPath) ?? "";
            var newPath = Path.Combine(dir, newName);

            // Record for undo
            if (_fileOperationHistoryService != null)
                await _fileOperationHistoryService.RecordRenameAsync(oldPath, newPath);

            if (_aiTagService != null)
                await _aiTagService.UpdateFilePathAsync(oldPath, newPath);

            if (_fileTagService != null)
                await _fileTagService.UpdatePathAsync(oldPath, newPath);

            // Update file index so FTS5 search reflects the new name
            if (_fileIndexWriter != null)
                await _fileIndexWriter.RenameEntryAsync(oldPath, newPath, newName);

            // Sync update PIN folder paths
            if (_pinnedFolderService != null && entry.IsDirectory)
            {
                await _pinnedFolderService.UpdateFolderPathAsync(oldPath, newPath, newName);
            }
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"重命名失败: {ex.Message}");
            throw;
        }
        finally
        {
            _directoryChangeNotifier?.NotifyChanged([Path.GetDirectoryName(entry.FullPath) ?? ""], null);
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

    public async Task CreateNewFolderAsync(string currentPath, IReadOnlyList<FileSystemEntry> rawEntries, Action<string>? setStatus = null, Func<string, Task>? refreshCallback = null)
    {
        var entries = rawEntries.ToArray();
        string fullPath;
        try
        {
            fullPath = await FileTaskContext.RunAsync(_taskManager,
                new(BackgroundTaskKind.Create, Services.Impl.LocalizationText.Get("新建文件夹"), [], currentPath),
                _ => CreateNewFolderCoreAsync(currentPath, entries));
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"创建文件夹失败: {ex.Message}");
            throw;
        }
        // Refresh, selection and inline rename belong to the caller's UI context.
        if (refreshCallback != null) await refreshCallback(Path.GetFileName(fullPath));
    }

    private async Task<string> CreateNewFolderCoreAsync(string currentPath, IReadOnlyList<FileSystemEntry> rawEntries)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        var name = GetUniqueNameInCurrentDir("未命名文件夹", isDirectory: true, rawEntries);
        var fullPath = await _fileService.CreateFolderAsync(currentPath, name);
        if (_fileOperationHistoryService != null) await _fileOperationHistoryService.RecordCreatedAsync(fullPath);
        return fullPath;
    }

    public async Task CreateNewFileAsync(string currentPath, IReadOnlyList<FileSystemEntry> rawEntries, string? extension = null, Action<string>? setStatus = null, Func<string, Task>? refreshCallback = null)
    {
        var entries = rawEntries.ToArray();
        string fullPath;
        try
        {
            fullPath = await FileTaskContext.RunAsync(_taskManager,
                new(BackgroundTaskKind.Create, Services.Impl.LocalizationText.Get("新建文件"), [], currentPath),
                _ => CreateNewFileCoreAsync(currentPath, entries, extension));
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"创建文件失败: {ex.Message}");
            throw;
        }
        if (refreshCallback != null) await refreshCallback(Path.GetFileName(fullPath));
    }

    private async Task<string> CreateNewFileCoreAsync(string currentPath, IReadOnlyList<FileSystemEntry> rawEntries, string? extension)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
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

        if (_fileOperationHistoryService != null) await _fileOperationHistoryService.RecordCreatedAsync(fullPath);
        return fullPath;
    }

    public async Task PasteImageAsync(string currentPath, ClipboardImageData image, Action<string>? setStatus = null, Func<string, Task>? refreshCallback = null)
    {
        var path = await FileTaskContext.RunAsync(_taskManager,
            new(BackgroundTaskKind.Create, Services.Impl.LocalizationText.Get("粘贴文件"), [], currentPath),
            _ => PasteImageCoreAsync(currentPath, image));
        var name = Path.GetFileName(path);
        setStatus?.Invoke($"已粘贴图片：{name}");
        if (refreshCallback != null) await refreshCallback(name);
    }

    private async Task<string> PasteImageCoreAsync(string currentPath, ClipboardImageData image)
    {
        Services.Subscriptions.SubscriptionAccess.RequireAccess();
        var path = await _fileService.CreateFileWithContentAsync(currentPath,
            $"图片 {DateTime.Now:yyyy-MM-dd HH.mm.ss}{image.Extension}", image.Bytes);
        if (_fileOperationHistoryService != null) await _fileOperationHistoryService.RecordCreatedAsync(path);
        return path;
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
