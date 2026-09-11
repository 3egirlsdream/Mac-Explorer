using System.Diagnostics;
using System.Runtime.InteropServices;
using MacExplorer.Models;

namespace MacExplorer.Platforms.MacCatalyst.Services;

internal static class LocalFileCopy
{
    private sealed record CopyEntry(string Path, bool IsDirectory, string? LinkTarget, long Length, bool IsRuntimeFile = false);

    public static string Copy(string sourcePath, string destinationDirectory,
        IProgress<FileOperationProgress>? progress, CancellationToken ct)
    {
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

        var destinationPath = GetDestinationPath(destinationDirectory, Path.GetFileName(sourcePath), source.IsDirectory);
        var entries = new List<CopyEntry>();
        var errors = new List<Exception>();
        var clock = Stopwatch.StartNew();
        long lastReport = -100;
        long completed = 0;
        long total = 0;
        var skipped = 0;

        void Report(string path, bool scanning = false, bool force = false)
        {
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
        Scan(source);
        var buffer = new byte[128 * 1024];
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var destination = entry.Path == sourcePath
                ? destinationPath
                : Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, entry.Path));
            var before = completed;
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
                    using (var input = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        try
                        {
                            int read;
                            while ((read = input.Read(buffer)) > 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                output.Write(buffer, 0, read);
                                completed += read;
                                Report(entry.Path);
                            }
                        }
                        catch
                        {
                            // Only remove the incomplete file created by this operation.
                            output.Dispose();
                            File.Delete(destination);
                            throw;
                        }
                    }
                    CopyMetadata(entry.Path, destination);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordError(entry.Path, ex);
            }
            completed = before + entry.Length + 1;
            Report(entry.Path);
        }

        // Apply directory permissions last so read-only folders can be populated first.
        foreach (var entry in entries.AsEnumerable().Reverse().Where(e => e.IsDirectory && e.LinkTarget == null))
        {
            ct.ThrowIfCancellationRequested();
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
                return new CopyEntry(path, false, null, 0, IsRuntimeFile: true);
            if (type != 0x8000)
                throw new IOException($"不支持复制此特殊文件：{path}");
        }
        return new CopyEntry(path, isDirectory, target,
            isDirectory || target != null ? 0 : ((FileInfo)info).Length);
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
