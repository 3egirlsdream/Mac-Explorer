using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacExplorer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed partial class LocalSendService
{
    private sealed class IncomingFile(FileInfoDto info, string token)
    {
        public FileInfoDto Info { get; } = info;
        public string Token { get; } = token;
        public int Active;
        public bool Completed;
    }

    private sealed class IncomingSession(string id, string sourceIp, string alias, Dictionary<string, IncomingFile> files,
        string directory, BackgroundTaskInfo task)
    {
        public string Id { get; } = id;
        public string SourceIp { get; } = sourceIp;
        public string Alias { get; } = alias;
        public Dictionary<string, IncomingFile> Files { get; } = files;
        public string Directory { get; } = directory;
        public BackgroundTaskInfo Task { get; } = task;
        public CancellationTokenSource Cts { get; } = new();
        public CancellationTokenRegistration TaskCancellationRegistration;
        public long ProgressBytes;
        public long LastActivityTicks = DateTime.UtcNow.Ticks;
        public long LastProgressTimestamp;
        public long TotalBytes { get; } = files.Values.Sum(file => file.Info.Size);
        public int CompletedCount;
        public int Finished;
        public int ActiveUploads;
        public int CleanupReady;
        public int ExpirationDone;
        public int CleanupQueued;
        public bool IsPending;
        public void Cancel()
        {
            try { Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task<IResult> PrepareUploadAsync(HttpContext context)
    {
        if (!Enabled || _running == null) return Results.StatusCode(503);
        context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 4 * 1024 * 1024;
        PrepareRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<PrepareRequest>(Json, context.RequestAborted); }
        catch (JsonException) { return Results.BadRequest(); }
        if (request?.Info == null || request.Files == null || request.Files.Count is < 1 or > 10000
            || request.Info.Version is null || !request.Info.Version.StartsWith("2.", StringComparison.Ordinal))
            return Results.BadRequest();
        var sourceIp = (context.Connection.RemoteIpAddress is { } remoteIp ? (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString() : null);
        if (sourceIp == null) return Results.BadRequest();
        var files = new Dictionary<string, IncomingFile>(StringComparer.Ordinal);
        long totalBytes = 0;
        try
        {
            foreach (var (id, file) in request.Files)
            {
                if (string.IsNullOrWhiteSpace(id) || file == null || file.Id != id || file.Size < 0 || file.Size > long.MaxValue / 2)
                    return Results.BadRequest();
                ValidateRelativePath(file.FileName);
                if (file.Sha256 != null && (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)))
                    return Results.BadRequest();
                totalBytes = checked(totalBytes + file.Size);
                files.Add(id, new IncomingFile(file, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()));
            }
        }
        catch (OverflowException) { return Results.BadRequest(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (InvalidDataException) { return Results.BadRequest(); }

        var reservation = new IncomingSession(Guid.NewGuid().ToString("N"), sourceIp, request.Info.Alias, files,
            ReceiveDirectory, _tasks.AddTask("接收自 " + request.Info.Alias)) { IsPending = true };
        bool busy;
        lock (_receiveLock)
        {
            busy = _incoming != null;
            if (!busy) _incoming = reservation;
        }
        if (busy) { _tasks.RemoveTask(reservation.Task.Id); return Results.StatusCode(409); }
        var confirmed = false;
        try
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, reservation.Cts.Token,
                reservation.Task.Cts.Token, _running.Token);
            var ask = ConfirmReceiveAsync;
            var decision = ask == null ? new LocalSendReceiveDecision(false, ReceiveDirectory) :
                await ask(new LocalSendIncomingRequest(request.Info.Alias,
                    files.Values.Select(item => new LocalSendIncomingFile(item.Info.Id, item.Info.FileName, item.Info.Size)).ToArray(), ReceiveDirectory), pending.Token);
            // A dialog can complete with a decision after its cancellation was requested.
            pending.Token.ThrowIfCancellationRequested();
            if (!decision.Accepted) return Results.StatusCode(403);
            var directory = Path.GetFullPath(decision.Directory);
            if (RuntimePaths.TestRoot is { } root && !IsWithin(directory, root)) return Results.StatusCode(403);
            AccessGrants.EnsureAccess(directory);
            EnsureSafeDirectory(directory, create: true);
            var acceptedFiles = decision.AcceptedFileIds == null ? files : files
                .Where(item => decision.AcceptedFileIds.Contains(item.Key, StringComparer.Ordinal))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            if (acceptedFiles.Count == 0) return Results.NoContent();
            IncomingSession session;
            lock (_receiveLock)
            {
                if (!ReferenceEquals(_incoming, reservation) || !Enabled) return Results.StatusCode(409);
                pending.Token.ThrowIfCancellationRequested();
                session = new IncomingSession(reservation.Id, sourceIp, request.Info.Alias, acceptedFiles, directory, reservation.Task);
                _incoming = session;
            }
            session.TaskCancellationRegistration = session.Task.Cts.Token.Register(() =>
            {
                lock (_receiveLock) if (ReferenceEquals(_incoming, session)) _incoming = null;
                session.Cancel();
                Interlocked.Exchange(ref session.Finished, 1);
                _tasks.UpdateProgress(session.Task.Id, session.Task.Progress,
                    $"已接收 {session.CompletedCount}/{session.Files.Count} 个文件，接收已取消。");
                TryCleanupSession(session);
            });
            Interlocked.Exchange(ref session.CleanupReady, 1);
            TryCleanupSession(session);
            confirmed = true;
            _tasks.UpdateProgress(session.Task.Id, 0, acceptedFiles.Count + " 个文件，等待上传");
            _ = ExpireSessionAsync(session);
            return Results.Json(new PrepareResponse(session.Id,
                acceptedFiles.ToDictionary(item => item.Key, item => item.Value.Token, StringComparer.Ordinal)), Json);
        }
        catch (OperationCanceledException) { return Results.StatusCode(403); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LocalSend receive preparation failed");
            return Results.StatusCode(500);
        }
        finally
        {
            lock (_receiveLock)
            {
                if (ReferenceEquals(_incoming, reservation)) _incoming = null;
            }
            reservation.Cancel();
            reservation.Cts.Dispose();
            if (!confirmed && reservation.Task.State == BackgroundTaskState.Running)
                _tasks.CancelTask(reservation.Task.Id);
        }
    }

    private async Task<IResult> UploadAsync(HttpContext context)
    {
        if (!Enabled || _running == null) return Results.StatusCode(503);
        var query = context.Request.Query;
        var sessionId = query["sessionId"].ToString();
        var fileId = query["fileId"].ToString();
        var token = query["token"].ToString();
        if (sessionId.Length == 0 || fileId.Length == 0 || token.Length == 0) return Results.BadRequest();
        var ip = (context.Connection.RemoteIpAddress is { } remoteIp ? (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString() : null);
        IncomingSession session;
        IncomingFile file;
        lock (_receiveLock)
        {
            var current = _incoming;
            if (current == null || current.Id != sessionId) return Results.StatusCode(409);
            if (ip != current.SourceIp || !current.Files.TryGetValue(fileId, out var found)
                || !TokenEquals(token, found.Token)) return Results.StatusCode(403);
            if (found.Completed || Interlocked.CompareExchange(ref found.Active, 1, 0) != 0) return Results.StatusCode(409);
            session = current;
            file = found;
            Interlocked.Increment(ref session.ActiveUploads);
        }
        context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
        string? temp = null;
        try
        {
            if (context.Request.ContentLength is { } declared && declared != file.Info.Size)
            { FinishIncoming(session, "上传长度不符。"); return Results.StatusCode(422); }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Cts.Token, _running.Token, session.Task.Cts.Token);
            var cancellationToken = linked.Token;
            var components = ValidateRelativePath(file.Info.FileName);
            var parent = components.Length == 1 ? session.Directory : Path.Combine([session.Directory, .. components[..^1]]);
            EnsureSafeDirectory(parent, create: true);
            _tasks.UpdateProgress(session.Task.Id, session.TotalBytes == 0 ? 0 :
                100d * Interlocked.Read(ref session.ProgressBytes) / session.TotalBytes, file.Info.FileName);
            temp = Path.Combine(parent, ".localsend-" + Guid.NewGuid().ToString("N") + ".part");
            long length = 0;
            using var hash = file.Info.Sha256 == null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    length += read;
                    if (length > file.Info.Size)
                    { FinishIncoming(session, "上传长度超过声明值。"); return Results.StatusCode(422); }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash?.AppendData(buffer.AsSpan(0, read));
                    var aggregate = Interlocked.Add(ref session.ProgressBytes, read);
                    Interlocked.Exchange(ref session.LastActivityTicks, DateTime.UtcNow.Ticks);
                    var now = Stopwatch.GetTimestamp();
                    var previous = Interlocked.Read(ref session.LastProgressTimestamp);
                    if (now - previous >= Stopwatch.Frequency / 10
                        && Interlocked.CompareExchange(ref session.LastProgressTimestamp, now, previous) == previous)
                        _tasks.UpdateProgress(session.Task.Id, session.TotalBytes == 0 ? 0 : 100d * aggregate / session.TotalBytes,
                            file.Info.FileName);
                }
                await output.FlushAsync(cancellationToken);
            }
            if (length != file.Info.Size)
            { FinishIncoming(session, "上传长度不符。"); return Results.StatusCode(422); }
            if (file.Info.Sha256 != null && !Convert.ToHexString(hash!.GetHashAndReset()).Equals(file.Info.Sha256, StringComparison.OrdinalIgnoreCase))
            { FinishIncoming(session, "文件校验失败。"); return Results.StatusCode(422); }
            EnsureSafeDirectory(parent, create: false);
            cancellationToken.ThrowIfCancellationRequested();
            MoveWithUniqueName(temp, parent, components[^1]);
            temp = null;
            // Serialize completion with the duplicate-upload check before Active is released.
            lock (_receiveLock) file.Completed = true;
            _tasks.UpdateProgress(session.Task.Id, session.TotalBytes == 0 ? 0 :
                100d * Interlocked.Read(ref session.ProgressBytes) / session.TotalBytes, file.Info.FileName);
            if (Interlocked.Increment(ref session.CompletedCount) == session.Files.Count)
                FinishIncoming(session);
            return Results.Ok();
        }
        catch (OperationCanceledException)
        {
            if (context.RequestAborted.IsCancellationRequested && !session.Cts.IsCancellationRequested)
                FinishIncoming(session, "上传连接中断。");
            return Results.StatusCode(409);
        }
        catch (IOException ex)
        {
            if (context.RequestAborted.IsCancellationRequested || ex is BadHttpRequestException)
            {
                FinishIncoming(session, "上传连接中断。");
                return Results.StatusCode(409);
            }
            _logger?.LogWarning(ex, "LocalSend write failed for {File}", file.Info.FileName);
            FinishIncoming(session, "保存文件失败：" + ex.Message);
            return Results.StatusCode(500);
        }
        catch (UnauthorizedAccessException ex)
        { FinishIncoming(session, "没有写入权限：" + ex.Message); return Results.StatusCode(500); }
        finally
        {
            Interlocked.Exchange(ref file.Active, 0);
            if (temp != null) try { File.Delete(temp); } catch { }
            Interlocked.Decrement(ref session.ActiveUploads);
            TryCleanupSession(session);
        }
    }

    private IResult CancelIncomingAsync(HttpContext context)
    {
        var id = context.Request.Query["sessionId"].ToString();
        IncomingSession? session;
        lock (_receiveLock)
        {
            session = _incoming;
            if (session == null || (id != session.Id && !(id.Length == 0 && session.IsPending)))
                return Results.StatusCode(409);
            if ((context.Connection.RemoteIpAddress is { } remoteIp ? (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString() : null) != session.SourceIp) return Results.StatusCode(403);
            _incoming = null;
        }
        if (session.IsPending)
        {
            session.Cancel();
            return Results.Ok();
        }
        FinishIncoming(session, "对方已取消。");
        return Results.Ok();
    }

    private async Task ExpireSessionAsync(IncomingSession session)
    {
        try
        {
            while (!session.Cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(1), session.Cts.Token); }
                catch (OperationCanceledException) { return; }
                if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref session.LastActivityTicks), DateTimeKind.Utc) > TimeSpan.FromMinutes(15))
                { FinishIncoming(session, "等待上传超时。"); return; }
            }
        }
        finally
        {
            Interlocked.Exchange(ref session.ExpirationDone, 1);
            TryCleanupSession(session);
        }
    }

    private void FinishIncoming(IncomingSession session, string? error = null)
    {
        if (Interlocked.CompareExchange(ref session.Finished, 1, 0) != 0) return;
        lock (_receiveLock) if (ReferenceEquals(_incoming, session)) _incoming = null;
        session.Cancel();
        if (session.Task.State == BackgroundTaskState.Running)
        {
            if (error == null) _tasks.CompleteTask(session.Task.Id);
            else if (session.CompletedCount > 0)
                _tasks.FailTask(session.Task.Id, $"{error} 已接收 {session.CompletedCount}/{session.Files.Count} 个文件。");
            else _tasks.FailTask(session.Task.Id, error);
        }
        TryCleanupSession(session);
    }

    private static void TryCleanupSession(IncomingSession session)
    {
        if (Volatile.Read(ref session.Finished) == 0 || Volatile.Read(ref session.ActiveUploads) != 0
            || Volatile.Read(ref session.CleanupReady) == 0 || Volatile.Read(ref session.ExpirationDone) == 0
            || Interlocked.CompareExchange(ref session.CleanupQueued, 1, 0) != 0) return;
        // Cancellation can invoke this from the registration itself; dispose off that callback.
        _ = Task.Run(() =>
        {
            session.TaskCancellationRegistration.Dispose();
            session.Cts.Dispose();
        });
    }

    private static bool TokenEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string[] ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] is '/' or '\\' || Path.IsPathFullyQualified(path))
            throw new InvalidDataException("无效的相对路径。");
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || part.Contains(':') || part.Any(char.IsControl)))
            throw new InvalidDataException("无效的相对路径。");
        return parts;
    }

    private static void EnsureSafeDirectory(string path, bool create)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint) || !Directory.Exists(current))
                    throw new IOException("接收路径包含符号链接或普通文件。");
            }
            else if (create) Directory.CreateDirectory(current);
            else throw new IOException("接收目录已改变。");
        }
    }

    private static void MoveWithUniqueName(string temp, string directory, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var number = 1; number <= 10000; number++)
        {
            var candidate = Path.Combine(directory, number == 1 ? name : $"{stem} {number}{extension}");
            try { File.Move(temp, candidate); return; }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new IOException("同名文件过多，无法保存。");
    }
}
