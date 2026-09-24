using MacExplorer.Models;
using MacExplorer.ViewModels;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services;

/// <summary>One complete move/delete operation, including app metadata and refresh notifications.</summary>
public sealed class FileOperationService(
    IFileService files, IDirectoryChangeNotifier? changes,
    IAiTagService? analysis, IFileTagService? tags,
    IFileOperationHistoryService? history, IBackgroundTaskManager? tasks,
    ILogger? logger)
{
    public async Task<IReadOnlyList<string>> CopyAsync(
        IReadOnlyList<string> sourcePaths, string destinationDirectory)
    {
        if (sourcePaths.Count == 0) return [];
        var trackProgress = !VirtualPath.IsRemotePath(destinationDirectory)
            && sourcePaths.All(path => !VirtualPath.IsRemotePath(path));
        var task = trackProgress ? tasks?.AddTask($"粘贴：复制 {sourcePaths.Count} 项", BackgroundTaskKind.Copy) : null;
        var token = task?.Cts.Token ?? CancellationToken.None;
        var outputs = new List<string>(sourcePaths.Count);
        try
        {
            var skippedCount = 0;
            for (var index = 0; index < sourcePaths.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var itemIndex = index;
                var itemSkipped = 0;
                var progress = task == null ? null : new FileProgress(value =>
                {
                    itemSkipped = value.SkippedCount;
                    var skipped = skippedCount + itemSkipped;
                    tasks!.UpdateProgress(task.Id,
                        (itemIndex * 100d + value.Percentage) / sourcePaths.Count, value.CurrentFile,
                        skipped == 0 ? null : $"粘贴：复制 {sourcePaths.Count} 项（已跳过 {skipped} 个运行时通信文件）");
                });
                var destination = await files.CopyWithProgressAsync(sourcePaths[index],
                    destinationDirectory, progress, token);
                outputs.Add(destination);
                skippedCount += itemSkipped;
                if (tags != null && !VirtualPath.IsRemotePath(destinationDirectory))
                    await tags.CopyPathAsync(sourcePaths[index], destination, token);
            }
            token.ThrowIfCancellationRequested();
            if (task != null) tasks!.CompleteTask(task.Id);
            return outputs;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (task != null) tasks!.CancelTask(task.Id);
            throw;
        }
        catch (Exception ex)
        {
            if (task != null)
                tasks!.FailTask(task.Id,
                    ex is AggregateException aggregate ? $"复制未完整完成，{aggregate.InnerExceptions.Count} 项失败" : ex.Message,
                    ex.ToString());
            logger?.LogError(ex, "Copy failed");
            throw;
        }
        finally { changes?.NotifyChanged([destinationDirectory], null); }
    }

    private sealed class FileProgress(Action<FileOperationProgress> report) : IProgress<FileOperationProgress>
    {
        public void Report(FileOperationProgress value) => report(value);
    }

    public async Task DeleteToTrashAsync(IReadOnlyList<FileSystemEntry> entries,
        string currentPath, Action<string>? setStatus = null,
        FileListViewModel? refreshedViewModel = null)
    {
        var paths = entries.Select(entry => entry.FullPath).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return;
        var deletedPaths = new List<string>(paths.Length);
        var completed = false;
        var metadataFailures = 0;
        try
        {
            foreach (var path in paths)
            {
                await files.DeleteAsync(path, moveToTrash: true);
                deletedPaths.Add(path);
                if (history != null)
                {
                    try { await history.RecordTrashAsync(path, ""); }
                    catch (Exception ex)
                    {
                        metadataFailures++;
                        logger?.LogError(ex, "Failed to record trash history for {Path}", path);
                    }
                }
            }
            completed = true;
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"删除未全部完成（已删除 {deletedPaths.Count}/{paths.Length} 项）: {ex.Message}");
            throw;
        }
        finally
        {
            if (deletedPaths.Count > 0 && analysis != null)
            {
                try { await analysis.DeleteAnalysisForFilesAsync(deletedPaths); }
                catch (Exception ex)
                {
                    metadataFailures++;
                    logger?.LogError(ex, "Failed to delete AI analysis data for {Count} files", deletedPaths.Count);
                }
            }
            if (tags != null)
            {
                foreach (var path in deletedPaths)
                {
                    try { await tags.DeletePathAsync(path); }
                    catch (Exception ex)
                    {
                        metadataFailures++;
                        logger?.LogError(ex, "Failed to delete tags for {Path}", path);
                    }
                }
            }
            var parentDirs = paths.Select(Path.GetDirectoryName).OfType<string>()
                .Distinct(StringComparer.Ordinal).ToArray();
            changes?.NotifyChanged(parentDirs.Length > 0 ? parentDirs : [currentPath],
                completed ? refreshedViewModel : null);
        }
        if (metadataFailures > 0)
            setStatus?.Invoke($"已删除 {deletedPaths.Count} 项，但有 {metadataFailures} 项历史或标签更新失败。");
    }

    public async Task MoveAsync(FileSystemEntry source, FileSystemEntry targetFolder,
        Action<string>? setStatus = null)
    {
        if (!targetFolder.IsDirectory) return;
        try
        {
            await files.MoveAsync(source.FullPath, targetFolder.FullPath);
            var movedPath = Path.Combine(targetFolder.FullPath, Path.GetFileName(source.FullPath));
            if (tags != null) await tags.UpdatePathAsync(source.FullPath, movedPath);
            if (history != null) await history.RecordMoveAsync(source.FullPath, movedPath);
        }
        catch (Exception ex)
        {
            setStatus?.Invoke($"移动失败: {ex.Message}");
            throw;
        }
        finally
        {
            changes?.NotifyChanged([Path.GetDirectoryName(source.FullPath) ?? "", targetFolder.FullPath], null);
        }
    }
}
