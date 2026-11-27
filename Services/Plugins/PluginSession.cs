using MacExplorer.FileConversion.Plugin;
using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

public sealed class PluginSession : IAsyncDisposable
{
    private readonly ConversionPlugin _builtIn = new(start => DirectoryAccess.Current.ConfigureHelper(start));
    private readonly Func<Task> _onDisposed;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource _callsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposeTask;
    private int _activeCalls;
    private bool _disposed;

    public PluginManifest Manifest { get; }
    public string WorkDirectory { get; }
    public event Action<PluginProgress>? Progress;

    internal PluginSession(PluginManifest manifest, string workDirectory, Func<Task> onDisposed)
    {
        Manifest = manifest;
        WorkDirectory = workDirectory;
        _onDisposed = onDisposed;
        Directory.CreateDirectory(workDirectory);
    }

    public void Cancel()
    {
        lock (_lifetimeGate)
            if (!_disposed) _cancel.Cancel();
    }

    public Task<PluginPreparation> PrepareAsync(PluginInvocation invocation, TimeSpan timeout, CancellationToken token) =>
        RunAsync(invocation, timeout, token, _builtIn.PrepareAsync);

    public Task<PluginResult> ExecuteAsync(PluginInvocation invocation, TimeSpan timeout, CancellationToken token) =>
        RunAsync(invocation, timeout, token, (request, ct) =>
            _builtIn.ExecuteAsync(request, new BuiltInProgress(p => Progress?.Invoke(p)), ct));

    private async Task<T> RunAsync<T>(PluginInvocation invocation, TimeSpan timeout, CancellationToken token,
        Func<PluginInvocation, CancellationToken, Task<T>> operation)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeCalls++;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _cancel.Token);
            deadline.CancelAfter(timeout);
            try
            {
                return await Task.Run(() =>
                {
                    foreach (var file in invocation.Files) DirectoryAccess.Current.EnsureAccess(file.Path);
                    return operation(invocation, deadline.Token);
                }, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested && !_cancel.IsCancellationRequested)
            {
                throw new TimeoutException("插件处理超时，请尝试更小或更简单的文件。");
            }
        }
        finally
        {
            lock (_lifetimeGate)
                if (--_activeCalls == 0 && _disposed) _callsDrained.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask != null) return new(_disposeTask);
            _disposed = true;
            if (_activeCalls == 0) _callsDrained.TrySetResult();
            return new(_disposeTask = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        try
        {
            _cancel.Cancel();
            await _callsDrained.Task.ConfigureAwait(false);
            Directory.Delete(WorkDirectory, true);
        }
        finally
        {
            _cancel.Dispose();
            await _onDisposed().ConfigureAwait(false);
        }
    }

    private sealed class BuiltInProgress(Action<PluginProgress> callback) : IProgress<PluginProgress>
    {
        public void Report(PluginProgress value) => callback(value);
    }
}
