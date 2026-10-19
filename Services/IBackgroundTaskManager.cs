using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IBackgroundTaskManager
{
    int MaxConcurrency { get => 2; set { } }
    Task<T> RunAsync<T>(FileTaskRequest request, Func<FileTaskContext, Task<T>> execute,
        CancellationToken cancellationToken = default) => FileTaskContext.RunUnmanagedAsync(execute, cancellationToken);
    void PauseTask(string taskId) { }
    void ResumeTask(string taskId) { }
    void MoveTask(string taskId, int direction) { }
    Task WaitForCompletionAsync(string taskId) => Task.CompletedTask;
    Task ShutdownAsync() => Task.CompletedTask;
    IReadOnlyList<BackgroundTaskInfo> Tasks { get; }
    event Action? TasksChanged;

    BackgroundTaskInfo AddTask(string label, Func<Task>? onCompleted = null);
    BackgroundTaskInfo AddTask(string label, BackgroundTaskKind kind, Func<Task>? onCompleted = null, Func<Task>? retryAction = null);
    void UpdateProgress(string taskId, double progress, string currentFile, string? label = null);
    void CompleteTask(string taskId);
    void FailTask(string taskId, string error, string? errorDetail = null);
    void CancelTask(string taskId);
    void RetryTask(string taskId);
    void MinimizeTask(string taskId);
    void RemoveTask(string taskId);
}
