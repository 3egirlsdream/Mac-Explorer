using System.Diagnostics;
using MacExplorer.Services.Subscriptions;
using MacExplorer.Models;

namespace MacExplorer.Services;

/// <summary>Execution state belongs to the application process, independently of a view or tab.</summary>
public sealed class FileTaskContext
{
    private static readonly AsyncLocal<FileTaskContext?> Ambient = new();
    public static FileTaskContext? Current => Ambient.Value;
    private readonly PremiumFeature _featureGrant;
    private int _grantActive;
    internal bool HasFeatureGrant(PremiumFeature feature) => Volatile.Read(ref _grantActive) != 0 && (_featureGrant & feature) == feature;
    private readonly Dictionary<string, object?> _checkpoints = new(StringComparer.Ordinal);
    private Func<Task>? _pause;
    private int _atomicDepth;
    private readonly Dictionary<string, Action> _cleanup = new();
    internal void RegisterCleanup(string key, Action cleanup) => _cleanup.TryAdd(key, cleanup);
    internal void Cleanup()
    {
        foreach (var action in _cleanup.Values)
            try { action(); } catch (Exception ex) { Warnings.Add(ex.Message); }
        _cleanup.Clear();
    }
    internal void ReleaseExecutionState()
    {
        Cleanup();
        DisposeResources();
        _checkpoints.Clear();
        Results.Clear();
        _resultIndices.Clear();
        Warnings.Clear();
        _pause = null;
        _changed = null;
    }
    internal long EstimateRetainedBytes(long limit)
    {
        try
        {
            var size = new RetainedStateSize(limit);
            size.Measure(_checkpoints);
            size.Measure(Results);
            size.Measure(_resultIndices);
            size.Measure(Warnings);
            return size.Measure(Task.Request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MemoryDiagnostics.Record("task.state_estimate_failed", new { exception = ex.GetType().Name });
            return limit + 1;
        }
    }
    internal IDisposable Atomic()
    {
        _atomicDepth++;
        return new AtomicScope(this);
    }
    private sealed class AtomicScope(FileTaskContext context) : IDisposable
    { public void Dispose() => context._atomicDepth--; }
    private Action? _changed;
    public BackgroundTaskInfo Task { get; }
    public CancellationToken CancellationToken => Task.Cts.Token;
    public List<FileTaskItemResult> Results { get; } = [];
    private readonly Dictionary<string, int> _resultIndices = new(StringComparer.Ordinal);
    private long _lastItemPublish;

    private readonly Dictionary<string, IDisposable> _resources = new();
    internal T Resource<T>(string key, Func<T> create) where T : class, IDisposable
    {
        if (_resources.TryGetValue(key, out var value)) return (T)value;
        var resource = create(); _resources.Add(key, resource); return resource;
    }
    public List<string> Warnings { get; } = [];
    internal FileTaskContext(BackgroundTaskInfo task, Func<Task>? pause = null, Action? changed = null, PremiumFeature featureGrant = PremiumFeature.None)
    { Task = task; _pause = pause; _changed = changed; _featureGrant = featureGrant; }

    internal async Task<T> ExecuteAsync<T>(Func<FileTaskContext, Task<T>> action)
    {
        Volatile.Write(ref _grantActive, 1);
        var previous = Ambient.Value;
        Ambient.Value = this;
        var succeeded = false;
        try
        {
            var result = await action(this);
            succeeded = Task.ErrorMessage == null;
            return result;
        }
        finally
        {
            try
            {
                if (succeeded || CancellationToken.IsCancellationRequested) Cleanup();
                DisposeResources(); Publish();
            }
            finally { Volatile.Write(ref _grantActive, 0); Ambient.Value = previous; }
        }
    }

    private void DisposeResources()
    {
        foreach (var resource in _resources.Values)
            try { resource.Dispose(); } catch (Exception ex) { Warnings.Add(ex.Message); }
        _resources.Clear();
    }

    internal static async Task<T> RunUnmanagedAsync<T>(Func<FileTaskContext, Task<T>> action, CancellationToken token, PremiumFeature grant = PremiumFeature.None)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        return await new FileTaskContext(new BackgroundTaskInfo { Cts = linked }, featureGrant: grant).ExecuteAsync(action);
    }

    public static Task<T> RunAsync<T>(IBackgroundTaskManager? manager, FileTaskRequest request,
        Func<FileTaskContext, Task<T>> action, CancellationToken token = default)
    {
        if (Current == null && manager != null) return manager.RunAsync(request, action, token);
        var feature = FeatureAccess.ForRequest(request);
        FeatureAccess.Require(feature);
        return Current is { } current ? action(current)
            : manager != null ? manager.RunAsync(request, action, token) : RunUnmanagedAsync(action, token, feature);
    }

    public async Task CheckpointAsync()
    {
        if (_atomicDepth > 0) return;
        CancellationToken.ThrowIfCancellationRequested();
        if (_pause != null) await _pause();
        CancellationToken.ThrowIfCancellationRequested();

    }

    public static void Checkpoint() => Current?.CheckpointAsync().GetAwaiter().GetResult();

    public T Remember<T>(string key, Func<T> create)
    {
        if (_checkpoints.TryGetValue(key, out var value)) return (T)value!;
        var result = create(); _checkpoints.Add(key, result); return result;
    }

    internal T? Recall<T>(string key) where T : class
        => _checkpoints.TryGetValue(key, out var value) ? (T?)value : null;

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
        var result = new FileTaskItemResult(source, destination, state, error);
        if (_resultIndices.TryGetValue(source, out var index)) Results[index] = result;
        else
        {
            _resultIndices.Add(source, Results.Count);
            Results.Add(result);
        }
        Task.TotalItems = Math.Max(Task.TotalItems, Results.Count);
        var now = Stopwatch.GetTimestamp();
        if (_lastItemPublish != 0 && Stopwatch.GetElapsedTime(_lastItemPublish, now).TotalMilliseconds < 50) return;
        Publish();
    }

    public void Publish()
    {
        _lastItemPublish = Stopwatch.GetTimestamp();
        Task.TotalItems = Math.Max(Task.TotalItems, Results.Count);
        Task.Items = Results.ToArray(); Task.Warnings = Warnings.ToArray(); _changed?.Invoke();
    }
}
