using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Services;

public sealed partial class CompositeFileService
{
    internal sealed class InventoryItem(FileSystemEntry entry)
    {
        internal readonly FileSystemEntry Entry = entry;
        internal readonly List<InventoryItem> Children = [];
        internal LocalFileCopy.CopyEntry? CopyMetadata;
        internal FileIdentitySnapshot? Identity;
        internal bool Counted;
        internal int Count = 1;
        internal long Bytes = entry.IsDirectory || entry.IsSymbolicLink ? 0 : Math.Max(0, entry.Size);
    }

    internal sealed record TargetSnapshot(FileSystemEntry Entry, FileIdentitySnapshot? Identity);
    internal sealed record TransferChoice(string Target, FileConflictAction? Action, TargetSnapshot? Expected, bool FromBatch);

    internal sealed class OperationBatch(FileOperationDisplay display, IEqualityComparer<string> targetComparer)
    {
        internal readonly Dictionary<string, InventoryItem> Sources = new(StringComparer.Ordinal);
        internal readonly Dictionary<(string Source, string Target), TransferChoice> Choices = new();
        internal readonly Dictionary<string, TargetSnapshot?> Published = new(targetComparer);
        internal readonly Dictionary<string, List<string>> WatchedOutputs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _offsets = new(StringComparer.Ordinal);
        internal void ReportBytes(string source, long offset)
        {
            if (display.ByItems || !Sources.ContainsKey(source)) return;
            var previous = _offsets.GetValueOrDefault(source);
            if (offset <= previous) return;
            display.DoneBytes += offset - previous;
            _offsets[source] = offset;
        }
        internal void Finish(string source, bool subtree = true)
        {
            if (Sources.TryGetValue(source, out var item)) Count(item);
            void Count(InventoryItem node)
            {
                if (!node.Counted) { node.Counted = true; display.DoneItems++; }
                if (subtree) foreach (var child in node.Children) Count(child);
            }
        }
    }

    internal static Task<T> RunOperationAsync<T>(IFileService files, IBackgroundTaskManager? tasks,
        FileOperationPresentation presentation, FileTaskRequest request, Func<FileTaskContext, Task<T>> action,
        CancellationToken token = default)
    {
        if (files is not CompositeFileService composite || FileOperationHistoryService.IsUndoing)
            return FileTaskContext.RunAsync(tasks, request, action, token);
        if (FileTaskContext.Current?.Batch != null)
            return FileTaskContext.RunAsync(tasks, request, action, token);

        if (presentation == FileOperationPresentation.Trash && request.Sources.Count > 0 && request.Sources.All(VirtualPath.IsRemotePath))
            presentation = FileOperationPresentation.Delete;
        var display = new FileOperationDisplay(presentation);
        request = request with { Operation = display };
        return Task.Run(() => FileTaskContext.RunAsync(tasks ?? composite._taskManager, request, async context =>
        {
            // Keep the approved paths and resumable outputs together across retries.
            try
            {
                var batch = await context.StepAsync("inventory:" + request.Kind + ":" + request.Destination + ":" +
                    string.Join('\0', request.Sources), () => composite.PrepareBatchAsync(request, display, context));
                context.Batch = batch;
                return await action(context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failed = request.Sources.FirstOrDefault(path => context.Task.CurrentFile == path ||
                    context.Task.CurrentFile.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal));
                foreach (var path in request.Sources)
                {
                    if (path == failed) context.ReportItem(path, context.ResultFor(path)?.Destination, FileTaskItemState.Failed, ex.Message);
                    else if (context.ResultFor(path) == null) context.ReportItem(path, null, FileTaskItemState.NotAttempted);
                }
                throw;
            }
            finally { context.Batch = null; }
        }, token), token);
    }

    private async Task<OperationBatch> PrepareBatchAsync(FileTaskRequest request, FileOperationDisplay display, FileTaskContext context)
    {
        var dest = request.Destination;
        IEqualityComparer<string> comparer = dest != null && !VirtualPath.IsRemotePath(dest)
            ? RenameNameComparer.ForDirectory(dest) : StringComparer.Ordinal;
        var batch = new OperationBatch(display, comparer);
        var originals = new Dictionary<string, TargetSnapshot?>(comparer);
        var roots = new List<InventoryItem>();
        var emptyTrash = display.Kind == FileOperationPresentation.EmptyTrash;
        var paths = emptyTrash ? (await GetDirectoryContentsAsync(TrashDirectory, context.CancellationToken))
            .Select(e => e.FullPath).ToArray() : request.Sources;
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var root = await Scan(path, dest == null ? null : CombinePath(dest, Path.GetFileName(path)));
                roots.Add(root);
            }
            catch (Exception ex) when (emptyTrash && ex is IOException or UnauthorizedAccessException)
            { context.Warnings.Add(ex.Message); }
        }

        display.TotalItems = roots.Sum(n => n.Count);
        display.TotalBytes = roots.Sum(n => n.Bytes);
        display.ByItems = dest == null || display.TotalBytes == 0 || request.Kind == BackgroundTaskKind.Move &&
            roots.All(n => !VirtualPath.IsRemotePath(n.Entry.FullPath) && !IsCrossVolume(n.Entry.FullPath, dest));
        if (dest != null && _conflicts != null)
        {
            FileConflictDecision? remaining = null;
            var projected = new Dictionary<string, (InventoryItem Node, string Producer)>(comparer);
            foreach (var root in roots)
                await Decide(root, CombinePath(dest, Path.GetFileName(root.Entry.FullPath)), root.Entry.FullPath);

            foreach (var (key, choice) in batch.Choices)
            {
                if (choice.FromBatch || choice.Action == FileConflictAction.Skip) continue;
                await context.CheckpointAsync();
                context.Task.CurrentFile = key.Source;
                if (!Matches(choice.Expected, await SnapshotAsync(choice.Target)))
                    throw Changed();
            }

            async Task Decide(InventoryItem item, string target, string producer)
            {
                await context.CheckpointAsync();
                var entry = item.Entry;
                var key = (entry.FullPath, target);
                var fromBatch = projected.TryGetValue(target, out var prior);
                var original = originals.GetValueOrDefault(target);
                var existing = fromBatch ? prior.Node.Entry : original?.Entry;
                FileConflictAction? action = null;
                if (existing != null && !(request.Kind == BackgroundTaskKind.Move && entry.FullPath == target))
                {
                    var choice = remaining;
                    if (entry.IsDirectory != existing.IsDirectory && choice?.Action is FileConflictAction.Replace or FileConflictAction.ReplaceOlder)
                        choice = null;
                    if (choice == null)
                    {
                        display.BeginConfirmation();
                        try { choice = await _conflicts.ResolveAsync(new(entry, fromBatch ? AtPath(existing, target) : existing,
                            request.Kind == BackgroundTaskKind.Move, VirtualPath.IsRemotePath(target)), context.CancellationToken); }
                        finally { display.EndConfirmation(); }
                    }
                    if (choice.ApplyToRemaining) remaining = choice;
                    action = choice.Action;
                    if (action == FileConflictAction.ReplaceOlder && (!entry.IsDirectory && entry.LastModified <= existing.LastModified
                        || entry.IsDirectory != existing.IsDirectory)) action = FileConflictAction.Skip;
                    if (action == FileConflictAction.Skip)
                    {
                        batch.Choices[key] = new(target, action, original, fromBatch);
                        display.TotalBytes -= item.Bytes;
                        return;
                    }
                    if (action == FileConflictAction.KeepBoth || target == entry.FullPath)
                    {
                        var ext = entry.IsDirectory ? "" : Path.GetExtension(target);
                        var name = Path.GetFileNameWithoutExtension(target);
                        if (entry.IsDirectory) name = Path.GetFileName(target);
                        var parent = GetParentPath(target);
                        var number = 1;
                        do { target = CombinePath(parent, $"{name} ({number++}){ext}"); }
                        while (projected.ContainsKey(target) || await SnapshotAsync(target) != null);
                        action = null; fromBatch = false; original = null; existing = null;
                    }
                }
                batch.Choices[key] = new(target, action, original, fromBatch);
                if (fromBatch)
                {
                    batch.Published.TryAdd(target, null);
                    if (!batch.WatchedOutputs.TryGetValue(prior.Producer, out var watch))
                        batch.WatchedOutputs[prior.Producer] = watch = [];
                    watch.Add(target);
                }
                if (existing is { IsDirectory: true, IsSymbolicLink: false } && entry.IsDirectory && !entry.IsSymbolicLink)
                {
                    projected[target] = (item, producer);
                    foreach (var child in item.Children)
                        await Decide(child, CombinePath(target, Path.GetFileName(child.Entry.FullPath)), child.Entry.FullPath);
                }
                else ReserveTree(item, target, producer);
            }

            void ReserveTree(InventoryItem node, string path, string producer)
            {
                FileTaskContext.Checkpoint();
                projected[path] = (node, producer);
                foreach (var child in node.Children)
                    ReserveTree(child, CombinePath(path, Path.GetFileName(child.Entry.FullPath)), producer);
            }
        }
        display.Preparing = false;
        return batch;

        async Task<InventoryItem> Scan(string path, string? target)
        {
            await context.CheckpointAsync();
            context.Task.CurrentFile = path;
            LocalFileCopy.CopyEntry? metadata = null;
            FileSystemEntry entry;
            if (request.Kind == BackgroundTaskKind.Copy && !VirtualPath.IsRemotePath(path))
            {
                DirectoryAccess.Current.EnsureAccess(path);
                metadata = LocalFileCopy.ReadEntry(path);
                entry = new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path), IsDirectory = metadata.IsDirectory,
                    IsSymbolicLink = metadata.LinkTarget != null, Size = metadata.Length, LastModified = metadata.Modified.ToLocalTime() };
            }
            else entry = await PhysicalEntryAsync(path) ?? throw new FileNotFoundException(path);
            var item = new InventoryItem(entry)
            {
                CopyMetadata = metadata,
                Identity = metadata != null ? (metadata.Identity == null ? null : new(metadata.Identity, "")) : FileIdentitySnapshot.Capture(path, false)
            };
            batch.Sources[path] = item;
            TargetSnapshot? existing = null;
            if (target != null)
            {
                if (!originals.TryGetValue(target, out existing))
                    originals[target] = existing = await SnapshotAsync(target);
            }
            if (entry.IsDirectory && !entry.IsSymbolicLink)
            {
                foreach (var childPath in await GetOperationChildPathsAsync(path, context.CancellationToken))
                {
                    var child = await Scan(childPath, existing?.Entry is { IsDirectory: true, IsSymbolicLink: false }
                        ? CombinePath(target!, Path.GetFileName(childPath)) : null);
                    item.Children.Add(child); item.Count += child.Count; item.Bytes += child.Bytes;
                }
            }
            return item;
        }
    }

    private static FileSystemEntry AtPath(FileSystemEntry source, string path) => new()
    {
        FullPath = path, Name = Path.GetFileName(path), Size = source.Size, IsDirectory = source.IsDirectory,
        IsSymbolicLink = source.IsSymbolicLink, Created = source.Created, LastModified = source.LastModified
    };

    private async Task<FileSystemEntry?> PhysicalEntryAsync(string path)
    {
        if (VirtualPath.IsRemotePath(path))
            return _remoteService is SftpFileService sftp ? await sftp.GetOperationEntryAsync(path) : await GetEntryAsync(path);
        DirectoryAccess.Current.EnsureAccess(path);
        try
        {
            var attrs = File.GetAttributes(path);
            var dir = (attrs & FileAttributes.Directory) != 0;
            FileSystemInfo info = dir ? new DirectoryInfo(path) : new FileInfo(path);
            return new() { FullPath = path, Name = info.Name, IsDirectory = dir, IsSymbolicLink = info.LinkTarget != null,
                Size = !dir && info.LinkTarget == null ? ((FileInfo)info).Length : 0,
                Created = info.CreationTime, LastModified = info.LastWriteTime };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
    private async Task<TargetSnapshot?> SnapshotAsync(string path) => await PhysicalEntryAsync(path) is { } e
        ? new(e, FileIdentitySnapshot.Capture(path, false)) : null;
    private static bool Matches(TargetSnapshot? before, TargetSnapshot? after) =>
        before?.Identity == after?.Identity && before?.Entry.IsDirectory == after?.Entry.IsDirectory &&
        before?.Entry.IsSymbolicLink == after?.Entry.IsSymbolicLink && before?.Entry.LastModified == after?.Entry.LastModified &&
        before?.Entry.Size == after?.Entry.Size;
    private static IOException Changed() => new(LocalizationText.Get("conflict.changed"));
}
