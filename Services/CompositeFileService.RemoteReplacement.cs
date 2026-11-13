using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Renci.SshNet;

namespace MacExplorer.Services;

public sealed partial class CompositeFileService
{
    private async Task<string> ReplaceRemoteTypeAsync(string source, string target, bool move, FileTaskContext context,
        IProgress<FileOperationProgress>? progress, FileSystemEntry expected)
    {
        var client = Client(target, context); var rawTarget = VirtualPath.ParseRemotePath(target).RemotePath;
        var container = CombinePath(GetParentPath(target), ".MacExplorer-replace-" + Guid.NewGuid().ToString("N") + ".fkfinder-tmp");
        var rawContainer = VirtualPath.ParseRemotePath(container).RemotePath;
        var incoming = CombinePath(container, "incoming"); var backup = CombinePath(container, "previous");
        var rawBackup = VirtualPath.ParseRemotePath(backup).RemotePath;
        await Task.Run(() => client.CreateDirectory(rawContainer), context.CancellationToken);
        var backedUp = false; var committed = false;
        try
        {
            await CopyToExactAsync(source, incoming, context, progress);
            if (move) await VerifyCopyAsync(source, incoming, context);
            await context.CheckpointAsync();
            await Task.Run(() =>
            {
                var current = client.GetAttributes(rawTarget);
                if (!expected.IsDirectory && current.Size != expected.Size || current.LastWriteTimeUtc != expected.LastModified.ToUniversalTime() || current.IsDirectory != expected.IsDirectory)
                    throw new IOException(LocalizationText.Get("conflict.changed"));
                client.RenameFile(rawTarget, rawBackup); backedUp = true;
                try { client.RenameFile(VirtualPath.ParseRemotePath(incoming).RemotePath, rawTarget); }
                catch { if (!client.Exists(rawTarget)) { client.RenameFile(rawBackup, rawTarget); backedUp = false; } throw; }
                committed = true;
            }, context.CancellationToken);
            await SyncPlannedMetadataAsync(target, backup, true);
            await SyncPlannedMetadataAsync(source, target, false);
            if (move) await Resolve(source).DeleteAsync(source, false);
            if (move) await SyncPlannedMetadataAsync(source, target, true);
            await RecordAsync(h => h.RecordIrreversibleAsync(target, LocalizationText.Get("远程修改不可撤销。")));
            context.ReportItem(source, target, FileTaskItemState.Completed); return target;
        }
        finally
        {
            if (committed || !backedUp)
                try { await Task.Run(() => DeleteOwnedRemoteTree(client, rawContainer)); }
                catch (Exception ex) { context.Warnings.Add(container + ": " + ex.Message); }
            else context.Warnings.Add(backup);
        }
    }
    private static void DeleteOwnedRemoteTree(SftpClient client, string path)
    {
        var attributes = client.GetAttributes(path);
        if (attributes.IsDirectory && !attributes.IsSymbolicLink)
        {
            foreach (var child in client.ListDirectory(path).Where(c => c.Name is not ("." or "..")))
                DeleteOwnedRemoteTree(client, path.TrimEnd('/') + "/" + child.Name);
            client.DeleteDirectory(path);
        }
        else client.DeleteFile(path);
    }
}
