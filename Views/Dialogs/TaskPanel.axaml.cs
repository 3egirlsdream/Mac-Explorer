using Avalonia.Interactivity;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class TaskPanel : ToolWindow
{
    private IBackgroundTaskManager? _taskManager;
    private IFileOperationHistoryService? _history;
    private FileTaskPanelPresenter? _presenter;
    public TaskPanel() { InitializeComponent(); }
    public void SetTaskManager(IBackgroundTaskManager taskManager)
    {
        if (_taskManager != null) _taskManager.TasksChanged -= OnTasksChanged;
        if (_history != null) _history.HistoryChanged -= OnTasksChanged;
        _taskManager = taskManager;
        _history = App.Services?.GetService<IFileOperationHistoryService>();
        _taskManager.TasksChanged += OnTasksChanged;
        if (_history != null) _history.HistoryChanged += OnTasksChanged;
        _presenter = new(taskManager, _history);
        TaskItemsPanel.Children.Clear(); TaskItemsPanel.Children.Add(_presenter.View);
        RefreshTasks();
    }
    private void OnTasksChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshTasks);
    private void RefreshTasks()
    {
        if (_taskManager == null) return;
        var tasks = _taskManager.Tasks;
        var running = tasks.Count(t => t.State is BackgroundTaskState.Running or BackgroundTaskState.Pausing);
        TaskRunningCountText.Text = running > 0 ? LocalizationText.Get("{0} 项进行中", running) : "";
        TaskRunningCountText.IsVisible = running > 0;
        CancelAllTasksMenuItem.IsEnabled = tasks.Any(t => t.IsActive && t.CanCancel);
        ClearFinishedTasksMenuItem.IsEnabled = tasks.Any(t => t.IsTerminal);
        _presenter?.Refresh();
    }
    private void ClearAllTasks(object? sender, RoutedEventArgs e)
    {
        if (_taskManager != null)
            foreach (var task in _taskManager.Tasks.Where(t => t.IsActive && t.CanCancel)) _taskManager.CancelTask(task.Id);
    }
    private void ClearCompleted(object? sender, RoutedEventArgs e)
    {
        if (_taskManager != null)
            foreach (var task in _taskManager.Tasks.Where(t => t.IsTerminal)) _taskManager.RemoveTask(task.Id);
    }
    protected override void OnClosed(EventArgs e)
    {
        if (_taskManager != null) _taskManager.TasksChanged -= OnTasksChanged;
        if (_history != null) _history.HistoryChanged -= OnTasksChanged;
        base.OnClosed(e);
    }
}
