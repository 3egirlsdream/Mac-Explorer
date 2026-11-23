using MacExplorer.Models;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileTasks")]
public sealed class TaskProgressSummaryTests
{
    [Fact]
    public void WeightsTasksEquallyAndExcludesTerminalHistory()
    {
        var tasks = new[]
        {
            Task(BackgroundTaskState.Running, 20, 1000), Task(BackgroundTaskState.Running, 80, 10),
            Task(BackgroundTaskState.Paused, 40), Task(BackgroundTaskState.Waiting, 0),
            Task(BackgroundTaskState.NeedsAttention, 60), Task(BackgroundTaskState.Completed, 100),
            Task(BackgroundTaskState.Failed, 99), Task(BackgroundTaskState.Cancelled, 75)
        };
        var summary = TaskProgressSummary.FromTasks(tasks);
        Assert.Equal(40, summary.Value);
        Assert.Equal(5, summary.ActiveCount);
        Assert.Equal(1, summary.PausedCount);
        Assert.Equal(1, summary.WaitingCount);
        Assert.Equal(1, summary.AttentionCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(1, summary.CancelledCount);
        Assert.True(summary.IsAnimating);
        Assert.Equal(TaskProgressAppearance.Running, summary.Appearance);
    }

    [Theory]
    [InlineData(BackgroundTaskState.Running, true)]
    [InlineData(BackgroundTaskState.Pausing, true)]
    [InlineData(BackgroundTaskState.Cancelling, true)]
    [InlineData(BackgroundTaskState.Paused, false)]
    [InlineData(BackgroundTaskState.Waiting, false)]
    [InlineData(BackgroundTaskState.NeedsAttention, false)]
    public void UnknownProgressNeverBecomesAPercentage(BackgroundTaskState state, bool animate)
    {
        var unknown = Task(state, 35); unknown.IsIndeterminate = true;
        var summary = TaskProgressSummary.FromTasks([unknown, Task(BackgroundTaskState.Completed, 100)]);
        Assert.True(summary.IsIndeterminate);
        Assert.Equal(animate, summary.IsAnimating);
        unknown.IsIndeterminate = false;
        Assert.False(TaskProgressSummary.FromTasks([unknown]).IsIndeterminate);
    }

    [Fact]
    public void AddCompletePauseResumeAndRetryUseTheCurrentSet()
    {
        var first = Task(BackgroundTaskState.Running, 80);
        var second = Task(BackgroundTaskState.Waiting, 0);
        Assert.Equal(80, TaskProgressSummary.FromTasks([first]).Value);
        Assert.Equal(40, TaskProgressSummary.FromTasks([first, second]).Value);
        first.State = BackgroundTaskState.Completed;
        Assert.Equal(0, TaskProgressSummary.FromTasks([first, second]).Value);
        second.Progress = 25; second.State = BackgroundTaskState.Paused;
        var paused = TaskProgressSummary.FromTasks([first, second]);
        Assert.Equal(25, paused.Value); Assert.False(paused.IsAnimating);
        second.State = BackgroundTaskState.Running;
        Assert.True(TaskProgressSummary.FromTasks([first, second]).IsAnimating);
        second.State = BackgroundTaskState.Failed;
        Assert.Equal(TaskProgressAppearance.Failed, TaskProgressSummary.FromTasks([first, second]).Appearance);
        second.State = BackgroundTaskState.Waiting; second.Progress = 0;
        Assert.Equal(0, TaskProgressSummary.FromTasks([first, second]).Value);
    }

    [Fact]
    public void EmptySuccessCancellationAndFailureHaveDistinctTerminalStates()
    {
        Assert.Equal(0, TaskProgressSummary.FromTasks([]).TaskCount);
        var completed = Task(BackgroundTaskState.Completed, 100);
        var cancelled = Task(BackgroundTaskState.Cancelled, 30);
        var failed = Task(BackgroundTaskState.Failed, 10);
        Assert.Equal(TaskProgressAppearance.Completed, TaskProgressSummary.FromTasks([completed]).Appearance);
        Assert.Equal(TaskProgressAppearance.Muted, TaskProgressSummary.FromTasks([completed, cancelled]).Appearance);
        var error = TaskProgressSummary.FromTasks([completed, failed, cancelled]);
        Assert.Equal(TaskProgressAppearance.Failed, error.Appearance);
        Assert.False(error.IsAnimating);
        Assert.Equal(100, error.Value);
    }

    [Fact]
    public void InvalidValuesAreClampedOrReportedAsUnknown()
    {
        Assert.Equal(50, TaskProgressSummary.FromTasks([Task(BackgroundTaskState.Running, -5), Task(BackgroundTaskState.Running, 120)]).Value);
        Assert.True(TaskProgressSummary.FromTasks([Task(BackgroundTaskState.Running, double.NaN)]).IsIndeterminate);
    }

    private static BackgroundTaskInfo Task(BackgroundTaskState state, double progress, long totalBytes = 0)
        => new() { State = state, Progress = progress, TotalBytes = totalBytes };
}
