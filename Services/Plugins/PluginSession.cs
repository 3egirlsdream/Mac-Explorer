using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

public sealed class PluginSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _writes = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<int, long> _children = new();
    private readonly Task _reader;
    private readonly Task _logReader;
    private readonly Func<Task> _onDisposed;
    private readonly CancellationTokenSource _cancel = new();
    private Task? _stopTask;
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource _callsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposeTask;
    private int _activeCalls;
    private int _nextId;
    private int _disposed;
    public PluginManifest Manifest { get; }
    public string WorkDirectory { get; }
    public string LogPath { get; }
    public int ProcessId => _process.Id;
    public event Action<PluginProgress>? Progress;

    internal PluginSession(string executable, string assemblyPath, string pluginDirectory, string workDirectory,
        string logPath, Func<Task> onDisposed)
    {
        Manifest = PluginPackage.ReadManifest(pluginDirectory);
        WorkDirectory = workDirectory;
        LogPath = logPath;
        _onDisposed = onDisposed;
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(assemblyPath);
        info.ArgumentList.Add(PluginProtocol.WorkerArgument);
        info.ArgumentList.Add(pluginDirectory);
        _process = Process.Start(info) ?? throw new IOException("无法启动插件进程。");
        _reader = ReadAsync();
        _logReader = ReadLogAsync();
    }

    public void Cancel()
    {
        lock (_lifetimeGate)
            if (_disposed == 0) _cancel.Cancel();
    }

    private async Task ReadAsync()
    {
        Exception failure = new IOException("插件进程已退出或通信已断开。");
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                var message = JsonSerializer.Deserialize<PluginRpcMessage>(line, PluginProtocol.Json)
                    ?? throw new InvalidDataException("插件返回了无效消息。");
                if (message.Jsonrpc != "2.0") throw new InvalidDataException("插件通信版本不兼容。");
                if (message.Method == "child-process" && message.Params is { } child)
                    _children[child.GetProperty("pid").GetInt32()] = child.GetProperty("started").GetInt64();
                else if (message.Method == "progress" && message.Params is { } parameters)
                    Progress?.Invoke(parameters.Deserialize<PluginProgress>(PluginProtocol.Json)!);
                else if (message.Id is { } id && _pending.TryRemove(id, out var completion))
                {
                    if (message.Error is { } error)
                        completion.TrySetException(error.Code == -32800 ? new OperationCanceledException(error.Message) : new IOException(error.Message));
                    else if (message.Result is { } result) completion.TrySetResult(result);
                    else completion.TrySetException(new InvalidDataException("插件未返回结果。"));
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            KillChildren();
            foreach (var completion in _pending.Values) completion.TrySetException(failure);
        }
    }

    private void KillChildren()
    {
        foreach (var (pid, started) in _children)
        {
            try
            {
                using var child = Process.GetProcessById(pid);
                if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == started) child.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private async Task ReadLogAsync()
    {
        StreamWriter? log = null;
        void CloseLog()
        {
            try { log?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Debug.WriteLine("关闭插件日志失败：" + ex); }
            log = null;
        }
        try
        {
            try
            {
                log = new StreamWriter(LogPath, false);
                await log.WriteLineAsync($"[{DateTimeOffset.Now:O}] Plugin worker PID {_process.Id}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Debug.WriteLine("创建插件日志失败：" + ex); CloseLog(); }
            var written = 0;
            var buffer = new char[4096];
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer)) > 0)
            {
                var toWrite = Math.Min(count, 1_000_000 - written);
                // Logging is optional; draining stderr is not. Disk-full, locked and
                // unwritable logs must not fill the worker's pipe and stall its RPCs.
                if (log == null || toWrite == 0) continue;
                try
                {
                    await log.WriteAsync(buffer, 0, toWrite);
                    written += toWrite;
                    await log.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Debug.WriteLine("写入插件日志失败：" + ex); CloseLog(); }
            }
        }
        finally { CloseLog(); }
    }

    private async Task SendAsync(PluginRpcMessage message, CancellationToken token)
    {
        await _writes.WaitAsync(token);
        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message, PluginProtocol.Json).AsMemory(), token);
            await _process.StandardInput.FlushAsync(token);
        }
        finally { _writes.Release(); }
    }

    public async Task<T> CallAsync<T>(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _activeCalls++;
        }
        try { return await CallCoreAsync<T>(method, parameters, timeout, cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_lifetimeGate)
                if (--_activeCalls == 0 && _disposed != 0) _callsDrained.TrySetResult();
        }
    }

    private async Task<T> CallCoreAsync<T>(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel.Token);
        deadline.CancelAfter(timeout);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (_reader.IsCompleted) throw new IOException("插件进程已退出。");
            await SendAsync(new() { Id = id, Method = method, Params = parameters == null ? null : JsonSerializer.SerializeToElement(parameters, PluginProtocol.Json) }, deadline.Token);
            var result = await completion.Task.WaitAsync(deadline.Token);
            return result.Deserialize<T>(PluginProtocol.Json) ?? throw new InvalidDataException("插件结果为空。");
        }
        catch (Exception cancellation) when (cancellation is OperationCanceledException ||
            (deadline.IsCancellationRequested && cancellation is (IOException or ObjectDisposedException or InvalidOperationException)))
        {
            var timedOut = deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested && !_cancel.IsCancellationRequested;
            // Give cooperative plugins a bounded opportunity to observe cancellation.
            // If the same pipe is blocked, terminate the worker instead of waiting for a write.
            using var cancelWrite = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try { await SendAsync(new() { Method = "cancel" }, cancelWrite.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                KillChildren();
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // The worker exited concurrently.
            }
            await StopAsync();
            if (timedOut)
                throw new TimeoutException("插件处理超时，请尝试更小或更简单的文件。");
            if (cancellation is OperationCanceledException) throw;
            throw new OperationCanceledException("插件处理已取消。", cancellation, deadline.Token);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private Task StopAsync()
    {
        lock (_process) return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        try
        {
            // Close the pipe, not StreamWriter: Close() on the writer may synchronously
            // flush a full pipe or throw while an asynchronous write is still in flight.
            try { _process.StandardInput.BaseStream.Close(); }
            catch (IOException) { } // A killed/exited worker can close the pipe first.
            KillChildren();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        catch (InvalidOperationException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = 1;
            if (_activeCalls == 0) _callsDrained.TrySetResult();
            return new ValueTask(_disposeTask = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Publish the shared disposal task before cancellation invokes any callbacks.
        await Task.Yield();
        _cancel.Cancel();
        try
        {
            await StopAsync();
            // RPC finally blocks release _writes and linked tokens. Join them before
            // disposing those objects, including when callers dispose concurrently.
            await Task.WhenAll(_reader, _logReader, _callsDrained.Task);
        }
        finally
        {
            _process.Dispose();
            _cancel.Dispose();
            _writes.Dispose();
            try { if (Directory.Exists(WorkDirectory)) Directory.Delete(WorkDirectory, true); }
            finally { await _onDisposed(); }
        }
    }
}
