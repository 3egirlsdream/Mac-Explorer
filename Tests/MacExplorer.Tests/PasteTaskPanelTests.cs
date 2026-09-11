using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;
using Xunit;

namespace MacExplorer.Tests;

public sealed class PasteTaskPanelTests
{
    [AvaloniaFact]
    public void TaskPanelDisplaysCopyProgressCancellationAndFailureDetails()
    {
        var manager = new BackgroundTaskManager();
        var panel = new TaskPanel();
        panel.SetTaskManager(manager);
        panel.Show();
        try
        {
            var task = manager.AddTask("粘贴：复制 1 项", BackgroundTaskKind.Copy);
            manager.UpdateProgress(task.Id, 37, "/tmp/project/.git/objects/example");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(37, Assert.Single(panel.GetVisualDescendants().OfType<ProgressBar>()).Value);
            Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "37%");

            manager.CancelTask(task.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(panel.GetVisualDescendants().OfType<ProgressBar>());
            Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "已取消");

            var failed = manager.AddTask("粘贴：复制 1 项", BackgroundTaskKind.Copy);
            const string details = "/tmp/project/unreadable: permission denied";
            manager.FailTask(failed.Id, "复制未完整完成，1 项失败", details);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(panel.GetVisualDescendants().OfType<Border>(), row => Equals(ToolTip.GetTip(row), details));
        }
        finally { panel.Close(); }
    }
}
