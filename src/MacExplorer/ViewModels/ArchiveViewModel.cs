using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class ArchiveViewModel : ObservableObject
{
    private readonly IArchiveService? _archiveService;
    private readonly IBackgroundTaskManager? _taskManager;
    private readonly IApplicationLauncherService? _launcherService;
    private readonly IFileService? _fileService;

    [ObservableProperty]
    private bool _isCompressDialogVisible;

    [ObservableProperty]
    private CompressOptions? _pendingCompressOptions;

    [ObservableProperty]
    private string? _activeTaskId;

    public ArchiveViewModel(
        IArchiveService? archiveService = null,
        IBackgroundTaskManager? taskManager = null,
        IApplicationLauncherService? launcherService = null,
        IFileService? fileService = null)
    {
        _archiveService = archiveService;
        _taskManager = taskManager;
        _launcherService = launcherService;
        _fileService = fileService;
    }

    public async Task NavigateToArchiveAsync(
        string archivePath,
        string internalPath,
        Action<string> setCurrentPath,
        Action updateBreadcrumbs,
        Action<ObservableCollection<FileSystemEntry>> applyEntries,
        Action<string> setStatus,
        Action<bool> setLoading)
    {
        if (_archiveService == null) return;

        setCurrentPath(ArchivePathHelper.Build(archivePath, internalPath));
        updateBreadcrumbs();
        setLoading(true);

        try
        {
            var entries = await _archiveService.GetArchiveContentsAsync(archivePath, internalPath);
            applyEntries(new ObservableCollection<FileSystemEntry>(entries));
        }
        catch (Exception ex)
        {
            setStatus($"无法打开归档: {ex.Message}");
        }
        finally
        {
            setLoading(false);
        }
    }

    public async Task ExtractHereAsync(
        FileSystemEntry entry,
        string currentPath,
        Action<string> setStatus,
        Action<string?> setActiveTaskId,
        Func<Task> refreshCallback)
    {
        if (_archiveService == null || _taskManager == null) return;
        var destDir = Path.GetDirectoryName(entry.FullPath) ?? currentPath;

        var taskInfo = _taskManager.AddTask("正在解压...", async () =>
        {
            await refreshCallback();
        });
        setActiveTaskId(taskInfo.Id);

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<ArchiveProgress>(p =>
                {
                    _taskManager.UpdateProgress(taskInfo.Id, p.Percentage, p.CurrentFile);
                });
                await _archiveService.ExtractAsync(entry.FullPath, destDir, progress, taskInfo.Cts.Token);
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

    public async Task OpenArchiveEntryAsync(
        FileSystemEntry entry,
        IArchiveService archiveService,
        IApplicationLauncherService launcherService,
        Action<string> setStatus)
    {
        if (archiveService == null || launcherService == null) return;
        try
        {
            var (archPath, entryKey) = ArchivePathHelper.Parse(entry.FullPath);
            var tempFile = await archiveService.ExtractEntryToTempAsync(archPath, entryKey);
            await launcherService.OpenFileAsync(tempFile);
        }
        catch (Exception ex)
        {
            setStatus($"打开文件失败: {ex.Message}");
        }
    }

    public async Task<string?> ExtractEntryToTempAsync(string archivePath, string entryKey)
    {
        if (_archiveService == null) return null;
        return await _archiveService.ExtractEntryToTempAsync(archivePath, entryKey);
    }

    public void ShowCompressDialog(
        IReadOnlyList<FileSystemEntry> selectedEntries,
        FileSystemEntry? contextMenuEntry,
        string currentPath,
        bool isCollectionView,
        bool isArchiveView,
        int? currentCollectionId)
    {
        var sources = selectedEntries.Count > 0
            ? selectedEntries.Select(e => e.FullPath).ToList()
            : contextMenuEntry != null ? new List<string> { contextMenuEntry.FullPath } : new List<string>();
        if (sources.Count == 0) return;

        // Sentinel paths (collection/archive) are not real dirs — use first file's parent
        var outputDir = (isCollectionView || isArchiveView)
            ? Path.GetDirectoryName(sources[0]) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : currentPath;

        var defaultName = sources.Count == 1
            ? Path.GetFileNameWithoutExtension(sources[0])
            : Path.GetFileName(outputDir) ?? "archive";

        PendingCompressOptions = new CompressOptions
        {
            ArchiveName = defaultName,
            OutputDirectory = outputDir,
            SourcePaths = sources,
            CollectionId = isCollectionView ? currentCollectionId : null
        };
        IsCompressDialogVisible = true;
    }

    public void ConfirmCompress(
        CompressOptions options,
        ICollectionService? collectionService,
        Func<Task> refreshCallback,
        Action<string?> setActiveTaskId,
        Action<string> setStatus)
    {
        IsCompressDialogVisible = false;
        PendingCompressOptions = null;
        if (_archiveService == null || _taskManager == null) return;

        var collectionId = options.CollectionId;

        var taskInfo = _taskManager.AddTask("正在压缩...", async () =>
        {
            if (collectionId != null && collectionService != null)
            {
                var ext = options.Format switch
                {
                    ArchiveFormat.Zip => ".zip",
                    ArchiveFormat.TarGz => ".tar.gz",
                    ArchiveFormat.TarBz2 => ".tar.bz2",
                    _ => ".zip"
                };
                var outputPath = Path.Combine(options.OutputDirectory, options.ArchiveName + ext);
                await collectionService.AddFileToCollectionAsync(collectionId.Value, outputPath);
            }
            await refreshCallback();
        });
        setActiveTaskId(taskInfo.Id);

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<ArchiveProgress>(p =>
                {
                    _taskManager.UpdateProgress(taskInfo.Id, p.Percentage, p.CurrentFile);
                });
                await _archiveService.CompressAsync(options, progress, taskInfo.Cts.Token);
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

    public void MinimizeActiveTask()
    {
        if (ActiveTaskId != null)
        {
            _taskManager?.MinimizeTask(ActiveTaskId);
            ActiveTaskId = null;
        }
    }

    public void CancelCompressDialog()
    {
        IsCompressDialogVisible = false;
        PendingCompressOptions = null;
    }

    public void Reset()
    {
        // Archive view state (IsArchiveView, CurrentArchivePath, CurrentArchiveInternalPath)
        // is now managed exclusively by NavigationViewModel
    }
}