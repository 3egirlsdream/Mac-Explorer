using MacExplorer.Models;

namespace MacExplorer.Services;

/// <summary>Execution state belongs to the application process, independently of a view or tab.</summary>
public sealed class FileTaskContext
{
    private static readonly AsyncLocal<FileTaskContext?> Ambient = new();
    public static FileTaskContext? Current => Ambient.Value;
    private readonly Dictionary<string, object?> _checkpoints = new(StringComparer.Ordinal);
    private readonly Func<Task>? _pause;
    private int _atomicDepth;
    private readonly Dictionary<string, Action> _cleanup = new();
    internal void RegisterCleanup(string key, Action cleanup) => _cleanup.TryAdd(key, cleanup);
    internal void Cleanup()
    {
        foreach (var action in _cleanup.Values)
            try { action(); } catch (Exception ex) { Warnings.Add(ex.Message); }
        _cleanup.Clear();
    }
    internal IDisposable Atomic()
    {
        _atomicDepth++;
        return new AtomicScope(this);
    }
    private sealed class AtomicScope(FileTaskContext context) : IDisposable
    { public void Dispose() => context._atomicDepth--; }
    private readonly Action? _changed;
    public BackgroundTaskInfo Task { get; }
    public CancellationToken CancellationToken => Task.Cts.Token;
    public List<FileTaskItemResult> Results { get; } = [];
    private readonly Dictionary<string, IDisposable> _resources = new();
    internal T Resource<T>(string key, Func<T> create) where T : class, IDisposable
    {
        if (_resources.TryGetValue(key, out var value)) return (T)value;
        var resource = create(); _resources.Add(key, resource); return resource;
    }
    public List<string> Warnings { get; } = [];
    internal FileTaskContext(BackgroundTaskInfo task, Func<Task>? pause = null, Action? changed = null)
    { Task = task; _pause = pause; _changed = changed; }

    internal async Task<T> ExecuteAsync<T>(Func<FileTaskContext, Task<T>> action)
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        try { return await action(this); }
        finally
        {
            if (CancellationToken.IsCancellationRequested) Cleanup();
            foreach (var resource in _resources.Values)
                try { resource.Dispose(); } catch (Exception ex) { Warnings.Add(ex.Message); }
            _resources.Clear(); Publish(); Ambient.Value = previous;
        }
    }

    internal static async Task<T> RunUnmanagedAsync<T>(Func<FileTaskContext, Task<T>> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        return await new FileTaskContext(new BackgroundTaskInfo { Cts = linked }).ExecuteAsync(action);
    }

    public static Task<T> RunAsync<T>(IBackgroundTaskManager? manager, FileTaskRequest request,
        Func<FileTaskContext, Task<T>> action, CancellationToken token = default)
        => Current is { } current ? action(current)
            : manager != null ? manager.RunAsync(request, action, token) : RunUnmanagedAsync(action, token);

    public async Task CheckpointAsync()
    {
        if (_atomicDepth > 0) return;
        CancellationToken.ThrowIfCancellationRequested();
        if (_pause != null) await _pause();
        CancellationToken.ThrowIfCancellationRequested();
        Subscriptions.SubscriptionAccess.RequireAccess();
    }

    public static void Checkpoint() => Current?.CheckpointAsync().GetAwaiter().GetResult();

    public T Remember<T>(string key, Func<T> create)
    {
        if (_checkpoints.TryGetValue(key, out var value)) return (T)value!;
        var result = create(); _checkpoints.Add(key, result); return result;
    }

    public bool IsDone(string key) => _checkpoints.ContainsKey("done:" + key);
    public void MarkDone(string key) => _checkpoints["done:" + key] = true;

    public async Task<T> StepAsync<T>(string key, Func<Task<T>> action)
    {
        if (_checkpoints.TryGetValue("step:" + key, out var value)) return (T)value!;
        await CheckpointAsync();
        var result = await action();
        _checkpoints["step:" + key] = result;
        return result;
    }

    public void ReportItem(string source, string? destination, FileTaskItemState state, string? error = null)
    {
        Results.RemoveAll(i => i.Source == source);
        Results.Add(new(source, destination, state, error));
        Publish();
    }

    public void Publish()
    {
        Task.TotalItems = Math.Max(Task.TotalItems, Results.Count);
        Task.Items = Results.ToArray(); Task.Warnings = Warnings.ToArray(); _changed?.Invoke();
    }
}
