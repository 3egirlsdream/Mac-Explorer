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

    public void Cancel() => _cancel.Cancel();

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
        using var log = new StreamWriter(LogPath, false);
        await log.WriteLineAsync($"[{DateTimeOffset.Now:O}] Plugin worker PID {_process.Id}");
        var written = 0;
        var buffer = new char[4096];
        int count;
        while ((count = await _process.StandardError.ReadAsync(buffer)) > 0)
        {
            if (written < 1_000_000) await log.WriteAsync(buffer, 0, Math.Min(count, 1_000_000 - written));
            written += count;
            await log.FlushAsync();
        }
    }

    private async Task SendAsync(PluginRpcMessage message)
    {
        await _writes.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message, PluginProtocol.Json));
            await _process.StandardInput.FlushAsync();
        }
        finally { _writes.Release(); }
    }

    public async Task<T> CallAsync<T>(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel.Token);
        deadline.CancelAfter(timeout);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (_reader.IsCompleted) throw new IOException("插件进程已退出。");
            await SendAsync(new() { Id = id, Method = method, Params = parameters == null ? null : JsonSerializer.SerializeToElement(parameters, PluginProtocol.Json) });
            var result = await completion.Task.WaitAsync(deadline.Token);
            return result.Deserialize<T>(PluginProtocol.Json) ?? throw new InvalidDataException("插件结果为空。");
        }
        catch (OperationCanceledException)
        {
            try { await SendAsync(new() { Method = "cancel" }); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            await StopAsync();
            if (!cancellationToken.IsCancellationRequested && !_cancel.IsCancellationRequested)
                throw new TimeoutException("插件处理超时，请尝试更小或更简单的文件。");
            throw;
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
            _process.StandardInput.Close();
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Cancel();
        try
        {
            await StopAsync();
            await Task.WhenAll(_reader, _logReader);
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
