using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.ViewModels;

public partial class ArchiveViewModel : ObservableObject
{
    private readonly IArchiveService? _archiveService;
    private readonly IBackgroundTaskManager? _taskManager;

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
            var entries = await ArchivePasswordAccess.ReadAsync(_archiveService, archivePath,
                password => _archiveService.GetArchiveContentsAsync(archivePath, internalPath, password),
                promptPassword, browse: true, checkEncryption: true);

            setCurrentPath(ArchivePathHelper.Build(archivePath, internalPath));
            updateBreadcrumbs();
            applyEntries(new ObservableCollection<FileSystemEntry>(entries));
            return true;
        }
        catch (OperationCanceledException)
        {
            setStatus(LocalizationText.Get("archive.password_cancelled"));
            return false;
        }
        catch (Exception ex) when (ArchivePasswordAccess.IsPasswordError(ex))
        {
            setStatus(LocalizationText.Get("archive.wrong_password"));
            return false;
        }
        catch (Exception ex)
        {
            setStatus(LocalizationText.Get("archive.open_failed", ex.Message));
            return false;
        }
        finally { setLoading(false); }
    }

    public Task ExtractHereAsync(FileSystemEntry entry, string currentPath, Action<string> setStatus,
        Func<Task> refreshCallback, Func<Task<string?>> promptPassword)
        => QueueExtraction(entry.FullPath, new(Path.GetDirectoryName(entry.FullPath) ?? currentPath,
            ArchiveExtractionMode.CurrentFolder), refreshCallback, promptPassword);

    public Task ExtractToNamedFolderAsync(FileSystemEntry entry, string currentPath, Action<string> setStatus,
        Func<Task> refreshCallback, Func<Task<string?>> promptPassword)
        => QueueExtraction(entry.FullPath, new(Path.GetDirectoryName(entry.FullPath) ?? currentPath,
            ArchiveExtractionMode.NamedFolder), refreshCallback, promptPassword);

    internal Task QueueExtraction(string path, ArchiveExtractionOptions options, Func<Task> refreshCallback,
        Func<Task<string?>> promptPassword, string? displayName = null)
    {
        if (_archiveService == null || _taskManager == null) return Task.CompletedTask;
        _ = ObserveArchiveTaskAsync(FileTaskContext.RunAsync(_taskManager,
            new(BackgroundTaskKind.Extract, LocalizationText.Get("正在解压..."), [path], options.Directory, displayName), async context =>
            {
                var progress = new Progress<ArchiveProgress>(p =>
                    _taskManager.UpdateProgress(context.Task.Id, p.Percentage, p.CurrentFile));
                try
                {
                    return await ArchivePasswordAccess.ReadAsync(_archiveService, path, async password =>
                    {
                        await _archiveService.ExtractAsync(path, options, progress, context.CancellationToken, password);
                        return true;
                    }, async () =>
                    {
                        _taskManager.UpdateProgress(context.Task.Id, context.Task.Progress, LocalizationText.Get("archive.wait_password"));
                        return await promptPassword();
                    }, context.CancellationToken);
                }
                finally { await RunOnUiThreadAsync(refreshCallback); }
            }));
        return Task.CompletedTask;
    }

    internal Task ExportClipboardEntriesAsync(string path, ArchiveExtractionOptions options)
    {
        var request = new FileTaskRequest(BackgroundTaskKind.Extract, LocalizationText.Get("拷贝"), [path], options.Directory)
        {
            AllowRetry = false
        };
        return FileTaskContext.RunAsync(_taskManager, request, async context =>
        {
            var progress = new Progress<ArchiveProgress>(value =>
                _taskManager?.UpdateProgress(context.Task.Id, value.Percentage, value.CurrentFile));
            await _archiveService!.ExtractAsync(path, options, progress, context.CancellationToken);
            return true;
        });
    }

    internal Task<T> RunZipMutationAsync<T>(string path, Func<CancellationToken, Action<double>, Task<T>> mutate,
        CancellationToken token = default, IReadOnlyList<string>? sources = null)
    {
        var operation = new FileOperationDisplay(FileOperationPresentation.ArchiveUpdate);
        var request = new FileTaskRequest(BackgroundTaskKind.Generic, LocalizationText.Get("operation.ArchiveUpdate"), sources ?? [], path)
        {
            Operation = operation,
            AllowRetry = false
        };
        return FileTaskContext.RunAsync(_taskManager, request, context => mutate(context.CancellationToken, progress =>
        {
            operation.Preparing = false;
            _taskManager?.UpdateProgress(context.Task.Id, progress * 100, Path.GetFileName(path));
        }), token);
    }

    public async Task<string?> ExtractEntryToTempWithPasswordAsync(string archivePath, string entryKey,
        Func<Task<string?>> promptPassword)
    {
        if (_archiveService == null) return null;
        try
        {
            return await ArchivePasswordAccess.ReadAsync(_archiveService, archivePath,
                password => _archiveService.ExtractEntryToTempAsync(archivePath, entryKey, password), promptPassword);
        }
        catch (OperationCanceledException) { return null; }
    }

    public void ShowCompressDialog(
        IReadOnlyList<FileSystemEntry> selectedEntries,
        FileSystemEntry? contextMenuEntry,
        FileTag? currentTag, ArchiveFormat defaultFormat = ArchiveFormat.Zip)
    {
        var sources = selectedEntries.Count > 0
            ? selectedEntries.Select(e => e.FullPath).ToList()
            : contextMenuEntry != null ? new List<string> { contextMenuEntry.FullPath } : new List<string>();
        if (sources.Count == 0 || sources.Any(path => !Path.IsPathFullyQualified(path))
            || Path.GetDirectoryName(sources[0]) is not { } outputDir) return;

        var defaultName = sources.Count == 1
            ? Path.GetFileNameWithoutExtension(sources[0])
            : Path.GetFileName(outputDir) ?? "archive";

        PendingCompressOptions = new CompressOptions
        {
            Format = defaultFormat,
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
