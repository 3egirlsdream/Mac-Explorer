using MacExplorer.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MacExplorer.Models;

namespace MacExplorer.Platforms.MacCatalyst.Services;

internal static class LocalFileCopy
{
    private sealed class PartialCopy
    {
        public required string Temporary;
        public long Offset;
        public bool Created;
        public bool Committed;
        public string? Identity;
    }
    private sealed record CopyEntry(string Path, bool IsDirectory, string? LinkTarget, long Length, DateTime Modified, string? Identity, bool IsRuntimeFile = false);

    public static string Copy(string sourcePath, string destinationDirectory,
        IProgress<FileOperationProgress>? progress, CancellationToken ct)
    {
        MacExplorer.Services.DirectoryAccess.Current.EnsureAccess(sourcePath);
        MacExplorer.Services.DirectoryAccess.Current.EnsureAccess(destinationDirectory);
        sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        var source = ReadEntry(sourcePath);
        if (source.IsRuntimeFile) throw new IOException($"不能复制进程通信文件：{sourcePath}");
        if (source.IsDirectory && source.LinkTarget == null)
        {
            var realSource = ResolveDirectory(sourcePath);
            var realDestination = ResolveDirectory(destinationDirectory);
            if (realDestination == realSource || realDestination.StartsWith(realSource.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new IOException("不能将文件夹复制到它自身或其子文件夹中。");
        }

        var context = FileTaskContext.Current;
        var destinationPath = context?.Remember("local-copy-output:" + sourcePath + ":" + destinationDirectory,
            () => GetDestinationPath(destinationDirectory, Path.GetFileName(sourcePath), source.IsDirectory))
            ?? GetDestinationPath(destinationDirectory, Path.GetFileName(sourcePath), source.IsDirectory);
        MacExplorer.Services.DirectoryAccess.Current.EnsureAccess(destinationPath);
        var entries = new List<CopyEntry>();
        var errors = new List<Exception>();
        var clock = Stopwatch.StartNew();
        long lastReport = -100;
        long completed = 0;
        long total = 0;
        var skipped = 0;

        void Report(string path, bool scanning = false, bool force = false)
        {
            if (context != null)
            {
                context.Task.BytesTransferred = completed; context.Task.TotalBytes = total;
                context.Task.BytesPerSecond = completed / Math.Max(.001, clock.Elapsed.TotalSeconds);
            }
            if (!force && clock.ElapsedMilliseconds - lastReport < 100) return;
            lastReport = clock.ElapsedMilliseconds;
            progress?.Report(new FileOperationProgress
            {
                Percentage = scanning || total == 0 ? 0 : Math.Min(99.9, completed * 100d / total),
                CurrentFile = scanning ? $"正在统计文件：{path}" : path,
                SkippedCount = skipped
            });
        }

        void RecordError(string path, Exception ex)
            => errors.Add(new IOException($"{path}: {ex.Message}", ex));

        void Scan(CopyEntry entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsRuntimeFile)
            {
                skipped++;
                context?.ReportItem(entry.Path, null, FileTaskItemState.Skipped,
                    MacExplorer.Services.Impl.LocalizationText.Get("已跳过运行时通信文件。"));
                return;
            }
            entries.Add(entry);
            total += entry.Length + 1; // Empty files, directories and links also advance progress.
            Report(entry.Path, scanning: true);
            if (!entry.IsDirectory || entry.LinkTarget != null) return;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(entry.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    try { Scan(ReadEntry(path)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        RecordError(path, ex);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordError(entry.Path, ex);
            }
        }

        Report(sourcePath, scanning: true, force: true);
        var manifest = context?.Remember("local-copy-manifest:" + sourcePath + ":" + destinationDirectory, () =>
        { Scan(source); return entries.ToArray(); });
        if (manifest == null) Scan(source);
        else if (entries.Count == 0) { entries.AddRange(manifest); total = entries.Sum(e => e.Length + 1); }
        if (context != null) context.Task.TotalItems = entries.Count;
        var buffer = new byte[128 * 1024];
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            FileTaskContext.Checkpoint();
            var destination = entry.Path == sourcePath
                ? destinationPath
                : Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, entry.Path));
            var before = completed;
            if (context?.IsDone("local-copy:" + entry.Path + ":" + destination) == true)
            {
                if (context.Remember("local-output-identity:" + destination, () => FileIdentitySnapshot.Capture(destination, false))
                    != FileIdentitySnapshot.Capture(destination, false))
                    throw new IOException(MacExplorer.Services.Impl.LocalizationText.Get("暂存文件已变化，无法继续。"));
                completed += entry.Length + 1; continue;
            }
            try
            {
                if (entry.LinkTarget != null)
                {
                    // Preserve relative, dangling and cyclic links without reading their targets.
                    if (entry.IsDirectory) Directory.CreateSymbolicLink(destination, entry.LinkTarget);
                    else File.CreateSymbolicLink(destination, entry.LinkTarget);
                }
                else if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                }
                else
                {
                    var state = context?.Remember("local-file:" + destination, () => new PartialCopy
                    { Temporary = destination + "." + context.Task.Id + ".fkfinder-tmp" })
                        ?? new PartialCopy { Temporary = destination + "." + Guid.NewGuid().ToString("N") + ".fkfinder-tmp" };
                    context?.RegisterCleanup("local-file:" + destination, () =>
                    {
                        if (state.Created && !state.Committed && File.Exists(state.Temporary)
                            && FileIdentitySnapshot.Capture(state.Temporary, false)?.Identity == state.Identity)
                            File.Delete(state.Temporary);
                    });
                    if (!state.Committed)
                    {
                        if (ReadEntry(entry.Path) != entry) throw new IOException(MacExplorer.Services.Impl.LocalizationText.Get("源项目已变化，请重新创建任务。"));
                        using var input = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using (var output = new FileStream(state.Temporary, state.Created ? FileMode.Open : FileMode.CreateNew,
                            FileAccess.ReadWrite, FileShare.None))
                        {
                            state.Created = true;
                            state.Identity ??= FileIdentitySnapshot.Capture(state.Temporary, false)?.Identity;
                            if (FileIdentitySnapshot.Capture(state.Temporary, false)?.Identity != state.Identity)
                                throw new IOException(MacExplorer.Services.Impl.LocalizationText.Get("暂存文件已变化，无法继续。"));
                            if (output.Length != state.Offset) throw new IOException(MacExplorer.Services.Impl.LocalizationText.Get("暂存文件已变化，无法继续。"));
                            var verify = new byte[buffer.Length]; long remaining = state.Offset;
                            while (remaining > 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                var count = (int)Math.Min(buffer.Length, remaining);
                                input.ReadExactly(buffer.AsSpan(0, count)); output.ReadExactly(verify.AsSpan(0, count));
                                if (!buffer.AsSpan(0, count).SequenceEqual(verify.AsSpan(0, count)))
                                    throw new IOException(MacExplorer.Services.Impl.LocalizationText.Get("暂存文件已变化，无法继续。"));
                                remaining -= count;
                            }
                            completed += state.Offset;
                            try
                            {
                                int read;
                                while ((read = input.Read(buffer)) > 0)
                                {
                                    ct.ThrowIfCancellationRequested(); FileTaskContext.Checkpoint();
                                    if (ReadEntry(entry.Path) != entry) throw new IOException(MacExplorer.Services.Impl.LocalizationText.Get("源项目已变化，请重新创建任务。"));
                                    output.Write(buffer, 0, read); state.Offset += read; completed += read;
                                    Report(entry.Path);
                                }
                                output.Flush();
                            }
                            catch (OperationCanceledException) { output.Dispose(); context?.Cleanup(); if (context == null) File.Delete(state.Temporary); throw; }
                        }
                        if (ReadEntry(entry.Path) != entry) throw new IOException(MacExplorer.Services.Impl.LocalizationText.Get("源项目已变化，请重新创建任务。"));
                        File.Move(state.Temporary, destination, false); state.Committed = true;
                    }
                    CopyMetadata(entry.Path, destination);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context?.ReportItem(entry.Path, destination, FileTaskItemState.Failed, ex.Message);
                RecordError(entry.Path, ex);
                completed = before + entry.Length + 1;
                continue;
            }
            context?.Remember("local-output-identity:" + destination, () => FileIdentitySnapshot.Capture(destination, false));
            context?.MarkDone("local-copy:" + entry.Path + ":" + destination);
            context?.ReportItem(entry.Path, destination, FileTaskItemState.Completed);
            completed = before + entry.Length + 1;
            Report(entry.Path);
        }

        // Apply directory permissions last so read-only folders can be populated first.
        foreach (var entry in entries.AsEnumerable().Reverse().Where(e => e.IsDirectory && e.LinkTarget == null))
        {
            ct.ThrowIfCancellationRequested();
            FileTaskContext.Checkpoint();
            var destination = entry.Path == sourcePath ? destinationPath
                : Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, entry.Path));
            try
            {
                CopyMetadata(entry.Path, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordError(entry.Path, ex);
            }
        }

        ct.ThrowIfCancellationRequested();
        if (errors.Count > 0)
            throw new AggregateException($"复制未完整完成，{errors.Count} 项失败。目标中保留了已复制的文件。", errors);
        progress?.Report(new FileOperationProgress { Percentage = 100, CurrentFile = sourcePath, SkippedCount = skipped });
        return destinationPath;
    }

    private static CopyEntry ReadEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        var target = info.LinkTarget;
        if (!isDirectory && target == null && OperatingSystem.IsMacOS())
        {
            if (LStat(path, out var status) != 0)
                throw new IOException($"无法读取文件类型：{path}", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            var type = status.Mode & 0xf000;
            if (type is 0xc000 or 0x1000) // Unix socket / FIFO: process endpoints, not stored file data.
                return new CopyEntry(path, false, null, 0, info.LastWriteTimeUtc, FileIdentitySnapshot.Capture(path, false)?.Identity, IsRuntimeFile: true);
            if (type != 0x8000)
                throw new IOException($"不支持复制此特殊文件：{path}");
        }
        return new CopyEntry(path, isDirectory, target,
            isDirectory || target != null ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc, FileIdentitySnapshot.Capture(path, false)?.Identity);
    }

    // Darwin's 64-bit struct stat (macOS SDK sys/stat.h).
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacStat
    {
        [FieldOffset(4)] public ushort Mode;
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacStat status);

    private static void CopyMetadata(string source, string destination)
    {
        if (OperatingSystem.IsMacOS())
        {
            // COPYFILE_METADATA | COPYFILE_NOFOLLOW: preserve timestamps, modes,
            // ACLs and extended attributes (including Finder tags).
            if (CopyFileMetadata(source, destination, IntPtr.Zero, 0x000c0007) != 0)
                throw new IOException($"无法复制文件属性：{source}", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            return;
        }
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "copyfile", SetLastError = true)]
    private static extern int CopyFileMetadata(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, IntPtr state, uint flags);

    private static string ResolveDirectory(string path, int linkDepth = 0)
    {
        if (linkDepth > 40) throw new IOException($"目录符号链接层级过深：{path}");
        var current = Path.GetPathRoot(path)!;
        foreach (var part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            var target = directory.ResolveLinkTarget(true);
            current = target == null ? directory.FullName : ResolveDirectory(target.FullName, linkDepth + 1);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static string GetDestinationPath(string directory, string name, bool isDirectory)
    {
        var path = Path.Combine(directory, name);
        var extension = isDirectory ? "" : Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        for (var index = 1; File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget != null; index++)
            path = Path.Combine(directory, $"{stem} 副本{(index == 1 ? "" : $" {index}")}{extension}");
        return path;
    }
}
