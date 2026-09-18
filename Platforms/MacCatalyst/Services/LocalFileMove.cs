using System.Diagnostics;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>Publish one complete item at a time; never roll back a user-owned destination.</summary>
internal static class LocalFileMove
{
    public static void Move(IReadOnlyList<string> sourcePaths, string destinationDirectory,
        IProgress<FileOperationProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sources = sourcePaths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.Ordinal).ToArray();
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);

        for (var index = 0; index < sources.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var source = sources[index];
            var destination = Path.Combine(directory, Path.GetFileName(source));
            if (source == destination) continue;
            // Read attributes rather than interpreting Exists(false) as a successful move.
            var attributes = File.GetAttributes(source);
            if (File.Exists(destination) || Directory.Exists(destination) || new FileInfo(destination).LinkTarget != null)
                throw new IOException($"移动目标已存在：{destination}。跨卷移动不会合并或覆盖已有内容。");

            var staging = Path.Combine(directory, $".MacExplorer-move-{Guid.NewGuid():N}.fkfinder-tmp");
            Directory.CreateDirectory(staging);
            try
            {
                var skipped = 0;
                var itemIndex = index;
                var relay = new MoveProgress(p =>
                {
                    skipped = p.SkippedCount;
                    progress?.Report(new FileOperationProgress
                    {
                        Percentage = (itemIndex * 100d + Math.Clamp(p.Percentage, 0, 99.9)) / sources.Length,
                        CurrentFile = p.CurrentFile,
                        SkippedCount = skipped
                    });
                });
                // Reuse link-aware copying and metadata preservation instead of a
                // second recursive walker that follows links or hides read failures.
                var staged = LocalFileCopy.Copy(source, staging, relay, ct);
                if (skipped > 0)
                    throw new IOException($"源目录含有 {skipped} 个无法搬移的运行时通信文件；未删除来源，请停止相关程序后重试。");
                ct.ThrowIfCancellationRequested();
                if (!NewFileWriter.TryPublish(staged, destination, attributes.HasFlag(FileAttributes.Directory)))
                    throw new IOException($"移动期间目标名称被占用：{destination}。来源文件未删除。");

                // Finish this committed item even if cancellation arrives after the
                // rename. Later items still honor cancellation. A delete failure
                // leaves the complete destination in place; there is no rollback.
                if (attributes.HasFlag(FileAttributes.Directory))
                    Directory.Delete(source, recursive: !attributes.HasFlag(FileAttributes.ReparsePoint));
                else File.Delete(source);
                progress?.Report(new FileOperationProgress
                {
                    Percentage = (index + 1) * 100d / sources.Length,
                    CurrentFile = source
                });
            }
            finally
            {
                // Only this invocation's private staging directory is ours to remove.
                // Never delete destination, including after cancellation or a partial batch.
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Debug.WriteLine($"清理移动暂存失败：{staging}: {ex}"); }
            }
        }
    }

    private sealed class MoveProgress(Action<FileOperationProgress> report) : IProgress<FileOperationProgress>
    {
        // Synchronous: skipped-count and cancellation decisions precede publication.
        public void Report(FileOperationProgress value) => report(value);
    }
}
