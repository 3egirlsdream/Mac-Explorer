using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IBackgroundTaskManager
{
    IReadOnlyList<BackgroundTaskInfo> Tasks { get; }
    event Action? TasksChanged;

    BackgroundTaskInfo AddTask(string label, Func<Task>? onCompleted = null);
    void UpdateProgress(string taskId, double progress, string currentFile);
    void CompleteTask(string taskId);
    void FailTask(string taskId, string error);
    void MinimizeTask(string taskId);
    void RemoveTask(string taskId);
}
