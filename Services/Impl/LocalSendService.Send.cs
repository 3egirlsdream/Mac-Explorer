using System.Net;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MacExplorer.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace MacExplorer.Services.Impl;

public sealed partial class LocalSendService
{
    private sealed record SendFile(string Path, string RelativeName, long Size);

    public async Task SendAsync(LocalSendDevice device, IReadOnlyList<string> paths)
    {
        var task = _tasks.AddTask("发送到 " + device.Alias);
        if (!await _sendGate.WaitAsync(0))
        {
            _tasks.FailTask(task.Id, "已有发送任务正在进行。");
            return;
        }
        _sending = task;
        string? sessionId = null;
        var sentCount = 0;
        var totalCount = 0;
        try
        {
            if (!Enabled || _running == null) throw new InvalidOperationException("LocalSend 已关闭。");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.Cts.Token, _running.Token);
            var token = linked.Token;
            var (files, skipped) = await Task.Run(() => CollectFiles(paths, token), token);
            totalCount = files.Count;
            if (files.Count == 0) throw new InvalidOperationException($"没有可发送的文件。已跳过 {skipped.Count} 项：{string.Join("；", skipped.Take(3))}");
            var fileMap = files.Select((file, index) => (Id: index.ToString(System.Globalization.CultureInfo.InvariantCulture), File: file))
                .ToDictionary(item => item.Id, item => new FileInfoDto(item.Id, item.File.RelativeName, item.File.Size, "application/octet-stream"));
            using var client = CreatePeerClient(device);
            var prepare = new PrepareRequest(SelfInfo(), fileMap);
            var response = await PostPrepareAsync(client, PeerUri(device, "prepare-upload"), prepare, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                var pin = RequestPinAsync == null ? null : await RequestPinAsync(token);
                if (string.IsNullOrWhiteSpace(pin)) throw new InvalidOperationException("对方要求 PIN，发送已取消。");
                response = await PostPrepareAsync(client,
                    PeerUri(device, "prepare-upload?pin=" + Uri.EscapeDataString(pin)), prepare, token);
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NoContent) { _tasks.CompleteTask(task.Id); return; }
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("PIN 错误。");
                if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("对方拒绝接收。");
                if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException("对方正在处理其他传输。");
                response.EnsureSuccessStatusCode();
                var accepted = await response.Content.ReadFromJsonAsync<PrepareResponse>(Json, token)
                    ?? throw new InvalidDataException("对方没有返回传输会话。");
                sessionId = accepted.SessionId;
                if (await UploadAcceptedAsync(client, device, task, files, accepted, skipped, token, count => sentCount = count))
                    sessionId = null;
            }
        }
        catch (OperationCanceledException)
        {
            _tasks.UpdateProgress(task.Id, task.Progress, $"已发送 {sentCount}/{totalCount} 个文件，发送已取消。");
            if (task.State == BackgroundTaskState.Running)
            {
                if (sentCount > 0) _tasks.FailTask(task.Id, $"发送已取消，已发送 {sentCount} 个文件。");
                else _tasks.CancelTask(task.Id);
            }
        }
        catch (Exception ex)
        {
            _tasks.FailTask(task.Id, ex.Message);
            _logger?.LogWarning(ex, "LocalSend transfer to {Alias} failed", device.Alias);
        }
        finally
        {
            if (sessionId != null)
            {
                try
                {
                    using var client = CreatePeerClient(device);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var response = await client.PostAsync(PeerUri(device, "cancel?sessionId=" + Uri.EscapeDataString(sessionId)), null, timeout.Token);
                }
                catch { }
            }
            _sending = null;
            _sendGate.Release();
        }
    }

    private static async Task<HttpResponseMessage> PostPrepareAsync(HttpClient client, Uri uri,
        PrepareRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try { return await client.PostAsJsonAsync(uri, request, Json, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("等待对方确认超时。"); }
    }

    private async Task<bool> UploadAcceptedAsync(HttpClient client, LocalSendDevice device, BackgroundTaskInfo task,
        IReadOnlyList<SendFile> files, PrepareResponse accepted, IReadOnlyList<string> skipped, CancellationToken token,
        Action<int> onCompleted)
    {
        if (string.IsNullOrWhiteSpace(accepted.SessionId)) throw new InvalidDataException("对方返回了无效会话。");
        var total = files.Select((file, index) => accepted.Files.ContainsKey(index.ToString()) ? file.Size : 0).Sum();
        long sentBytes = 0;
        var sentCount = 0;
        var failures = new List<string>();
        var lastProgressTimestamp = 0L;
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            var id = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!accepted.Files.TryGetValue(id, out var fileToken)) continue;
            token.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length != file.Size) throw new IOException("源文件在传输前发生变化。");
                _tasks.UpdateProgress(task.Id, total == 0 ? 0 : 100d * sentBytes / total, file.RelativeName);
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromMinutes(15));
                using var content = new ProgressFileContent(stream, file.Size, bytes =>
                {
                    idle.CancelAfter(TimeSpan.FromMinutes(15));
                    var now = Stopwatch.GetTimestamp();
                    if (now - lastProgressTimestamp < Stopwatch.Frequency / 10) return;
                    lastProgressTimestamp = now;
                    _tasks.UpdateProgress(task.Id, total == 0 ? 0 : 100d * (sentBytes + bytes) / total, file.RelativeName);
                });
                using var response = await client.PostAsync(PeerUri(device,
                    "upload?sessionId=" + Uri.EscapeDataString(accepted.SessionId) + "&fileId=" + Uri.EscapeDataString(id)
                    + "&token=" + Uri.EscapeDataString(fileToken)), content, idle.Token);
                response.EnsureSuccessStatusCode();
                sentBytes += file.Size;
                _tasks.UpdateProgress(task.Id, total == 0 ? 0 : 100d * sentBytes / total, file.RelativeName);
                sentCount++;
                onCompleted(sentCount);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { failures.Add(file.RelativeName + "：传输长时间无进展。"); break; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failures.Add(file.RelativeName + "：" + ex.Message);
                if (ex is HttpRequestException) break;
            }
        }
        var declined = files.Count - accepted.Files.Count;
        if (failures.Count > 0 || declined > 0 || skipped.Count > 0)
            _tasks.FailTask(task.Id, $"已发送 {sentCount}/{files.Count} 个文件；拒绝 {declined}、跳过 {skipped.Count}、失败 {failures.Count}。",
                string.Join("\n", skipped.Concat(failures)));
        else
            _tasks.CompleteTask(task.Id);
        return failures.Count == 0;
    }

    private static (List<SendFile> Files, List<string> Skipped) CollectFiles(IReadOnlyList<string> paths, CancellationToken token)
    {
        var files = new List<SendFile>();
        var skipped = new List<string>();
        var roots = paths.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var root in roots.Where(path => !roots.Any(other => other != path && Directory.Exists(other)
            && path.StartsWith(Path.TrimEndingDirectorySeparator(other) + Path.DirectorySeparatorChar, StringComparison.Ordinal))))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Path.IsPathFullyQualified(root) || !File.Exists(root) && !Directory.Exists(root))
                { skipped.Add(root + " 不存在或不是本地文件"); continue; }
                if ((File.GetAttributes(root) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                { skipped.Add(root + " 是符号链接"); continue; }
                var baseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
                if (File.Exists(root))
                {
                    if (!IsRegularLocalFile(root)) { skipped.Add(root + " 不是普通文件"); continue; }
                    files.Add(new SendFile(root, baseName, new FileInfo(root).Length));
                    continue;
                }
                var before = files.Count;
                var stack = new Stack<(string Path, string Relative)>();
                stack.Push((root, baseName));
                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var (directory, relative) = stack.Pop();
                    try
                    {
                        var hadEntry = false;
                        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                        {
                            hadEntry = true;
                            var childName = relative + "/" + Path.GetFileName(child);
                            try
                            {
                                if ((File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                                { skipped.Add(childName + " 是符号链接"); continue; }
                                if (Directory.Exists(child)) stack.Push((child, childName));
                                else if (File.Exists(child) && IsRegularLocalFile(child))
                                    files.Add(new SendFile(child, childName, new FileInfo(child).Length));
                                else skipped.Add(childName + " 不是普通文件");
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            { skipped.Add(childName + " " + ex.Message); }
                        }
                        if (!hadEntry) skipped.Add(relative + " 是空目录");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { skipped.Add(relative + " " + ex.Message); }
                }
                if (files.Count == before && !skipped.Any(item => item.StartsWith(baseName + " ", StringComparison.Ordinal)))
                    skipped.Add(baseName + " 没有可发送的普通文件");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { skipped.Add(root + " " + ex.Message); }
        }
        return (files, skipped);
    }

    private static bool IsRegularLocalFile(string path)
    {
        if (!OperatingSystem.IsMacOS()) return File.Exists(path);
        var status = new byte[256];
        // Darwin's stat.st_mode follows the 32-bit st_dev field on arm64 and x64.
        return LStat(path, status) == 0 && (BitConverter.ToUInt16(status, 4) & 0xF000) == 0x8000;
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, [Out] byte[] status);

    private sealed class ProgressFileContent(FileStream source, long length, Action<long> progress) : HttpContent
    {
        protected override bool TryComputeLength(out long computedLength) { computedLength = length; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            long sent = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sent += read;
                progress(sent);
            }
            if (sent != length) throw new IOException("源文件在传输过程中发生变化。");
        }
    }
}
