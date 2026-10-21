using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileTasks")]
public sealed class FileTaskQueueTests
{
    [Fact]
    public async Task CompletedTaskKeepsItsSummaryWithoutRetainingCapturedDataOrCheckpoints()
    {
        var manager = new BackgroundTaskManager();
        var (references, context) = CompleteWithCapturedData(manager);
        for (var i = 0; i < 10 && references.Any(reference => reference.IsAlive); i++)
        {
            await Task.Delay(1);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        var summary = Assert.Single(manager.Tasks);
        Assert.Same(summary, context.Task);
        Assert.Equal(BackgroundTaskState.Completed, summary.State);
        Assert.False(summary.CanRetry);
        Assert.Single(summary.Items);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference[] References, FileTaskContext Context) CompleteWithCapturedData(BackgroundTaskManager manager)
    {
        var capture = new byte[1024 * 1024];
        var checkpoint = new byte[1024 * 1024];
        var result = new byte[1024 * 1024];
        FileTaskContext? retained = null;
        manager.RunAsync(Request("/tmp/task-tests/retention"), context =>
        {
            retained = context;
            context.Remember("state", () => checkpoint);
            context.ReportItem("source", "destination", FileTaskItemState.Completed);
            GC.KeepAlive(capture);
            return Task.FromResult(result);
        }).GetAwaiter().GetResult();
        return ([new(capture), new(checkpoint), new(result)], retained!);
    }

    [Fact]
    public void FinishedTaskSummariesHaveABoundWhileFailuresRemainActionable()
    {
        var manager = new BackgroundTaskManager();
        var failed = manager.AddTask("failed", BackgroundTaskKind.Copy, retryAction: () => Task.CompletedTask);
        manager.FailTask(failed.Id, "retry me");
        for (var i = 0; i < BackgroundTaskManager.CompletedTaskLimit + 10; i++)
            manager.CompleteTask(manager.AddTask("completed").Id);
        Assert.Equal(BackgroundTaskManager.CompletedTaskLimit + 1, manager.Tasks.Count);
        Assert.Contains(failed, manager.Tasks);
        Assert.True(failed.CanRetry);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(8));
    private static FileTaskRequest Request(string path) => new(BackgroundTaskKind.Copy, "copy", [path]);

    [Fact]
    public async Task ConcurrencyAndPauseReleaseSlotsWhileRetainingPathClaims()
    {
        var manager = new BackgroundTaskManager { MaxConcurrency = 1 };
        var entered = Signal(); var checkpoint = Signal(); var resumed = Signal(); var end = Signal();
        var first = manager.RunAsync(Request("/tmp/task-tests/a"), async context =>
        {
            entered.TrySetResult(); await checkpoint.Task; await context.CheckpointAsync();
            resumed.TrySetResult(); await end.Task; return 1;
        });
        await Await(entered.Task);
        var id = manager.Tasks[0].Id;
        var conflictEntered = Signal(); var independentEntered = Signal(); var independentEnd = Signal();
        var conflict = manager.RunAsync(Request("/tmp/task-tests/a/child"), _ => { conflictEntered.TrySetResult(); return Task.FromResult(2); });
        var independent = manager.RunAsync(Request("/tmp/task-tests/b"), async _ =>
        { independentEntered.TrySetResult(); await independentEnd.Task; return 3; });
        Assert.Equal(2, manager.Tasks.Count(t => t.State == BackgroundTaskState.Waiting));
        manager.PauseTask(id); checkpoint.TrySetResult();
        await Await(independentEntered.Task);
        Assert.Equal(BackgroundTaskState.Paused, manager.Tasks[0].State);
        Assert.False(conflictEntered.Task.IsCompleted);
        manager.ResumeTask(id);
        Assert.False(resumed.Task.IsCompleted);
        independentEnd.TrySetResult(); await Await(independent);
        await Await(resumed.Task); end.TrySetResult();
        await Await(Task.WhenAll(first, conflict));
        Assert.True(conflictEntered.Task.IsCompleted);
        Assert.All(manager.Tasks, t => Assert.Equal(BackgroundTaskState.Completed, t.State));
    }

    [Fact]
    public async Task CancelRunningWaitsForAcknowledgementAndCannotRemoveActiveTask()
    {
        var manager = new BackgroundTaskManager(); var entered = Signal(); var commit = Signal();
        var operation = manager.RunAsync(Request("/tmp/task-tests/c"), async context =>
        { entered.TrySetResult(); await commit.Task; await context.CheckpointAsync(); return true; });
        await Await(entered.Task); var task = Assert.Single(manager.Tasks);
        manager.CancelTask(task.Id); manager.RemoveTask(task.Id);
        Assert.Equal(BackgroundTaskState.Cancelling, task.State); Assert.Single(manager.Tasks);
        commit.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(operation));
        Assert.Equal(BackgroundTaskState.Cancelled, task.State);
        manager.RemoveTask(task.Id); Assert.Empty(manager.Tasks);
    }

    [Fact]
    public async Task RetrySkipsCommittedStepsAndKeepsTheSameTask()
    {
        var manager = new BackgroundTaskManager(); var commits = 0; var attempts = 0;
        var operation = manager.RunAsync(Request("/tmp/task-tests/retry"), async context =>
        {
            await context.StepAsync("one", () => Task.FromResult(++commits));
            if (++attempts == 1) throw new IOException("offline");
            return true;
        });
        await Assert.ThrowsAsync<IOException>(() => operation);
        var task = Assert.Single(manager.Tasks); Assert.Equal(BackgroundTaskState.NeedsAttention, task.State);
        manager.RetryTask(task.Id); await Await(manager.WaitForCompletionAsync(task.Id));
        Assert.Equal(1, commits); Assert.Equal(2, attempts); Assert.Single(manager.Tasks);
        Assert.Equal(BackgroundTaskState.Completed, task.State);
    }

    [Fact]
    public async Task ShutdownCancelsPausedAndWaitingTasksAndANewInstanceStartsEmpty()
    {
        var manager = new BackgroundTaskManager { MaxConcurrency = 1 };
        var entered = Signal(); var release = Signal();
        var first = manager.RunAsync(Request("/tmp/task-tests/shutdown"), async context =>
        { entered.TrySetResult(); await release.Task; await context.CheckpointAsync(); return 1; });
        await Await(entered.Task);
        var second = manager.RunAsync(Request("/tmp/task-tests/shutdown/child"), _ => Task.FromResult(2));
        var shutdown = manager.ShutdownAsync(); release.TrySetResult(); await Await(shutdown);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.All(manager.Tasks, t => Assert.Equal(BackgroundTaskState.Cancelled, t.State));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RunAsync(Request("/tmp/new"), _ => Task.FromResult(1)));
        Assert.Empty(new BackgroundTaskManager().Tasks);
    }

    [Fact]
    public async Task QueueReorderingAndLoweringConcurrencyDoNotInterruptRunningWork()
    {
        var manager = new BackgroundTaskManager { MaxConcurrency = 1 }; var started = Signal(); var release = Signal();
        var first = manager.RunAsync(Request("/tmp/task-tests/first"), async _ => { started.TrySetResult(); await release.Task; return 0; });
        await Await(started.Task); var order = new List<int>();
        var second = manager.RunAsync(Request("/tmp/task-tests/second"), _ => { order.Add(2); return Task.FromResult(2); });
        var third = manager.RunAsync(Request("/tmp/task-tests/third"), _ => { order.Add(3); return Task.FromResult(3); });
        manager.MoveTask(manager.Tasks[2].Id, -1); release.TrySetResult();
        await Await(Task.WhenAll(first, second, third)); Assert.Equal(new[] { 3, 2 }, order);
    }
}
