using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

internal static class TaskProgressText
{
    public static string Format(TaskProgressSummary summary)
    {
        var lines = new List<string>();
        if (summary.ActiveCount > 0)
            lines.Add(summary.IsIndeterminate
                ? LocalizationText.Get("tasks.progress.unknown", summary.ActiveCount)
                : LocalizationText.Get("tasks.progress.known", summary.ActiveCount, summary.Value));
        else if (summary.Appearance == TaskProgressAppearance.Completed)
            lines.Add(LocalizationText.Get("tasks.progress.completed"));
        else
            lines.Add(LocalizationText.Get("ui.048"));
        if (summary.PausedCount > 0) lines.Add(LocalizationText.Get("tasks.progress.paused", summary.PausedCount));
        if (summary.WaitingCount > 0) lines.Add(LocalizationText.Get("tasks.progress.waiting", summary.WaitingCount));
        if (summary.AttentionCount > 0) lines.Add(LocalizationText.Get("tasks.progress.attention", summary.AttentionCount));
        if (summary.FailedCount > 0) lines.Add(LocalizationText.Get("tasks.progress.failed", summary.FailedCount));
        if (summary.CancelledCount > 0) lines.Add(LocalizationText.Get("tasks.progress.cancelled", summary.CancelledCount));
        return string.Join("\n", lines);
    }
}
