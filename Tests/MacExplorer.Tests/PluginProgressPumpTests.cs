using MacExplorer.PluginSdk;
using MacExplorer.Services.Plugins;
using Xunit;

namespace MacExplorer.Tests;

public sealed class PluginProgressPumpTests
{
    [Fact]
    public void BurstsQueueOnlyOneCallbackAndKeepTheTaskPanelRequest()
    {
        var queue = new Queue<Action>();
        var applied = new List<PluginProgress>();
        using var pump = new PluginProgressPump(queue.Enqueue, applied.Add);
        pump.Report(new("start", 0) { ShowInTaskPanel = true, TaskTitle = "conversion" });
        for (var index = 1; index <= 10_000; index++) pump.Report(new("step " + index, index / 100d));
        Assert.Single(queue);
        queue.Dequeue()();
        var progress = Assert.Single(applied);
        Assert.Equal("step 10000", progress.Message);
        Assert.Equal(100d, progress.Percent);
        Assert.True(progress.ShowInTaskPanel);
        Assert.Equal("conversion", progress.TaskTitle);
    }

    [Fact]
    public void DisposedRunCannotCreateALateTaskOrChangeItsStatus()
    {
        var queue = new Queue<Action>();
        var applied = new List<PluginProgress>();
        var pump = new PluginProgressPump(queue.Enqueue, applied.Add);
        pump.Report(new("late", 50) { ShowInTaskPanel = true });
        pump.Dispose();
        queue.Dequeue()();
        pump.Report(new("too late", 99));
        Assert.Empty(applied);
        Assert.Empty(queue);
    }

    [Fact]
    public void CompletionFlushesOnceThenRejectsQueuedAndFutureReports()
    {
        var queue = new Queue<Action>();
        var applied = new List<PluginProgress>();
        using var pump = new PluginProgressPump(queue.Enqueue, applied.Add);
        pump.Report(new("done", 100) { ShowInTaskPanel = true });
        pump.Complete();
        queue.Dequeue()();
        pump.Report(new("late", 0));
        Assert.Equal("done", Assert.Single(applied).Message);
        Assert.Empty(queue);
    }
}
