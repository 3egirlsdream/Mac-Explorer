using MacExplorer.Models;

namespace MacExplorer.Services;

/// <summary>Shared execution path for the batch rename dialog and Copilot.</summary>
public sealed class BatchRenameOperationService(
    IBatchRenameService rename,
    IFileOperationHistoryService history,
    IBackgroundTaskManager tasks)
{
    public async Task<BatchRenameResult> ExecuteAsync(
        List<BatchRenamePreviewItem> preview, CancellationToken cancellationToken = default)
    {
        var task = tasks.AddTask($"批量重命名 {preview.Count} 项", BackgroundTaskKind.BatchRename);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.Cts.Token, cancellationToken);
        var progress = new Progress<BatchRenameProgress>(update => tasks.UpdateProgress(
            task.Id, update.Percent, update.CurrentPath,
            $"批量重命名 {update.CompletedCount}/{update.TotalCount}"));
        try
        {
            var result = await rename.ExecuteAsync(preview, progress, linked.Token);
            foreach (var item in result.SuccessfulItems)
                await history.RecordRenameAsync(item.OriginalPath, item.NewPath);
            if (result.FailedCount > 0)
                tasks.FailTask(task.Id, $"成功 {result.SuccessCount}，失败 {result.FailedCount}",
                    string.Join(Environment.NewLine, result.Errors));
            else tasks.CompleteTask(task.Id);
            return result;
        }
        catch (OperationCanceledException)
        {
            tasks.CancelTask(task.Id);
            throw;
        }
        catch (Exception ex)
        {
            tasks.FailTask(task.Id, "批量重命名失败", ex.Message);
            throw;
        }
    }
}
