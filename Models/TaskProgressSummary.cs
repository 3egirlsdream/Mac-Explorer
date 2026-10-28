namespace MacExplorer.Models;

internal enum TaskProgressAppearance { Running, Muted, Attention, Failed, Completed }

/// <summary>Equal task weights: this is neither a byte total nor a time estimate.</summary>
internal readonly record struct TaskProgressSummary(
    int TaskCount, int ActiveCount, int PausedCount, int WaitingCount, int AttentionCount,
    int FailedCount, int CancelledCount, double Value, bool IsIndeterminate,
    bool IsAnimating, TaskProgressAppearance Appearance)
{
    public static TaskProgressSummary FromTasks(IReadOnlyList<BackgroundTaskInfo> tasks)
    {
        var active = 0; var paused = 0; var waiting = 0; var attention = 0;
        var failed = 0; var cancelled = 0; var running = false; var unknown = false;
        var sum = 0d;
        foreach (var task in tasks)
        {
            var state = task.State;
            if (state == BackgroundTaskState.Failed) failed++;
            if (state == BackgroundTaskState.Cancelled) cancelled++;
            if (state is BackgroundTaskState.Completed or BackgroundTaskState.Failed or BackgroundTaskState.Cancelled)
                continue;
            active++;
            if (state is BackgroundTaskState.Paused or BackgroundTaskState.Pausing) paused++;
            if (state == BackgroundTaskState.Waiting) waiting++;
            if (state == BackgroundTaskState.NeedsAttention) attention++;
            running |= state is BackgroundTaskState.Running or BackgroundTaskState.Pausing or BackgroundTaskState.Cancelling;
            var value = task.Progress;
            unknown |= task.IsIndeterminate || !double.IsFinite(value);
            sum += double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
        }
        var appearance = active > 0
            ? running ? TaskProgressAppearance.Running
                : attention > 0 ? TaskProgressAppearance.Attention : TaskProgressAppearance.Muted
            : failed > 0 ? TaskProgressAppearance.Failed
                : tasks.Count > 0 && cancelled == 0 ? TaskProgressAppearance.Completed : TaskProgressAppearance.Muted;
        return new(tasks.Count, active, paused, waiting, attention, failed, cancelled,
            active > 0 ? sum / active : tasks.Count > 0 ? 100 : 0,
            unknown, running, appearance);
    }
}
