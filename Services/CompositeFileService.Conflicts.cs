using MacExplorer.Models;
using MacExplorer.Services.Impl;
using System.Text.Json;

namespace MacExplorer.Services;

public sealed partial class CompositeFileService
{
    private sealed class BatchConflictPolicy { internal FileConflictDecision? Decision; }
    private sealed record LocalReplacementPublication(string Backup, string Root, FileIdentitySnapshot Snapshot, FileIdentitySnapshot? SourceIdentity)
    { internal bool SourceRemoved; }
    private static string ReplacementKey(string source, string target, bool move)
        => "local-replacement:" + source + ":" + target + ":" + move;
    private async Task<string> CopyOrMovePlannedAsync(string source, string directory, bool move,
        FileTaskContext context, IProgress<FileOperationProgress>? progress)
    {
        await context.CheckpointAsync();
        var target = CombinePath(directory, Path.GetFileName(source));
        if (source == target && move) return target;
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
        var entry = await GetEntryAsync(source) ?? throw new FileNotFoundException(source);
        if (entry.IsDirectory && !entry.IsSymbolicLink)
        {
            var realSource = BackgroundTaskManager.Normalize(source); var realDirectory = BackgroundTaskManager.Normalize(directory);
            if (realDirectory == realSource || realDirectory.StartsWith(realSource.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new IOException(LocalizationText.Get("conflict.self-copy"));
        }
        var existing = await GetEntryAsync(target);
        if (existing == null) return await CommitNewAsync(source, directory, target, move, context, progress);
        var previousIdentity = VirtualPath.IsRemotePath(target) ? null : await Task.Run(() => FileIdentitySnapshot.Capture(target, false), context.CancellationToken);
        var batch = context.Remember("file-conflict-policy", () => new BatchConflictPolicy());
        var differentTypes = entry.IsDirectory != existing.IsDirectory;
        var inherited = batch.Decision;
        if (differentTypes && inherited?.Action is FileConflictAction.Replace or FileConflictAction.ReplaceOlder) inherited = null;
        var decision = inherited ?? await _conflicts!.ResolveAsync(new(entry, existing, move, VirtualPath.IsRemotePath(target)), context.CancellationToken);
        if (decision.ApplyToRemaining) batch.Decision = decision;
        if (decision.Action == FileConflictAction.Skip || decision.Action == FileConflictAction.ReplaceOlder
            && (differentTypes || !entry.IsDirectory && entry.LastModified <= existing.LastModified))
        {
            context.ReportItem(source, target, FileTaskItemState.Skipped); return target;
        }
        if (decision.Action == FileConflictAction.KeepBoth || source == target)
        {
            var extension = entry.IsDirectory ? "" : Path.GetExtension(target);
            var stem = Path.GetFileName(target)[..^extension.Length];
            if (extension.Length == 0) stem = Path.GetFileName(target);
            var n = 1;
            do { target = CombinePath(directory, $"{stem} ({n++}){extension}"); } while (await ExistsAsync(target));
            return await CommitNewAsync(source, directory, target, move, context, progress);
        }
        var currentTarget = await GetEntryAsync(target);
        if (currentTarget == null || currentTarget.Size != existing.Size || currentTarget.LastModified != existing.LastModified
            || currentTarget.IsDirectory != existing.IsDirectory || !VirtualPath.IsRemotePath(target)
                && previousIdentity != await Task.Run(() => FileIdentitySnapshot.Capture(target, false), context.CancellationToken))
            throw new IOException(LocalizationText.Get("conflict.changed"));
        if (entry.IsDirectory && existing.IsDirectory && !entry.IsSymbolicLink && !existing.IsSymbolicLink)
        {
            context.MarkDone("merge-output:" + target);
            foreach (var child in await GetDirectoryContentsAsync(source, context.CancellationToken))
                await CopyOrMovePlannedAsync(child.FullPath, target, move, context, progress);
            if (move && (await GetDirectoryContentsAsync(source, context.CancellationToken)).Count == 0)
            {
                await Resolve(source).DeleteAsync(source, false);
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
            if (move) { await VerifyCopyAsync(source, output, context); await Resolve(source).DeleteAsync(source, false); }
            await SyncPlannedMetadataAsync(source, output, move);
            await RecordAsync(h => h.RecordIrreversibleAsync(output, LocalizationText.Get("远程修改不可撤销。")));
            context.ReportItem(source, output, FileTaskItemState.Completed); return output;
        }
        return await ReplaceLocalAsync(source, target, move, context, progress);
    }

    private async Task<string> CommitNewAsync(string source, string directory, string target, bool move,
        FileTaskContext context, IProgress<FileOperationProgress>? progress)
    {
        var output = await context.StepAsync("planned-transfer:" + source + ":" + target, async () =>
        {
            if (move && !IsCrossVolume(source, directory) && !VirtualPath.IsRemotePath(source) && Path.GetFileName(source) == Path.GetFileName(target))
            { await _localService.MoveAsync(source, directory); return target; }
            var copied = await CopyToExactAsync(source, target, context, progress);
            if (move)
            {
                if (context.Results.Any(r => r.State == FileTaskItemState.Skipped && (r.Source == source || r.Source.StartsWith(source.TrimEnd('/') + "/", StringComparison.Ordinal))))
                    throw new IOException(LocalizationText.Get("来源包含未复制的项目，已保留源文件。"));
                await VerifyCopyAsync(source, copied, context); await Resolve(source).DeleteAsync(source, false);
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
        var copied = await _localService.CopyWithProgressAsync(source, directory, progress, context.CancellationToken);
        if (copied != target)
        {
            if (await ExistsAsync(target)) throw new IOException(LocalizationText.Get("conflict.changed"));
            await _localService.RenameAsync(copied, Path.GetFileName(target));
        }
        return target;
    }
    private async Task<string> ReplaceLocalAsync(string source, string target, bool move, FileTaskContext context, IProgress<FileOperationProgress>? progress)
    {
        var original = await Task.Run(() => FileIdentitySnapshot.Capture(target, true, context.CancellationToken), context.CancellationToken)
            ?? throw new IOException(LocalizationText.Get("conflict.changed"));
        var sourceIdentity = move ? FileIdentitySnapshot.Capture(source, false, context.CancellationToken) : null;
        var root = Path.Combine(Path.GetDirectoryName(target)!, ".MacExplorer-replace-" + Guid.NewGuid().ToString("N") + ".fkfinder-tmp");
        var incoming = Path.Combine(root, "incoming");
        var backup = Path.Combine(root, "previous", Path.GetFileName(target));
        var committed = false; var backedUp = false;
        try
        {
            Directory.CreateDirectory(incoming);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await File.WriteAllTextAsync(Path.Combine(root, "recovery.json"), JsonSerializer.Serialize(new { Source = source, Destination = target, Backup = backup, Move = move }), context.CancellationToken);
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
                await context.CheckpointAsync();
                await Resolve(source).DeleteAsync(source, false);
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
