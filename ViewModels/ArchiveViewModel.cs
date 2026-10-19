using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Views.Dialogs;
using SharpCompress.Common;
using CryptographicException = SharpCompress.Common.CryptographicException;

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

    public async Task<bool> NavigateToArchiveAsync(
        string archivePath,
        string internalPath,
        Action<string> setCurrentPath,
        Action updateBreadcrumbs,
        Action<ObservableCollection<FileSystemEntry>> applyEntries,
        Action<string> setStatus,
        Action<bool> setLoading,
        Func<Task<string?>> promptPassword)
    {
        if (_archiveService == null) return false;

        setLoading(true);

        try
        {
            var entries = await Task.Run(() => _archiveService.IsEncrypted(archivePath))
                ? await TryNavigateWithPasswordAsync(archivePath, internalPath, setStatus, promptPassword)
                : await TryNavigateToArchiveAsync(archivePath, internalPath);
            if (entries == null) return false;

            setCurrentPath(ArchivePathHelper.Build(archivePath, internalPath));
            updateBreadcrumbs();
            applyEntries(entries);
            return true;
        }
        catch (CryptographicException)
        {
            var entries = await TryNavigateWithPasswordAsync(archivePath, internalPath, setStatus, promptPassword);
            if (entries == null) return false;

            setCurrentPath(ArchivePathHelper.Build(archivePath, internalPath));
            updateBreadcrumbs();
            applyEntries(entries);
            return true;
        }
        catch (InvalidFormatException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            var entries = await TryNavigateWithPasswordAsync(archivePath, internalPath, setStatus, promptPassword);
            if (entries == null) return false;

            setCurrentPath(ArchivePathHelper.Build(archivePath, internalPath));
            updateBreadcrumbs();
            applyEntries(entries);
            return true;
        }
        catch (Exception ex)
        {
            setStatus($"无法打开归档: {ex.Message}");
            return false;
        }
        finally
        {
            setLoading(false);
        }
    }

    private async Task<ObservableCollection<FileSystemEntry>> TryNavigateToArchiveAsync(
        string archivePath,
        string internalPath)
    {
        var entries = await _archiveService!.GetArchiveContentsAsync(archivePath, internalPath);
        return new ObservableCollection<FileSystemEntry>(entries);
    }

    private async Task<ObservableCollection<FileSystemEntry>?> TryNavigateWithPasswordAsync(
        string archivePath,
        string internalPath,
        Action<string> setStatus,
        Func<Task<string?>> promptPassword)
    {
        var password = await promptPassword();
        if (string.IsNullOrEmpty(password))
        {
            setStatus("需要密码才能打开此归档文件");
            return null;
        }

        try
        {
            var entries = await _archiveService!.GetArchiveContentsAsync(archivePath, internalPath, password);
            return new ObservableCollection<FileSystemEntry>(entries);
        }
        catch (CryptographicException)
        {
            setStatus("密码错误，无法打开归档文件");
            return null;
        }
        catch (InvalidFormatException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            setStatus("密码错误，无法打开归档文件");
            return null;
        }
    }

    public async Task ExtractHereAsync(
        FileSystemEntry entry,
        string currentPath,
        Action<string> setStatus,
        Func<Task> refreshCallback,
        Func<Task<string?>> promptPassword)
    {
        await ExtractAsync(
            entry,
            currentPath,
            createNamedFolder: false,
            setStatus,
            refreshCallback,
            promptPassword);
    }

    public async Task ExtractToNamedFolderAsync(
        FileSystemEntry entry,
        string currentPath,
        Action<string> setStatus,
        Func<Task> refreshCallback,
        Func<Task<string?>> promptPassword)
    {
        await ExtractAsync(
            entry,
            currentPath,
            createNamedFolder: true,
            setStatus,
            refreshCallback,
            promptPassword);
    }

    private Task ExtractAsync(
        FileSystemEntry entry,
        string currentPath,
        bool createNamedFolder,
        Action<string> setStatus,
        Func<Task> refreshCallback,
        Func<Task<string?>> promptPassword)
    {
        if (_archiveService == null || _taskManager == null) return Task.CompletedTask;
        var archPath = ArchivePathHelper.IsArchivePath(entry.FullPath)
            ? ArchivePathHelper.Parse(entry.FullPath).archivePath
            : entry.FullPath;
        var parentDir = Path.GetDirectoryName(archPath) ?? currentPath;

        _ = ObserveArchiveTaskAsync(FileTaskContext.RunAsync(_taskManager,
            new(BackgroundTaskKind.Extract, Services.Impl.LocalizationText.Get("正在解压..."), [archPath], parentDir), async context =>
            {
                var destDir = context.Remember("extract-destination", () => createNamedFolder
                    ? ArchiveExtractionPathHelper.CreateUniqueExtractionDirectory(parentDir, archPath) : parentDir);
                var progress = new Progress<ArchiveProgress>(p =>
                    _taskManager.UpdateProgress(context.Task.Id, p.Percentage, p.CurrentFile));
                try
                {
                    try { await _archiveService.ExtractAsync(archPath, destDir, progress, context.CancellationToken); }
                    catch (CryptographicException)
                    { await HandleExtractPasswordAsync(archPath, destDir, progress, context.Task, promptPassword); }
                    catch (InvalidFormatException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                    { await HandleExtractPasswordAsync(archPath, destDir, progress, context.Task, promptPassword); }
                    return true;
                }
                finally { await RunOnUiThreadAsync(refreshCallback); }
            }));
        return Task.CompletedTask;
    }

    private async Task HandleExtractPasswordAsync(
        string archivePath,
        string destDir,
        IProgress<ArchiveProgress> progress,
        BackgroundTaskInfo taskInfo,
        Func<Task<string?>> promptPassword)
    {
        _taskManager?.UpdateProgress(taskInfo.Id, taskInfo.Progress, "等待输入密码", "需要密码才能解压...");
        var password = await promptPassword();
        if (string.IsNullOrEmpty(password))
            throw new OperationCanceledException("用户取消密码输入");

        await _archiveService!.ExtractAsync(archivePath, destDir, progress, taskInfo.Cts.Token, password);
    }

    public async Task OpenArchiveEntryAsync(
        FileSystemEntry entry,
        IArchiveService archiveService,
        IApplicationLauncherService launcherService,
        Action<string> setStatus,
        Func<Task<string?>> promptPassword)
    {
        if (archiveService == null || launcherService == null) return;
        try
        {
            var (archPath, entryKey) = ArchivePathHelper.Parse(entry.FullPath);

            try
            {
                var tempFile = await archiveService.ExtractEntryToTempAsync(archPath, entryKey);
                await launcherService.OpenFileAsync(tempFile);
            }
            catch (CryptographicException)
            {
                await OpenEntryWithPasswordAsync(archPath, entryKey, archiveService, launcherService, promptPassword);
            }
            catch (InvalidFormatException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                await OpenEntryWithPasswordAsync(archPath, entryKey, archiveService, launcherService, promptPassword);
            }
        }
        catch (Exception ex)
        {
            setStatus($"打开文件失败: {ex.Message}");
        }
    }

    private async Task OpenEntryWithPasswordAsync(
        string archPath,
        string entryKey,
        IArchiveService archiveService,
        IApplicationLauncherService launcherService,
        Func<Task<string?>> promptPassword)
    {
        var password = await promptPassword();
        if (string.IsNullOrEmpty(password)) return;

        var tempFile = await archiveService.ExtractEntryToTempAsync(archPath, entryKey, password);
        await launcherService.OpenFileAsync(tempFile);
    }

    public async Task<string?> ExtractEntryToTempAsync(string archivePath, string entryKey)
    {
        if (_archiveService == null) return null;
        return await _archiveService.ExtractEntryToTempAsync(archivePath, entryKey);
    }

    public async Task<string?> ExtractEntryToTempWithPasswordAsync(
        string archivePath, string entryKey, Func<Task<string?>> promptPassword)
    {
        if (_archiveService == null) return null;
        try
        {
            return await _archiveService.ExtractEntryToTempAsync(archivePath, entryKey);
        }
        catch (CryptographicException)
        {
            var password = await promptPassword();
            if (string.IsNullOrEmpty(password)) return null;
            return await _archiveService.ExtractEntryToTempAsync(archivePath, entryKey, password);
        }
        catch (InvalidFormatException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            var password = await promptPassword();
            if (string.IsNullOrEmpty(password)) return null;
            return await _archiveService.ExtractEntryToTempAsync(archivePath, entryKey, password);
        }
    }

    public void ShowCompressDialog(
        IReadOnlyList<FileSystemEntry> selectedEntries,
        FileSystemEntry? contextMenuEntry,
        string currentPath,
        bool isArchiveView,
        FileTag? currentTag)
    {
        var sources = selectedEntries.Count > 0
            ? selectedEntries.Select(e => e.FullPath).ToList()
            : contextMenuEntry != null ? new List<string> { contextMenuEntry.FullPath } : new List<string>();
        if (sources.Count == 0) return;

        // Sentinel paths (tag/archive) are not real dirs — use first file's parent
        var outputDir = (currentTag != null || isArchiveView)
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
            Tag = currentTag
        };
        IsCompressDialogVisible = true;
    }

    public void ConfirmCompress(
        CompressOptions options,
        IFileTagService? tagService,
        IDirectoryChangeNotifier? directoryChangeNotifier,
        Func<Task> refreshCallback,
        Action<string> setStatus)
    {
        IsCompressDialogVisible = false;
        PendingCompressOptions = null;
        if (_archiveService == null || _taskManager == null) return;

        var tag = options.Tag;

        _ = ObserveArchiveTaskAsync(FileTaskContext.RunAsync(_taskManager,
            new(BackgroundTaskKind.Compress, Services.Impl.LocalizationText.Get("正在压缩..."),
                options.SourcePaths.ToArray(), options.OutputDirectory, options.OutputFileName), async context =>
            {
                var progress = new Progress<ArchiveProgress>(p =>
                    _taskManager.UpdateProgress(context.Task.Id, p.Percentage, p.CurrentFile));
                var actualOutputPath = await context.StepAsync("archive-output", () =>
                    _archiveService.CompressAsync(options, progress, context.CancellationToken));
                if (tag != null && tagService != null)
                    try { await tagService.SetTagAsync([actualOutputPath], tag, true); }
                    catch (Exception ex) { context.Warnings.Add(ex.Message); }
                context.ReportItem(string.Join(";", options.SourcePaths), actualOutputPath, FileTaskItemState.Completed);
                directoryChangeNotifier?.NotifyChanged([options.OutputDirectory], null);
                await RunOnUiThreadAsync(refreshCallback);
                return true;
            }));
    }

    private static async Task ObserveArchiveTaskAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public void CancelCompressDialog()
    {
        IsCompressDialogVisible = false;
        PendingCompressOptions = null;
    }

    public void Reset()
    {
    }

    private static async Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await action();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        await completion.Task;
    }
}
