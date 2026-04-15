namespace MacExplorer.Models;

public enum BackgroundTaskState { Running, Completed, Failed }

public class BackgroundTaskInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";
    public double Progress { get; set; }
    public string CurrentFile { get; set; } = "";
    public BackgroundTaskState State { get; set; } = BackgroundTaskState.Running;
    public string? ErrorMessage { get; set; }
    public bool IsMinimized { get; set; }
    public CancellationTokenSource Cts { get; set; } = new();
    public Func<Task>? OnCompleted { get; set; }
}
