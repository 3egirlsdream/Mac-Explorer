using MacExplorer.Models;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed class BackgroundTaskManager : IBackgroundTaskManager
{
    internal const int CompletedTaskLimit = 100;
    private sealed class Entry
    {
        public required BackgroundTaskInfo Info;
        public required FileTaskRequest Request;
        public required Func<Task<object?>> Execute;
        public required FileTaskContext Context;
        public TaskCompletionSource<object?> Completion = NewCompletion();
        public TaskCompletionSource? Resume;
        public bool Slot;
        public bool Claimed;
        public bool Executing;
        public CancellationTokenRegistration Registration;
        public readonly List<string> Paths = [];
    }
    private readonly List<BackgroundTaskInfo> _tasks = [];
    private readonly List<Entry> _queue = [];
    private readonly object _lock = new();
    private readonly ILogger<BackgroundTaskManager>? _logger;
    private readonly ISettingsService? _settings;
    private bool _stopping;
    private int _maxConcurrency;
    private static TaskCompletionSource<object?> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action? TasksChanged;
    public IReadOnlyList<BackgroundTaskInfo> Tasks { get { lock (_lock) return _tasks.ToArray(); } }
    public int MaxConcurrency
    {
        get { lock (_lock) return _maxConcurrency; }
        set
        {
            if (value is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(value));
            _settings?.Set("FileTaskConcurrency", value);
            lock (_lock) { _maxConcurrency = value; Schedule(); }
            RaiseTasksChanged();
        }
    }
    public BackgroundTaskManager(ILoggerFactory? loggerFactory = null, ISettingsService? settings = null)
    {
        _logger = loggerFactory?.CreateLogger<BackgroundTaskManager>(); _settings = settings;
        _maxConcurrency = Math.Clamp(settings?.Get("FileTaskConcurrency", 2) ?? 2, 1, 4);
    }

    public async Task<T> RunAsync<T>(FileTaskRequest request, Func<FileTaskContext, Task<T>> execute,
        CancellationToken cancellationToken = default)
    {
        if (FileTaskContext.Current is { } parent) return await execute(parent);
        cancellationToken.ThrowIfCancellationRequested();
        var info = new BackgroundTaskInfo { Kind = request.Kind, Label = request.Label,
            State = BackgroundTaskState.Waiting, IsQueuedFileTask = true, Request = request, CanRetry = true, TotalItems = request.Sources.Count };
        Entry? entry = null;
        var context = new FileTaskContext(info, () => PauseAtCheckpointAsync(entry!), RaiseTasksChanged);
        entry = new Entry { Info = info, Request = request, Context = context,
            Execute = async () => await context.ExecuteAsync(execute) };
        foreach (var path in request.Sources.Concat(request.Destination is { } dest ? [dest] : Array.Empty<string>()))
            entry.Paths.Add(Normalize(path));
        lock (_lock)
        {
            if (_stopping) throw new InvalidOperationException(LocalizationText.Get("应用正在退出，无法添加任务。"));
            _tasks.Add(info); _queue.Add(entry);
            entry.Registration = cancellationToken.Register(() => CancelTask(info.Id));
            Schedule();
        }
        RaiseTasksChanged();
        return (T)(await entry.Completion.Task)!;
    }

    // The scheduler owns execution slots. Paused operations retain their path claims.
    private void Schedule()
    {
        if (_stopping) return;
        foreach (var entry in _queue.Where(e => e.Info.State == BackgroundTaskState.Waiting).ToArray())
        {
            if (_queue.Count(e => e.Slot) >= _maxConcurrency) break;
            if (_queue.Any(other => other != entry && other.Claimed && Conflicts(other, entry))) continue;
            entry.Slot = entry.Claimed = true;
            entry.Info.State = BackgroundTaskState.Running;
            if (entry.Resume is { } resume) { entry.Resume = null; resume.TrySetResult(); }
            else { entry.Executing = true; _ = System.Threading.Tasks.Task.Run(() => ExecuteEntryAsync(entry)); }
        }
    }

    internal static string Normalize(string path)
    {
        if (VirtualPath.IsRemotePath(path)) return path.TrimEnd('/');
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            // Resolve existing parents as well as the leaf, including destinations not created yet.
            var suffix = new Stack<string>(); var existing = full;
            while (!File.Exists(existing) && !Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent && parent != existing)
            { suffix.Push(Path.GetFileName(existing)); existing = parent; }
            if (OperatingSystem.IsMacOS()) existing = Platforms.MacOS.MacSandboxNative.RealPath(existing);
            foreach (var part in suffix) existing = Path.Combine(existing, part);
            return existing;
        }
        catch { return Path.GetFullPath(path); }
    }
    internal static bool Overlaps(string left, string right) => left == right
        || left.StartsWith(right.TrimEnd('/') + "/", StringComparison.Ordinal)
        || right.StartsWith(left.TrimEnd('/') + "/", StringComparison.Ordinal);
    private static bool Conflicts(Entry left, Entry right) => left.Paths.Any(a => right.Paths.Any(b => Overlaps(a, b)));

    private async Task ExecuteEntryAsync(Entry entry)
    {
        object? result = null; Exception? failure = null;
        try { result = await entry.Execute(); }
        catch (Exception ex) { failure = ex; }
        Func<Task>? callback = null;
        lock (_lock)
        {
            var info = entry.Info;
            if (failure is OperationCanceledException || info.Cts.IsCancellationRequested)
                info.State = BackgroundTaskState.Cancelled;
            else if (failure != null || info.ErrorMessage != null)
            {
                info.State = failure is IOException or UnauthorizedAccessException or Renci.SshNet.Common.SshException
                    ? BackgroundTaskState.NeedsAttention : BackgroundTaskState.Failed;
                info.ErrorMessage ??= failure?.Message;
                info.ErrorDetail ??= failure?.ToString();
            }
            else { info.State = BackgroundTaskState.Completed; info.Progress = 100; callback = info.OnCompleted; }
            if (info.State is BackgroundTaskState.Completed or BackgroundTaskState.Cancelled)
            {
                info.CanRetry = false;
                info.OnCompleted = null;
                info.RetryAction = null;
            }
        }
        if (entry.Info.State is BackgroundTaskState.Completed or BackgroundTaskState.Cancelled)
            entry.Context.ReleaseExecutionState();
        lock (_lock)
        {
            entry.Slot = entry.Claimed = entry.Executing = false;
            entry.Resume = null;
            entry.Registration.Unregister();
            if (entry.Info.State is BackgroundTaskState.Completed or BackgroundTaskState.Cancelled)
                _queue.Remove(entry);
            if (failure != null) entry.Completion.TrySetException(failure);
            else entry.Completion.TrySetResult(result);
            TrimCompletedTasks();
            Schedule();
        }
        RaiseTasksChanged();
        if (callback != null) await InvokeCallbackAsync(callback);
    }

    private async Task PauseAtCheckpointAsync(Entry entry)
    {
        Task? wait = null;
        lock (_lock)
        {
            if (entry.Info.State == BackgroundTaskState.Pausing)
            {
                entry.Info.State = BackgroundTaskState.Paused;
                entry.Slot = false;
                entry.Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = entry.Resume.Task; Schedule();
            }
        }
        if (wait != null) { RaiseTasksChanged(); await wait.WaitAsync(entry.Info.Cts.Token); }
    }

    public void PauseTask(string taskId)
    {
        lock (_lock)
        {
            var entry = _queue.Find(e => e.Info.Id == taskId);
            if (entry == null) return;
            if (entry.Info.State == BackgroundTaskState.Running) entry.Info.State = BackgroundTaskState.Pausing;
            else if (entry.Info.State == BackgroundTaskState.Waiting) entry.Info.State = BackgroundTaskState.Paused;
        }
        RaiseTasksChanged();
    }
    public void ResumeTask(string taskId)
    {
        lock (_lock)
        {
            var entry = _queue.Find(e => e.Info.Id == taskId);
            if (entry?.Info.State == BackgroundTaskState.Pausing) entry.Info.State = BackgroundTaskState.Running;
            else if (entry?.Info.State == BackgroundTaskState.Paused) { entry.Info.State = BackgroundTaskState.Waiting; Schedule(); }
        }
        RaiseTasksChanged();
    }
    public void MoveTask(string taskId, int direction)
    {
        lock (_lock)
        {
            var index = _queue.FindIndex(e => e.Info.Id == taskId && e.Info.State == BackgroundTaskState.Waiting);
            if (index < 0 || direction == 0) return;
            var next = index + Math.Sign(direction);
            while (next >= 0 && next < _queue.Count && _queue[next].Info.State != BackgroundTaskState.Waiting) next += Math.Sign(direction);
            if (next < 0 || next >= _queue.Count || next == index) return;
            (_queue[index], _queue[next]) = (_queue[next], _queue[index]);
            var ordered = _queue.Select(e => e.Info).ToList();
            _tasks.Sort((a, b) => ordered.IndexOf(a).CompareTo(ordered.IndexOf(b)));
        }
        RaiseTasksChanged();
    }
    public Task WaitForCompletionAsync(string taskId)
    { lock (_lock) return _queue.Find(e => e.Info.Id == taskId)?.Completion.Task ?? System.Threading.Tasks.Task.CompletedTask; }
    public async Task ShutdownAsync()
    {
        Task[] pending;
        lock (_lock) { _stopping = true; pending = _queue.Select(e => e.Completion.Task).ToArray(); }
        foreach (var task in Tasks.Where(t => t.IsActive)) CancelTask(task.Id);
        try { await System.Threading.Tasks.Task.WhenAll(pending); } catch { /* Each operation retains its own outcome. */ }
        foreach (var entry in _queue.ToArray()) entry.Context.ReleaseExecutionState();
    }

    public BackgroundTaskInfo AddTask(string label, Func<Task>? onCompleted = null)
        => AddTask(label, BackgroundTaskKind.Generic, onCompleted);
    public BackgroundTaskInfo AddTask(string label, BackgroundTaskKind kind, Func<Task>? onCompleted = null, Func<Task>? retryAction = null)
    {
        if (FileTaskContext.Current is { } current) return current.Task;
        var task = new BackgroundTaskInfo { Label = label, Kind = kind, OnCompleted = onCompleted,
            RetryAction = retryAction, CanRetry = retryAction != null };
        lock (_lock) { if (_stopping) throw new InvalidOperationException("Application is stopping"); _tasks.Add(task); }
        RaiseTasksChanged(); return task;
    }
    public void UpdateProgress(string taskId, double progress, string currentFile, string? label = null)
    {
        lock (_lock)
        {
            var task = _tasks.Find(t => t.Id == taskId);
            if (task == null || task.IsTerminal && task.IsQueuedFileTask) return;
            task.Progress = Math.Clamp(progress, 0, 100); task.CurrentFile = currentFile;
            if (!string.IsNullOrWhiteSpace(label)) task.Label = label;
        }
        RaiseTasksChanged();
    }
    public void CompleteTask(string taskId)
    {
        Func<Task>? callback;
        lock (_lock)
        {
            var task = _tasks.Find(t => t.Id == taskId);
            if (task == null || task.IsQueuedFileTask || task.IsTerminal) return;
            task.State = BackgroundTaskState.Completed; task.Progress = 100; callback = task.OnCompleted;
            task.OnCompleted = null; task.RetryAction = null; task.CanRetry = false;
            TrimCompletedTasks();
        }
        RaiseTasksChanged(); if (callback != null) _ = InvokeCallbackAsync(callback);
    }
    public void FailTask(string taskId, string error, string? errorDetail = null)
    {
        lock (_lock)
        {
            var task = _tasks.Find(t => t.Id == taskId);
            if (task == null || task.IsTerminal) return;
            task.ErrorMessage = error; task.ErrorDetail = errorDetail;
            if (!task.IsQueuedFileTask) { task.State = BackgroundTaskState.Failed; task.CanRetry = task.RetryAction != null; }
        }
        RaiseTasksChanged();
    }
    public void CancelTask(string taskId)
    {
        CancellationTokenSource? cts;
        FileTaskContext? release = null;
        lock (_lock)
        {
            var task = _tasks.Find(t => t.Id == taskId);
            if (task == null || task.IsTerminal) return;
            cts = task.Cts;
            var entry = _queue.Find(e => e.Info == task);
            task.State = entry?.Executing == true ? BackgroundTaskState.Cancelling : BackgroundTaskState.Cancelled;
            if (entry != null && !entry.Executing)
            {
                entry.Claimed = entry.Slot = false; entry.Registration.Unregister();
                entry.Completion.TrySetCanceled(); _queue.Remove(entry); release = entry.Context;
                task.OnCompleted = null; task.RetryAction = null; task.CanRetry = false;
            }
            if (entry == null) { task.OnCompleted = null; task.RetryAction = null; task.CanRetry = false; }
        }
        cts.Cancel();
        release?.ReleaseExecutionState();
        lock (_lock) { TrimCompletedTasks(); Schedule(); }
        RaiseTasksChanged();
    }
    public void RetryTask(string taskId)
    {
        Func<Task>? legacy = null;
        lock (_lock)
        {
            var task = _tasks.Find(t => t.Id == taskId);
            if (task == null || task.State is not (BackgroundTaskState.Failed or BackgroundTaskState.NeedsAttention) || !task.CanRetry || _stopping) return;
            var entry = _queue.Find(e => e.Info == task);
            if (entry?.Executing == true) return;
            task.ErrorMessage = task.ErrorDetail = null;
            if (entry != null)
            {
                entry.Completion = NewCompletion();
                // Manual retries can outlive the original caller; observe their exceptions too.
                _ = entry.Completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                task.State = BackgroundTaskState.Waiting; Schedule();
            }
            else { task.State = BackgroundTaskState.Running; legacy = task.RetryAction; }
        }
        RaiseTasksChanged(); if (legacy != null) _ = InvokeCallbackAsync(legacy);
    }
    public void MinimizeTask(string taskId) { }
    private void TrimCompletedTasks()
    {
        foreach (var task in _tasks.Where(t => t.State is BackgroundTaskState.Completed or BackgroundTaskState.Cancelled)
                     .Where(t => !_queue.Any(e => e.Info == t && e.Executing))
                     .SkipLast(CompletedTaskLimit).ToArray())
        {
            _tasks.Remove(task);
        }
    }
    public void RemoveTask(string taskId)
    {
        FileTaskContext? context = null;
        BackgroundTaskInfo? removed = null;
        lock (_lock)
        {
            var task = _tasks.Find(t => t.Id == taskId);
            if (task == null || !task.IsTerminal) return;
            if (_queue.Any(e => e.Info == task && e.Executing)) return;
            context = _queue.Find(e => e.Info == task)?.Context;
            _tasks.Remove(task); _queue.RemoveAll(e => e.Info == task); removed = task;
        }
        if (removed != null)
            _ = System.Threading.Tasks.Task.Run(() => { context?.ReleaseExecutionState(); removed.Cts.Dispose(); });
        RaiseTasksChanged();
    }
    private async Task InvokeCallbackAsync(Func<Task> action)
    { try { await action(); } catch (Exception ex) { _logger?.LogWarning(ex, "Background task callback failed"); } }
    private void RaiseTasksChanged()
    {
        foreach (var handler in TasksChanged?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch (Exception ex) { _logger?.LogWarning(ex, "Background task subscriber failed"); }
    }
}
