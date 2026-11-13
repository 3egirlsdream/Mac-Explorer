using MacExplorer.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed class RemoteFileEditService : IRemoteFileEditService, IDisposable
{
    private sealed record RemoteVersion(long Size, DateTime Modified, string? Hash = null);
    private sealed class Session(string local, string remote, string server, RemoteVersion version)
    {
        internal readonly string Local = local, Remote = remote, Server = server;
        internal RemoteVersion Baseline = version;
        internal DateTime SyncedLocalWrite = File.GetLastWriteTimeUtc(local);
        internal RemoteEditState State;
        internal string? Error;
        internal RemoteEditFailureReason FailureReason;
        internal long Generation, Uploaded;
        internal Task Pump = Task.CompletedTask;
        internal CancellationTokenSource Lifetime = new();
        internal FileSystemWatcher? Watcher;
    }
    private readonly IRemoteConnectionService _connections;
    private readonly ILogger<RemoteFileEditService>? _logger;
    private readonly IBackgroundTaskManager? _tasks;
    private readonly IFileOperationHistoryService? _history;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly string _tempDir = Path.Combine(RuntimePaths.DataDirectory, "remote-edit");
    private bool _disposed;
    public event Action? Changed;
    public IReadOnlyList<RemoteEditSession> Sessions
    {
        get { lock (_sync) return _sessions.Values.Select(s => new RemoteEditSession(s.Local, s.Remote, s.Server,
            s.State, s.Error, s.Generation > s.Uploaded || LocalChanged(s)) { FailureReason = s.FailureReason }).ToArray(); }
    }
    public bool HasPendingChanges => Sessions.Any(s => s.HasPendingChanges);

    public RemoteFileEditService(IRemoteConnectionService connectionService, ILogger<RemoteFileEditService>? logger = null,
        IBackgroundTaskManager? tasks = null, IFileOperationHistoryService? history = null)
    { _connections = connectionService; _logger = logger; _tasks = tasks; _history = history; Directory.CreateDirectory(_tempDir); }

    private SftpClient Client(string path, FileTaskContext context) => context.Resource("sftp:" + VirtualPath.ParseRemotePath(path).ServerId,
        () => _connections.LeaseClientAsync(VirtualPath.ParseRemotePath(path).ServerId, context.CancellationToken).GetAwaiter().GetResult());
    private static RemoteVersion Version(SftpClient client, string path, bool content = false, CancellationToken token = default)
    {
        var a = client.GetAttributes(path);
        if (!content) return new(a.Size, a.LastWriteTimeUtc);
        using var stream = client.OpenRead(path); using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024]; int read;
        while ((read = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); digest.AppendData(buffer.AsSpan(0, read)); }
        return new(a.Size, a.LastWriteTimeUtc, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant());
    }

    public async Task<string> DownloadForEditAsync(string remotePath, string serverId)
    {
        Session? existing;
        lock (_sync)
        {
            existing = _sessions.Values.FirstOrDefault(s => s.Remote == remotePath && s.Server == serverId && File.Exists(s.Local));
            if (existing == null && _sessions.Count >= 64) throw new IOException(LocalizationText.Get("remote-edit.limit"));
        }
        if (existing != null) { await ResumeStoppedSessionAsync(existing); return existing.Local; }
        var source = VirtualPath.BuildRemotePath(serverId, remotePath);
        var directory = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        return await FileTaskContext.RunAsync(_tasks, new(BackgroundTaskKind.RemoteDownload, LocalizationText.Get("下载远程文件"), [source], directory), async context =>
        {
            var client = Client(source, context);
            var version = await Task.Run(() => Version(client, remotePath), context.CancellationToken);
            Directory.CreateDirectory(directory);
            var output = await SftpTransfer.CopyAsync(source, directory, context, p => Client(p, context), null);
            if (await Task.Run(() => Version(client, remotePath), context.CancellationToken) != version)
                throw new IOException(LocalizationText.Get("remote-edit.changed"));
            version = version with { Hash = await FileHashCalculator.ComputeAsync(output, context.CancellationToken) };
            lock (_sync) _sessions[output] = new(output, remotePath, serverId, version);
            Notify(); return output;
        });
    }

    public Task UploadAsync(string localPath, string remotePath, string serverId)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(localPath, out var session) || session.Remote != remotePath || session.Server != serverId)
                throw new InvalidOperationException(LocalizationText.Get("remote-edit.open-first"));
            session.Generation++;
            return StartPump(session);
        }
    }
    public async Task RetryAsync(string localPath)
    {
        Session session;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(localPath, out session!) || session.State == RemoteEditState.Conflict) return;
        }
        await ResumeStoppedSessionAsync(session);
        Task pump;
        lock (_sync)
        {
            session.Generation++;
            session.State = RemoteEditState.Pending;
            pump = StartPump(session);
        }
        await pump;
    }
    public async Task ReconnectAsync(string localPath)
    {
        Session session; lock (_sync) session = _sessions[localPath];
        var server = _connections.GetSavedServers().FirstOrDefault(s => s.Id == session.Server)
            ?? throw new IOException(LocalizationText.Get("remote-edit.open-first"));
        _connections.Disconnect(session.Server);
        await _connections.ConnectAsync(server);
        await RetryAsync(localPath);
    }
    private async Task ResumeStoppedSessionAsync(Session session)
    {
        Task pump;
        lock (_sync) { if (session.State != RemoteEditState.Stopped) return; pump = session.Pump; }
        await pump;
        lock (_sync)
        {
            if (session.State != RemoteEditState.Stopped || _disposed) return;
            session.Lifetime.Dispose(); session.Lifetime = new();
            session.State = session.Generation > session.Uploaded ? RemoteEditState.Pending : RemoteEditState.Synced;
        }
        WatchForChanges(session.Local, session.Remote, session.Server);
        Notify();
    }
    public async Task ResolveConflictAsync(string localPath, bool overwriteRemote)
    {
        Session session;
        lock (_sync) session = _sessions[localPath];
        if (!overwriteRemote) { StopWatching(localPath); return; }
        using var client = await _connections.LeaseClientAsync(session.Server);
        var version = await Task.Run(() => Version(client, session.Remote, true));
        lock (_sync) { session.Baseline = version; session.State = RemoteEditState.Pending; }
        await RetryAsync(localPath);
    }
    private Task StartPump(Session session)
    {
        if (!session.Pump.IsCompleted || session.State is RemoteEditState.Conflict or RemoteEditState.Stopped) return session.Pump;
        session.State = RemoteEditState.Pending;
        session.Pump = Task.Run(() => PumpAsync(session));
        Notify(); return session.Pump;
    }
    private async Task PumpAsync(Session session)
    {
        try
        {
            while (true)
            {
                long generation;
                do
                {
                    lock (_sync) generation = session.Generation;
                    await Task.Delay(500, session.Lifetime.Token);
                    lock (_sync) if (generation == session.Generation) break;
                } while (true);
                var snapshotDirectory = Path.Combine(_tempDir, "upload-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(snapshotDirectory);
                var snapshot = Path.Combine(snapshotDirectory, Path.GetFileName(session.Local));
                try
                {
                    var before = new FileInfo(session.Local); var size = before.Length; var modified = before.LastWriteTimeUtc;
                    await using (var input = new FileStream(session.Local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true))
                    await using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        await input.CopyToAsync(output, session.Lifetime.Token);
                    var after = new FileInfo(session.Local);
                    if (!after.Exists || size != after.Length || modified != after.LastWriteTimeUtc)
                    { lock (_sync) session.Generation++; continue; }
                    SetState(session, RemoteEditState.Uploading);
                    await UploadSnapshotAsync(session, snapshot);
                    lock (_sync)
                    {
                        session.Uploaded = generation;
                        if (session.Generation == generation) { session.SyncedLocalWrite = after.LastWriteTimeUtc; session.State = RemoteEditState.Synced; session.Error = null; break; }
                        session.State = RemoteEditState.Pending;
                    }
                    Notify();
                }
                finally { Directory.Delete(snapshotDirectory, true); }
            }
        }
        catch (RemoteEditConflictException ex) { SetState(session, RemoteEditState.Conflict, ex.Message); }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { SetState(session, RemoteEditState.Stopped); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Remote edit upload failed"); SetState(session, RemoteEditState.Failed, ex.Message, ex); }
        finally
        {
            lock (_sync)
            {
                session.Pump = Task.CompletedTask;
                if (!_disposed && session.Generation > session.Uploaded && session.State is RemoteEditState.Pending or RemoteEditState.Synced)
                    StartPump(session);
            }
            Notify();
        }
    }
    private async Task UploadSnapshotAsync(Session session, string snapshot)
    {
        var target = VirtualPath.BuildRemotePath(session.Server, session.Remote);
        var directory = VirtualPath.BuildRemotePath(session.Server, Path.GetDirectoryName(session.Remote) ?? "/");
        var task = _tasks?.AddTask(LocalizationText.Get("上传远程文件"), BackgroundTaskKind.RemoteUpload,
            retryAction: () => RetryAsync(session.Local));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token, task?.Cts.Token ?? CancellationToken.None);
        try
        {
            await FileTaskContext.RunAsync(null, new(BackgroundTaskKind.RemoteUpload, LocalizationText.Get("上传远程文件"), [snapshot], target), async context =>
            {
                var client = Client(target, context);
                void Check()
                {
                    if (!client.Exists(session.Remote) || Version(client, session.Remote, true, linked.Token) != session.Baseline)
                        throw new RemoteEditConflictException(LocalizationText.Get("remote-edit.conflict"));
                }
                await Task.Run(Check, linked.Token);
                await SftpTransfer.CopyAsync(snapshot, directory, context, p => Client(p, context),
                    task == null ? null : new InlineProgress(p => _tasks!.UpdateProgress(task.Id, p.Percentage, session.Remote)), target, true, Check);
                var version = Version(client, session.Remote) with { Hash = await FileHashCalculator.ComputeAsync(snapshot, linked.Token) };
                lock (_sync) session.Baseline = version;
                if (_history != null) await _history.RecordIrreversibleAsync(target, LocalizationText.Get("远程修改不可撤销。"));
                return true;
            }, linked.Token);
            if (task != null) _tasks!.CompleteTask(task.Id);
        }
        catch (Exception ex) { if (task != null) _tasks!.FailTask(task.Id, ex.Message); throw; }
    }
    private sealed class InlineProgress(Action<FileOperationProgress> report) : IProgress<FileOperationProgress>
    { public void Report(FileOperationProgress value) => report(value); }
    private sealed class RemoteEditConflictException(string message) : IOException(message);

    public void WatchForChanges(string localTempPath, string remotePath, string serverId)
    {
        lock (_sync)
        {
            var session = _sessions[localTempPath];
            session.Watcher?.Dispose();
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(localTempPath)!)
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            void ChangedFile(string path)
            {
                if (path != localTempPath) return;
                lock (_sync)
                {
                    if (_disposed || session.State == RemoteEditState.Stopped) return;
                    session.Generation++;
                    if (session.State is not (RemoteEditState.Conflict or RemoteEditState.Uploading)) session.State = RemoteEditState.Pending;
                    StartPump(session);
                }
                Notify();
            }
            watcher.Changed += (_, e) => ChangedFile(e.FullPath);
            watcher.Created += (_, e) => ChangedFile(e.FullPath);
            watcher.Renamed += (_, e) => { ChangedFile(e.FullPath); ChangedFile(e.OldFullPath); };
            watcher.Error += (_, e) => { var error = e.GetException(); SetState(session, RemoteEditState.Failed, error.Message, error); };
            session.Watcher = watcher; watcher.EnableRaisingEvents = true;
        }
    }
    public void StopWatching(string localTempPath)
    {
        lock (_sync)
            if (_sessions.TryGetValue(localTempPath, out var session))
            { session.Watcher?.Dispose(); session.Watcher = null; session.Lifetime.Cancel(); session.State = RemoteEditState.Stopped; }
        Notify();
    }
    private void SetState(Session session, RemoteEditState state, string? error = null, Exception? exception = null)
    {
        lock (_sync) { session.State = state; session.Error = error; session.FailureReason = FailureReason(exception); }
        Notify();
    }
    internal static RemoteEditFailureReason FailureReason(Exception? exception)
    {
        if (exception?.InnerException is { } inner && FailureReason(inner) is var reason && reason != RemoteEditFailureReason.Unknown) return reason;
        return exception switch
        {
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => RemoteEditFailureReason.ConnectionRefused,
            SocketException { SocketErrorCode: SocketError.TimedOut } or TimeoutException or SshOperationTimeoutException => RemoteEditFailureReason.TimedOut,
            UnauthorizedAccessException or SftpPermissionDeniedException or SshAuthenticationException => RemoteEditFailureReason.PermissionDenied,
            SocketException or SshConnectionException => RemoteEditFailureReason.ConnectionLost,
            _ => RemoteEditFailureReason.Unknown
        };
    }
    private void Notify() => Changed?.Invoke();
    public async Task StopAsync()
    {
        Session[] sessions; lock (_sync) sessions = _sessions.Values.ToArray();
        foreach (var session in sessions) StopWatching(session.Local);
        try { await Task.WhenAll(sessions.Select(s => s.Pump)).WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException ex) { _logger?.LogWarning(ex, "Remote edit shutdown timed out; local copies are retained"); }
    }
    public void CleanupTempFiles()
    {
        Session[] sessions;
        lock (_sync) { sessions = _sessions.Values.ToArray(); }
        foreach (var s in sessions)
        {
            StopWatching(s.Local);
            if (s.Generation == s.Uploaded && !LocalChanged(s) && s.Pump.IsCompleted)
            { var dir = Path.GetDirectoryName(s.Local)!; if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }
    private static bool LocalChanged(Session session)
    {
        try
        {
            var info = new FileInfo(session.Local);
            return !info.Exists || info.Length != session.Baseline.Size || info.LastWriteTimeUtc != session.SyncedLocalWrite;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
    public void Dispose() { _disposed = true; CleanupTempFiles(); }
}
