namespace MacExplorer.Models;

public enum BackgroundTaskState { Running, Completed, Failed, Cancelled, Waiting, Pausing, Paused, Cancelling, NeedsAttention }

public enum BackgroundTaskKind
{
    Generic,
    Copy,
    Move,
    Delete,
    Compress,
    Extract,
    RemoteUpload,
    RemoteDownload,
    BatchRename,
    Rename,
    Create
}

public class BackgroundTaskInfo
{
    public bool IsTerminal => State is BackgroundTaskState.Completed or BackgroundTaskState.Failed or BackgroundTaskState.Cancelled;
    public bool IsActive => !IsTerminal;
    public bool IsQueuedFileTask { get; internal set; }
    public FileTaskRequest? Request { get; internal set; }
    public Guid? HistoryId { get; set; }
    public DateTime CreatedAt { get; } = DateTime.Now;
    public IReadOnlyList<FileTaskItemResult> Items { get; internal set; } = [];
    public IReadOnlyList<string> Warnings { get; internal set; } = [];
    public int TotalItems { get; internal set; }
    public long BytesTransferred { get; internal set; }
    public long TotalBytes { get; internal set; }
    public double BytesPerSecond { get; internal set; }
    public TimeSpan? Remaining => BytesPerSecond > 0 && TotalBytes > BytesTransferred
        ? TimeSpan.FromSeconds((TotalBytes - BytesTransferred) / BytesPerSecond) : null;
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";
    public double Progress { get; set; }
    public bool IsIndeterminate { get; internal set; }
    public string CurrentFile { get; set; } = "";
    public BackgroundTaskState State { get; set; } = BackgroundTaskState.Running;
    public BackgroundTaskKind Kind { get; set; } = BackgroundTaskKind.Generic;
    public string? ErrorMessage { get; set; }
    public string? ErrorDetail { get; set; }
    public bool CanCancel { get; set; } = true;
    public bool CanRetry { get; set; }
    internal bool HasCompactSummary { get; set; }
    internal DateTime? FailedAtUtc { get; set; }
    internal long RetainedRetryBytes { get; set; }
    public Func<Task>? RetryAction { get; set; }
    public bool IsDismissedByUser { get; set; }
    public CancellationTokenSource Cts { get; set; } = new();
    public Func<Task>? OnCompleted { get; set; }
}
