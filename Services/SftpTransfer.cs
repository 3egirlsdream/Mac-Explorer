using System.Diagnostics;
using System.Security.Cryptography;
using MacExplorer.Models;
using Renci.SshNet;

namespace MacExplorer.Services;

/// <summary>Transfers use task-owned temporary outputs and never infer ownership from a filename.</summary>
internal static class SftpTransfer
{
    private sealed record Item(string Source, string Relative, bool Directory, string? Link, long Size, DateTime Modified);
    private sealed class Output
    {
        public required string Path;
        public required string Temporary;
        public bool Created;
        public bool Committed;
        public long Offset;
        public string? Backup;
        public bool BackupOwned;
    }
    internal static async Task<string> CopyAsync(string source, string directory, FileTaskContext context,
        Func<string, SftpClient> client, IProgress<FileOperationProgress>? progress,
        string? exactTarget = null, bool replaceExisting = false)
    {
        return await Task.Run(async () =>
        {
            bool Remote(string path) => VirtualPath.IsRemotePath(path);
            string Raw(string path) => VirtualPath.ParseRemotePath(path).RemotePath;
            string Join(string dir, string name) => Remote(dir) ? dir.TrimEnd('/') + "/" + name : Path.Combine(dir, name);
            bool Exists(string path) => Remote(path) ? client(path).Exists(Raw(path))
                : File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget != null;
            Item Inspect(string path, string relative)
            {
                if (Remote(path))
                {
                    var attrs = client(path).GetAttributes(Raw(path));
                    return new(path, relative, attrs.IsDirectory, attrs.IsSymbolicLink ? "" : null,
                        attrs.IsDirectory ? 0 : attrs.Size, attrs.LastWriteTimeUtc);
                }
                var info = (File.GetAttributes(path) & FileAttributes.Directory) != 0
                    ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                return new(path, relative, info is DirectoryInfo, info.LinkTarget, info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc);
            }
            var manifest = context.Remember("manifest:" + source + ":" + directory, () =>
            {
                var items = new List<Item>();
                void Scan(string path, string relative)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var item = Inspect(path, relative); items.Add(item);
                    if (!item.Directory || item.Link != null) return;
                    var children = Remote(path)
                        ? client(path).ListDirectory(Raw(path)).Where(i => i.Name is not ("." or "..")).Select(i => Join(path, i.Name)).ToArray()
                        : System.IO.Directory.GetFileSystemEntries(path);
                    foreach (var child in children) Scan(child, relative.Length == 0 ? Path.GetFileName(child) : relative + "/" + Path.GetFileName(child));
                }
                Scan(source, ""); return items;
            });
            var root = context.Remember("output-root:" + source + ":" + directory, () =>
            {
                if (exactTarget != null) return exactTarget;
                var name = Path.GetFileName(source); var candidate = Join(directory, name); var index = 1;
                while (Exists(candidate)) candidate = Join(directory, Path.GetFileNameWithoutExtension(name) + " " + (++index) + Path.GetExtension(name));
                return candidate;
            });
            context.Task.TotalItems = manifest.Count;
            var total = manifest.Sum(i => i.Size); var clock = Stopwatch.StartNew(); long attemptBytes = 0; long finishedBytes = 0;
            Stream OpenRead(string path) => Remote(path) ? client(path).OpenRead(Raw(path)) : File.OpenRead(path);
            void Delete(string path) { if (Remote(path)) client(path).DeleteFile(Raw(path)); else File.Delete(path); }
            foreach (var item in manifest)
            {
                await context.CheckpointAsync();
                var target = item.Relative.Length == 0 ? root : Join(root, item.Relative);
                var output = context.Remember("transfer-output:" + target, () => new Output
                { Path = target, Temporary = target + "." + context.Task.Id + ".fkfinder-tmp" });
                var original = context.Remember<Item?>("transfer-original:" + target, () => replaceExisting && Exists(target) ? Inspect(target, "") : null);
                context.RegisterCleanup("transfer:" + target, () =>
                {
                    if (output.BackupOwned && output.Backup != null && Exists(output.Backup) && !Exists(target))
                        client(target).RenameFile(Raw(output.Backup), Raw(target));
                    if (output.Created && !output.Committed && Exists(output.Temporary)) Delete(output.Temporary);
                    if (output.Committed && output.BackupOwned && output.Backup != null && Exists(output.Backup)) Delete(output.Backup);
                });
                if (output.Committed) { finishedBytes += item.Size; continue; }
                if (Inspect(item.Source, item.Relative) != item) throw new IOException(Impl.LocalizationText.Get("源项目已变化，请重新创建任务。"));
                if (!Remote(target)) DirectoryAccess.Current.EnsureAccess(target);
                if (item.Link != null)
                {
                    if (Remote(item.Source))
                    {
                        context.Warnings.Add(Impl.LocalizationText.Get("已跳过远程符号链接：{0}", item.Source));
                        context.ReportItem(item.Source, null, FileTaskItemState.Skipped,
                            Impl.LocalizationText.Get("远程符号链接不支持复制。"));
                        output.Committed = true; continue;
                    }
                    if (Exists(target)) throw new IOException(Impl.LocalizationText.Get("目标位置发生冲突，请检查已复制的文件。"));
                    if (Remote(target)) client(target).SymbolicLink(item.Link, Raw(target));
                    else if (item.Directory) Directory.CreateSymbolicLink(target, item.Link);
                    else File.CreateSymbolicLink(target, item.Link);
                    output.Committed = true;
                    context.ReportItem(item.Source, target, FileTaskItemState.Completed); continue;
                }
                if (item.Directory)
                {
                    if (!output.Created && Exists(target)) throw new IOException(Impl.LocalizationText.Get("目标位置发生冲突，请检查已复制的文件。"));
                    if (!output.Created)
                    {
                        if (Remote(target)) client(target).CreateDirectory(Raw(target)); else Directory.CreateDirectory(target);
                        output.Created = true;
                    }
                    output.Committed = true;
                    context.ReportItem(item.Source, target, FileTaskItemState.Completed); continue;
                }
                try
                {
                    if (!replaceExisting && Exists(target)) throw new IOException(Impl.LocalizationText.Get("目标位置发生冲突，请检查已复制的文件。"));
                    using var input = OpenRead(item.Source);
                    using (Stream destination = Remote(target)
                        ? client(target).Open(Raw(output.Temporary), output.Created ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite)
                        : new FileStream(output.Temporary, output.Created ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        output.Created = true;
                        var available = Remote(target) ? client(target).GetAttributes(Raw(output.Temporary)).Size : destination.Length;
                        if (available < 0 || available > item.Size) throw new IOException(Impl.LocalizationText.Get("暂存文件已变化，无法继续。"));
                        output.Offset = available;
                        if (output.Offset > 0)
                        {
                            using var left = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            using var right = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            var a = new byte[128 * 1024]; var b = new byte[a.Length]; long remaining = output.Offset;
                            while (remaining > 0)
                            {
                                context.CancellationToken.ThrowIfCancellationRequested();
                                var count = (int)Math.Min(a.Length, remaining);
                                input.ReadExactly(a.AsSpan(0, count)); destination.ReadExactly(b.AsSpan(0, count));
                                left.AppendData(a.AsSpan(0, count)); right.AppendData(b.AsSpan(0, count)); remaining -= count;
                            }
                            if (!left.GetHashAndReset().SequenceEqual(right.GetHashAndReset())) throw new IOException(Impl.LocalizationText.Get("暂存文件已变化，无法继续。"));
                        }
                        var buffer = new byte[128 * 1024]; int read;
                        while ((read = input.Read(buffer)) > 0)
                        {
                            await context.CheckpointAsync();
                            if (Inspect(item.Source, item.Relative) != item) throw new IOException(Impl.LocalizationText.Get("源项目已变化，请重新创建任务。"));
                            destination.Write(buffer, 0, read); output.Offset += read; attemptBytes += read;
                            context.Task.BytesTransferred = finishedBytes + output.Offset;
                            context.Task.TotalBytes = total;
                            context.Task.BytesPerSecond = attemptBytes / Math.Max(.001, clock.Elapsed.TotalSeconds);
                            context.Task.Progress = total == 0 ? 0 : context.Task.BytesTransferred * 100d / total;
                            context.Task.CurrentFile = item.Source;
                            progress?.Report(new() { CurrentFile = item.Source, Percentage = context.Task.Progress });
                            context.Publish();
                        }
                        destination.Flush();
                    }
                    await context.CheckpointAsync();
                    if (Inspect(item.Source, item.Relative) != item) throw new IOException(Impl.LocalizationText.Get("源项目已变化，请重新创建任务。"));
                    using (context.Atomic())
                    {
                        if (replaceExisting && Remote(target) && Exists(target))
                        {
                            if (original == null || Inspect(target, "") != original)
                                throw new IOException(Impl.LocalizationText.Get("远程文件已变化，未覆盖服务器内容。"));
                            output.Backup ??= target + "." + context.Task.Id + ".backup.fkfinder-tmp";
                            if (!output.BackupOwned && Exists(output.Backup)) throw new IOException(Impl.LocalizationText.Get("目标位置发生冲突，请检查已复制的文件。"));
                            output.BackupOwned = true;
                            client(target).RenameFile(Raw(target), Raw(output.Backup));
                        }
                        if (Remote(target)) client(target).RenameFile(Raw(output.Temporary), Raw(target));
                        else File.Move(output.Temporary, target, false);
                        output.Committed = true;
                        if (output.BackupOwned && output.Backup != null && Exists(output.Backup))
                            try { Delete(output.Backup); output.BackupOwned = false; } catch (Exception ex) { context.Warnings.Add(ex.Message); }
                    }
                    finishedBytes += item.Size;
                    context.MarkDone("transfer:" + target);
                    context.ReportItem(item.Source, target, FileTaskItemState.Completed);
                }
                catch (OperationCanceledException)
                { context.Cleanup(); throw; }
                catch (Exception ex)
                {
                    if (output.BackupOwned && output.Backup != null)
                        try { if (Exists(output.Backup) && !Exists(target)) client(target).RenameFile(Raw(output.Backup), Raw(target)); }
                        catch (Exception recovery) { context.Warnings.Add(output.Backup + ": " + recovery.Message); }
                    context.ReportItem(item.Source, target, FileTaskItemState.Failed, ex.Message); throw;
                }
            }
            return root;
        }, context.CancellationToken);
    }
}
