using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.Views.Dialogs;

public partial class TaskPanel : ToolWindow
{
    private IBackgroundTaskManager? _taskManager;

    public TaskPanel()
    {
        InitializeComponent();
    }

    public void SetTaskManager(IBackgroundTaskManager taskManager)
    {
        _taskManager = taskManager;
        _taskManager.TasksChanged += OnTasksChanged;
        RefreshTasks();
    }

    private void OnTasksChanged()
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(RefreshTasks);
    }

    private void RefreshTasks()
    {
        if (_taskManager == null) return;

        TaskItemsPanel.Children.Clear();
        var tasks = _taskManager.Tasks.ToList();
        var runningCount = tasks.Count(t => t.State == BackgroundTaskState.Running);
        var completedCount = tasks.Count(t => t.State == BackgroundTaskState.Completed);
        var failedCount = tasks.Count(t => t.State == BackgroundTaskState.Failed);

        RunningCountText.Text = runningCount.ToString();
        CompletedCountText.Text = completedCount.ToString();
        FailedCountText.Text = failedCount.ToString();
        FailedStat.Opacity = failedCount > 0 ? 1 : 0.55;
        ClearAllButton.IsEnabled = tasks.Count > 0;
        ClearCompletedButton.IsEnabled = completedCount + failedCount > 0;
        TaskSummaryText.Text = BuildSummary(tasks.Count, runningCount, failedCount);

        if (tasks.Count == 0)
        {
            EmptyPlaceholder.IsVisible = true;
            TaskListScroll.IsVisible = false;
            return;
        }

        EmptyPlaceholder.IsVisible = false;
        TaskListScroll.IsVisible = true;

        foreach (var task in tasks.OrderBy(GetTaskSortOrder))
        {
            TaskItemsPanel.Children.Add(CreateTaskRow(task));
        }
    }

    private static string BuildSummary(int totalCount, int runningCount, int failedCount)
    {
        if (totalCount == 0) return "没有正在运行的任务";
        if (failedCount > 0) return $"{totalCount} 个任务，{failedCount} 个需要处理";
        if (runningCount > 0) return $"{totalCount} 个任务，{runningCount} 个正在运行";
        return $"{totalCount} 个任务已完成";
    }

    private static int GetTaskSortOrder(BackgroundTaskInfo task) =>
        task.State switch
        {
            BackgroundTaskState.Running => 0,
            BackgroundTaskState.Failed => 1,
            BackgroundTaskState.Completed => 2,
            _ => 3
        };

    private Control CreateTaskRow(BackgroundTaskInfo task)
    {
        var row = new Border();
        row.Classes.Add("task-row");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 8
        };

        var dot = new Border();
        dot.Classes.Add("task-dot");
        dot.Classes.Add(GetStateClass(task.State));
        Grid.SetColumn(dot, 0);
        Grid.SetRow(dot, 0);
        Grid.SetRowSpan(dot, 2);
        grid.Children.Add(dot);

        var title = new TextBlock
        {
            Text = task.Label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Classes.Add("task-title");
        Grid.SetColumn(title, 1);
        Grid.SetRow(title, 0);
        grid.Children.Add(title);

        var details = new TextBlock
        {
            Text = BuildDetailText(task),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        details.Classes.Add("task-meta");
        Grid.SetColumn(details, 1);
        Grid.SetRow(details, 1);
        grid.Children.Add(details);

        var trailing = CreateTrailingControls(task);
        Grid.SetColumn(trailing, 2);
        Grid.SetRow(trailing, 0);
        Grid.SetRowSpan(trailing, 2);
        grid.Children.Add(trailing);

        if (task.State == BackgroundTaskState.Running)
        {
            var progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = task.Progress
            };
            progress.Classes.Add("task-progress");
            Grid.SetColumn(progress, 1);
            Grid.SetColumnSpan(progress, 2);
            Grid.SetRow(progress, 2);
            grid.Children.Add(progress);
        }

        row.Child = grid;
        return row;
    }

    private Control CreateTrailingControls(BackgroundTaskInfo task)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (task.State == BackgroundTaskState.Running)
        {
            var progressText = new TextBlock
            {
                Text = $"{task.Progress:F0}%",
                MinWidth = 38,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            progressText.Classes.Add("task-progress-text");
            panel.Children.Add(progressText);

            panel.Children.Add(CreateIconButton(AppIcons.Close, "取消任务", () =>
            {
                task.Cts.Cancel();
                _taskManager?.RemoveTask(task.Id);
            }));
        }
        else
        {
            var stateText = new TextBlock
            {
                Text = task.State == BackgroundTaskState.Completed ? "完成" : "失败",
                MinWidth = 34,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            stateText.Classes.Add("task-state-pill");
            panel.Children.Add(stateText);
            panel.Children.Add(CreateIconButton(AppIcons.Close, "移除任务", () => _taskManager?.RemoveTask(task.Id)));
        }

        return panel;
    }

    private static Button CreateIconButton(string iconData, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = new PathIcon
            {
                Data = Geometry.Parse(iconData),
                Width = 12,
                Height = 12
            }
        };
        button.Classes.Add("ghost");
        button.Classes.Add("task-icon-button");
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private static string BuildDetailText(BackgroundTaskInfo task)
    {
        if (task.State == BackgroundTaskState.Failed && !string.IsNullOrWhiteSpace(task.ErrorMessage))
            return task.ErrorMessage;

        if (!string.IsNullOrWhiteSpace(task.CurrentFile))
            return System.IO.Path.GetFileName(task.CurrentFile);

        return task.State switch
        {
            BackgroundTaskState.Running => "正在准备...",
            BackgroundTaskState.Completed => "任务已完成",
            BackgroundTaskState.Failed => "任务失败",
            _ => string.Empty
        };
    }

    private static string GetStateClass(BackgroundTaskState state) =>
        state switch
        {
            BackgroundTaskState.Running => "running",
            BackgroundTaskState.Completed => "completed",
            BackgroundTaskState.Failed => "failed",
            _ => "running"
        };

    private void ClearAllTasks(object? sender, RoutedEventArgs e)
    {
        if (_taskManager == null) return;
        foreach (var task in _taskManager.Tasks.ToList())
        {
            if (task.State == BackgroundTaskState.Running)
                task.Cts.Cancel();
            _taskManager.RemoveTask(task.Id);
        }
    }

    private void ClearCompleted(object? sender, RoutedEventArgs e)
    {
        if (_taskManager == null) return;
        foreach (var task in _taskManager.Tasks
                     .Where(t => t.State != BackgroundTaskState.Running)
                     .ToList())
        {
            _taskManager.RemoveTask(task.Id);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_taskManager != null)
            _taskManager.TasksChanged -= OnTasksChanged;
        base.OnClosed(e);
    }
}
