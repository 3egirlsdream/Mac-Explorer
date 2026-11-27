using MacExplorer.Serialization;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using System.Text.Json;

namespace MacExplorer.Services;

public sealed partial class CompositeFileService
{
    private sealed record LocalReplacementPublication(string Backup, string Root, FileIdentitySnapshot Snapshot, FileIdentitySnapshot? SourceIdentity) : IRetainedState
    {
        internal bool SourceRemoved;

        void IRetainedState.MeasureFields(RetainedStateSize size, int depth)
        {
            size.AddScalars(1, depth); // SourceRemoved
            size.Visit(Backup, depth);
            size.Visit(Root, depth);
            size.Visit(Snapshot, depth);
            size.Visit(SourceIdentity, depth);
        }
    }
    private static string ReplacementKey(string source, string target, bool move)
        => "local-replacement:" + source + ":" + target + ":" + move;
    private async Task<string> CopyOrMovePlannedAsync(string source, string directory, bool move,
        FileTaskContext context, IProgress<FileOperationProgress>? progress)
    {
        await context.CheckpointAsync();
        var batch = context.Batch!;
        var plan = batch.Choices[(source, CombinePath(directory, Path.GetFileName(source)))];
        context.Task.CurrentFile = source;
        var target = plan.Target;
        if (source == target && move) return target;
        return await context.StepAsync("approved:" + source + "\0" + target, async () =>
        {
            var output = await ExecuteTransferAsync(source, directory, move, context, progress, plan);
            batch.Finish(source, subtree: !context.IsDone("merge-output:" + target));
            if (batch.WatchedOutputs.TryGetValue(source, out var watched))
                foreach (var path in watched) batch.Published[path] = await SnapshotAsync(path);
            return output;
        });
    }

    private async Task<string> ExecuteTransferAsync(string source, string directory, bool move,
        FileTaskContext context, IProgress<FileOperationProgress>? progress, TransferChoice plan)
    {
        var batch = context.Batch!;
        var target = plan.Target;
        if (!VirtualPath.IsRemotePath(target) && context.Recall<LocalReplacementPublication>(ReplacementKey(source, target, move)) is { } publication)
        {
            if (publication.Snapshot != await Task.Run(() => FileIdentitySnapshot.Capture(target, true, context.CancellationToken), context.CancellationToken))
                throw new IOException(LocalizationText.Get("conflict.changed"));
            if (move && !publication.SourceRemoved)
            {
                if (publication.SourceIdentity is { } identity && identity != FileIdentitySnapshot.Capture(source, false, context.CancellationToken))
                    throw new IOException(LocalizationText.Get("源项目已变化，请重新创建任务。"));
                await VerifyCopyAsync(source, target, context);
            }
            return await CompleteLocalReplacementAsync(source, target, move, publication, context);
        }
        var item = batch.Sources[source];
        var entry = item.Entry;
        if (await PhysicalEntryAsync(source) is not { } currentSource)
        {
            if (entry.IsDirectory) throw new DirectoryNotFoundException(source);
            throw new FileNotFoundException(source);
        }
        if (item.Identity != FileIdentitySnapshot.Capture(source, false) || currentSource.IsDirectory != entry.IsDirectory || currentSource.IsSymbolicLink != entry.IsSymbolicLink ||
            !entry.IsDirectory && (currentSource.LastModified != entry.LastModified || currentSource.Size != entry.Size))
            throw Changed();
        if (entry.IsDirectory && !entry.IsSymbolicLink)
        {
            var realSource = BackgroundTaskManager.Normalize(source); var realDirectory = BackgroundTaskManager.Normalize(directory);
            if (realDirectory == realSource || realDirectory.StartsWith(realSource.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new IOException(LocalizationText.Get("conflict.self-copy"));
        }
        if (plan.Action == FileConflictAction.Skip)
        {
            context.ReportItem(source, target, FileTaskItemState.Skipped); return target;
        }
        var expected = plan.FromBatch ? batch.Published[target] ?? throw Changed() : plan.Expected;
        var current = await SnapshotAsync(target);
        var merging = context.IsDone("merge-output:" + target);
        if (!Matches(expected, current) && !(merging && current?.Entry.IsDirectory == true &&
            current.Identity == expected?.Identity) && !Platforms.MacCatalyst.Services.LocalFileCopy.OwnsDirectory(context, source, target))
            throw Changed();
        if (expected == null) return await CommitNewAsync(source, directory, target, move, context, progress);
        var existing = expected.Entry;
        if (entry.IsDirectory && existing.IsDirectory && !entry.IsSymbolicLink && !existing.IsSymbolicLink)
        {
            context.MarkDone("merge-output:" + target);
            foreach (var child in item.Children)
                await CopyOrMovePlannedAsync(child.Entry.FullPath, target, move, context, progress);
            if (move && !HasSkippedItems(source, context))
            {
                await context.CheckpointAsync();
                // Let the filesystem reject a directory that gained an item during the merge.
                if (VirtualPath.IsRemotePath(source))
                    await Client(source, context).DeleteDirectoryAsync(VirtualPath.ParseRemotePath(source).RemotePath, context.CancellationToken);
                else
                {
                    DirectoryAccess.Current.EnsureAccess(source);
                    await Task.Run(() => Directory.Delete(source, recursive: false), context.CancellationToken);
                }
                await RecordAsync(h => h.RecordRemovedDirectoryAsync(source));
            }
            context.ReportItem(source, target, FileTaskItemState.Completed); return target;
        }
        if (VirtualPath.IsRemotePath(target))
        {
            var client = Client(target, context); var raw = VirtualPath.ParseRemotePath(target).RemotePath;
            var attributes = await Task.Run(() => client.GetAttributes(raw), context.CancellationToken);
            if (!existing.IsDirectory && attributes.Size != existing.Size || attributes.LastWriteTimeUtc != existing.LastModified.ToUniversalTime() || attributes.IsDirectory != existing.IsDirectory)
                throw new IOException(LocalizationText.Get("conflict.changed"));
            if (existing.IsDirectory || entry.IsDirectory)
                return await ReplaceRemoteTypeAsync(source, target, move, context, progress, existing);
            var output = await SftpTransfer.CopyAsync(source, directory, context, p => Client(p, context), progress, target, true,
                () =>
                {
                    var now = client.GetAttributes(raw); if (now.Size != attributes.Size || now.LastWriteTimeUtc != attributes.LastWriteTimeUtc)
                        throw new IOException(LocalizationText.Get("conflict.changed"));
                });
            if (move) { await VerifyCopyAsync(source, output, context); await RemoveMoveSourceAsync(source, context); }
            await SyncPlannedMetadataAsync(source, output, move);
            await RecordAsync(h => h.RecordIrreversibleAsync(output, LocalizationText.Get("远程修改不可撤销。")));
            context.ReportItem(source, output, FileTaskItemState.Completed); return output;
        }
        return await ReplaceLocalAsync(source, target, move, context, progress, expected);
    }

    private async Task<string> CommitNewAsync(string source, string directory, string target, bool move,
        FileTaskContext context, IProgress<FileOperationProgress>? progress)
    {
        var output = await context.StepAsync("planned-transfer:" + source + ":" + target, async () =>
        {
            if (move && !VirtualPath.IsRemotePath(source) && !IsCrossVolume(source, directory))
            {
                if (_localService is Platforms.MacCatalyst.Services.MacFileService)
                {
                    try
                    {
                        DirectoryAccess.Current.EnsureAccess(target);
                        if (!NewFileWriter.TryPublish(source, target, context.Batch!.Sources[source].Entry.IsDirectory))
                            throw Changed();
                        return target;
                    }
                    catch (IOException ex) when (OperatingSystem.IsMacOS() &&
                        ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 45 or 78 or 102 })
                    { /* This volume needs the verified copy-and-remove path below. */ }
                }
                else if (Path.GetFileName(source) == Path.GetFileName(target))
                {
                    await _localService.MoveAsync(source, directory);
                    return target;
                }
            }
            var copied = await CopyToExactAsync(source, target, context, progress);
            if (move)
            {
                if (HasSkippedItems(source, context))
                    throw new IOException(LocalizationText.Get("来源包含未复制的项目，已保留源文件。"));
                await VerifyCopyAsync(source, copied, context); await RemoveMoveSourceAsync(source, context);
            }
            return copied;
        });
        await SyncPlannedMetadataAsync(source, output, move);
        await RecordAsync(h => move ? h.RecordMoveAsync(source, output) : h.RecordCreatedAsync(output, true));
        context.ReportItem(source, output, FileTaskItemState.Completed); return output;
    }
    private async Task<string> CopyToExactAsync(string source, string target, FileTaskContext context, IProgress<FileOperationProgress>? progress)
    {
        var directory = GetParentPath(target);
        if (VirtualPath.IsRemotePath(source) || VirtualPath.IsRemotePath(target))
            return await SftpTransfer.CopyAsync(source, directory, context, p => Client(p, context), progress, target);
        context.ExactCopyTarget = target;
        string copied;
        try { copied = await _localService.CopyWithProgressAsync(source, directory, progress, context.CancellationToken); }
        finally { context.ExactCopyTarget = null; }
        if (copied != target)
        {
            if (await ExistsAsync(target)) throw new IOException(LocalizationText.Get("conflict.changed"));
            await _localService.RenameAsync(copied, Path.GetFileName(target));
        }
        return target;
    }
    private async Task<string> ReplaceLocalAsync(string source, string target, bool move, FileTaskContext context, IProgress<FileOperationProgress>? progress, TargetSnapshot expected)
    {
        var original = await Task.Run(() => FileIdentitySnapshot.Capture(target, true, context.CancellationToken), context.CancellationToken)
            ?? throw new IOException(LocalizationText.Get("conflict.changed"));
        if (!Matches(expected, await SnapshotAsync(target))) throw Changed();
        var sourceIdentity = move ? FileIdentitySnapshot.Capture(source, false, context.CancellationToken) : null;
        var root = Path.Combine(Path.GetDirectoryName(target)!, ".MacExplorer-replace-" + Guid.NewGuid().ToString("N") + ".fkfinder-tmp");
        var incoming = Path.Combine(root, "incoming");
        var backup = Path.Combine(root, "previous", Path.GetFileName(target));
        var committed = false; var backedUp = false;
        try
        {
            Directory.CreateDirectory(incoming);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await File.WriteAllTextAsync(Path.Combine(root, "recovery.json"), JsonSerializer.Serialize(new FileRecoveryRecord(source, target, backup, move), AppJsonContext.Default.FileRecoveryRecord), context.CancellationToken);
            var staged = await CopyToExactAsync(source, Path.Combine(incoming, Path.GetFileName(target)), context, progress);
            if (move) await VerifyCopyAsync(source, staged, context);
            var published = await Task.Run(() => FileIdentitySnapshot.Capture(staged, true, context.CancellationToken), context.CancellationToken)
                ?? throw new IOException(LocalizationText.Get("conflict.changed"));
            FileOperationHistoryService.PrepareReplacementBaseline(context, target, published, original);
            await context.CheckpointAsync();
            if (await Task.Run(() => FileIdentitySnapshot.Capture(target, true, context.CancellationToken), context.CancellationToken) != original)
                throw new IOException(LocalizationText.Get("conflict.changed"));
            MoveOwnedPath(target, backup); backedUp = true;
            try { MoveOwnedPath(staged, target); }
            catch { if (!File.Exists(target) && !Directory.Exists(target)) { MoveOwnedPath(backup, target); backedUp = false; } throw; }
            committed = true;
            var publication = new LocalReplacementPublication(backup, root, published, sourceIdentity);
            context.Remember(ReplacementKey(source, target, move), () => publication);
            await SyncPlannedMetadataAsync(target, backup, true);
            await SyncPlannedMetadataAsync(source, target, false);
            return await CompleteLocalReplacementAsync(source, target, move, publication, context);
        }
        finally
        {
            if (!committed && !backedUp && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private async Task<string> CompleteLocalReplacementAsync(string source, string target, bool move,
        LocalReplacementPublication publication, FileTaskContext context)
    {
        await RecordAsync(h => h.RecordReplacementAsync("", target, publication.Backup, publication.Root));
        if (move)
        {
            if (!publication.SourceRemoved)
            {
                await RemoveMoveSourceAsync(source, context);
                publication.SourceRemoved = true;
            }
            await SyncPlannedMetadataAsync(source, target, true);
            await RecordAsync(h => h.RecordReplacementAsync(source, target, publication.Backup, publication.Root));
        }
        context.ReportItem(source, target, FileTaskItemState.Completed); return target;
    }
    internal static void MoveOwnedPath(string source, string target)
    { if (Directory.Exists(source) && new FileInfo(source).LinkTarget == null) Directory.Move(source, target); else File.Move(source, target); }
    private async Task SyncPlannedMetadataAsync(string source, string target, bool move)
    {
        if (_ratings != null) await SyncMetadataAsync(() => _ratings.TransferPathAsync(source, target, move));
        if (_tags != null) await SyncMetadataAsync(() => move ? _tags.UpdatePathAsync(source, target) : _tags.CopyPathAsync(source, target));
        if (move && _analysis != null) await SyncMetadataAsync(() => _analysis.UpdateFilePathAsync(source, target));
    }
}
