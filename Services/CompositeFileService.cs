using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Renci.SshNet;

namespace MacExplorer.Services;

public sealed class CompositeFileService : IFileService, ITrashResultFileService
{
    private readonly IFileService _localService;
    private readonly IRemoteFileService _remoteService;
    private readonly IBackgroundTaskManager? _taskManager;
    private readonly IRemoteConnectionService? _connections;
    private readonly Func<IFileOperationHistoryService?>? _history;
    private readonly IRatingService? _ratings;
    public CompositeFileService(IFileService localService, IRemoteFileService remoteService,
        IBackgroundTaskManager? taskManager = null, IRemoteConnectionService? connections = null,
        Func<IFileOperationHistoryService?>? history = null, IRatingService? ratings = null)
    { _localService = localService; _remoteService = remoteService; _taskManager = taskManager; _connections = connections; _history = history; _ratings = ratings; }
    private IFileService Resolve(string? path) => VirtualPath.IsRemotePath(path) ? _remoteService : _localService;
    public string HomeDirectory => _localService.HomeDirectory;
    public string RootDirectory => _localService.RootDirectory;
    public string TrashDirectory => _localService.TrashDirectory;
    public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
        => Resolve(path).GetDirectoryContentsAsync(path, cancellationToken);
    public IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(string path, int batchSize = 256, CancellationToken cancellationToken = default)
        => Resolve(path).EnumerateDirectoryBatchesAsync(path, batchSize, cancellationToken);
    public Task<FileSystemEntry?> GetEntryAsync(string path) => Resolve(path).GetEntryAsync(path);
    public Task<bool> ExistsAsync(string path) => Resolve(path).ExistsAsync(path);
    public string GetParentPath(string path) => Resolve(path).GetParentPath(path);
    public string CombinePath(string directory, string name) => Resolve(directory).CombinePath(directory, name);
    public IReadOnlyList<string> GetVolumes() => _localService.GetVolumes();
    public bool IsCrossVolume(string sourcePath, string destinationPath)
        => VirtualPath.IsRemotePath(sourcePath) != VirtualPath.IsRemotePath(destinationPath)
            || !VirtualPath.IsRemotePath(sourcePath) && _localService.IsCrossVolume(sourcePath, destinationPath);
    public Task ResolveAppIconsAsync(IEnumerable<FileSystemEntry> entries, Action? onBatchResolved = null, CancellationToken cancellationToken = default)
        => _localService.ResolveAppIconsAsync(entries, onBatchResolved, cancellationToken);

    private Task<T> Run<T>(BackgroundTaskKind kind, string label, IReadOnlyList<string> sources, string? destination,
        Func<FileTaskContext, Task<T>> action, CancellationToken token = default)
        => FileTaskContext.RunAsync(_taskManager, new(kind, LocalizationText.Get(label), sources, destination), action, token);
    private async Task RecordAsync(Func<IFileOperationHistoryService, Task> record)
    {
        if (FileOperationHistoryService.IsUndoing || FileTaskContext.Current?.Task.Kind == BackgroundTaskKind.BatchRename || _history?.Invoke() is not { } history) return;
        try { await record(history); }
        catch (Exception ex) { FileTaskContext.Current?.Warnings.Add(LocalizationText.Get("历史记录更新失败：{0}", ex.Message)); }
    }
    private static async Task SyncMetadataAsync(Func<Task> action)
    { try { await action(); } catch (Exception ex) { FileTaskContext.Current?.Warnings.Add(ex.Message); } }
    public Task<string> CreateFolderAsync(string parentPath, string name)
        => Run(BackgroundTaskKind.Create, "新建文件夹", [], parentPath, async context =>
        {
            var path = await context.StepAsync("mkdir:" + parentPath + "/" + name, () => Resolve(parentPath).CreateFolderAsync(parentPath, name));
            await RecordAsync(h => h.RecordCreatedAsync(path));
            context.ReportItem(path, path, FileTaskItemState.Completed); return path;
        });
    public Task<string> CreateFileAsync(string parentPath, string name) => CreateFileWithContentAsync(parentPath, name, []);
    public Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content)
        => Run(BackgroundTaskKind.Create, "新建文件", [], parentPath, async context =>
        {
            var path = await context.StepAsync("create:" + parentPath + "/" + name,
                () => Resolve(parentPath).CreateFileWithContentAsync(parentPath, name, content));
            await RecordAsync(h => h.RecordCreatedAsync(path));
            context.ReportItem(path, path, FileTaskItemState.Completed); return path;
        });
    public async Task DeleteAsync(string path, bool moveToTrash = true)
    {
        if (moveToTrash) { await TrashWithResultAsync(path); return; }
        await Run(BackgroundTaskKind.Delete, "永久删除", [path], null, async context =>
        {
            await context.StepAsync("delete:" + path, async () => { await Resolve(path).DeleteAsync(path, false); return true; });
            await RecordAsync(h => h.RecordIrreversibleAsync(path, LocalizationText.Get("永久删除不可撤销。")));
            context.ReportItem(path, null, FileTaskItemState.Completed); return true;
        });
    }
    public Task<string?> TrashWithResultAsync(string path)
        => Run(BackgroundTaskKind.Delete, "移到废纸篓", [path], null, async context =>
        {
            var output = await context.StepAsync("trash:" + path, () => FileTrash.MoveAsync(Resolve(path), path));
            if (_ratings != null && output != null)
                await SyncMetadataAsync(() => _ratings.TransferPathAsync(path, output, true));
            await RecordAsync(h => h.RecordTrashAsync(path, output ?? ""));
            context.ReportItem(path, output, FileTaskItemState.Completed); return output;
        });
    public Task DeletePermanentlyAsync(string path) => DeleteAsync(path, false);
    public Task EmptyTrashAsync() => Run(BackgroundTaskKind.Delete, "清空废纸篓", [TrashDirectory], null,
        async context => { await _localService.EmptyTrashAsync(); return true; });
    public Task RenameAsync(string path, string newName)
        => Run(BackgroundTaskKind.Rename, "重命名", [path], CombinePath(GetParentPath(path), newName), async context =>
        {
            var output = CombinePath(GetParentPath(path), newName);
            await context.StepAsync("rename:" + path + ":" + output, async () => { await Resolve(path).RenameAsync(path, newName); return true; });
            if (_ratings != null && FileTaskContext.Current?.Task.Kind != BackgroundTaskKind.BatchRename)
                await SyncMetadataAsync(() => _ratings.TransferPathAsync(path, output, true));
            await RecordAsync(h => h.RecordRenameAsync(path, output));
            context.ReportItem(path, output, FileTaskItemState.Completed); return true;
        });
    public Task CopyAsync(string sourcePath, string destinationDirectory)
        => CopyWithProgressAsync(sourcePath, destinationDirectory);
    public Task<string> CopyWithProgressAsync(string sourcePath, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default)
        => Run(BackgroundTaskKind.Copy, "复制文件", [sourcePath], destinationDirectory, async context =>
        {
            var output = await context.StepAsync("copy:" + sourcePath + ":" + destinationDirectory, async () =>
                !VirtualPath.IsRemotePath(sourcePath) && !VirtualPath.IsRemotePath(destinationDirectory)
                    ? await _localService.CopyWithProgressAsync(sourcePath, destinationDirectory, progress, context.CancellationToken)
                    : await TransferAsync(sourcePath, destinationDirectory, context, progress));
            if (_ratings != null) await SyncMetadataAsync(() => _ratings.TransferPathAsync(sourcePath, output, false));
            await RecordAsync(h => h.RecordCreatedAsync(output, true));
            context.ReportItem(sourcePath, output, FileTaskItemState.Completed); return output;
        }, ct);
    public Task MoveAsync(string sourcePath, string destinationDirectory, bool overwrite = false)
        => Run(BackgroundTaskKind.Move, "移动文件", [sourcePath], destinationDirectory, async context =>
        {
            var output = CombinePath(destinationDirectory, Path.GetFileName(sourcePath));
            var existed = context.Remember("overwrite:" + output, () => ExistsAsync(output).GetAwaiter().GetResult());
            await context.StepAsync("move:" + sourcePath + ":" + output, async () =>
            {
                if (IsCrossVolume(sourcePath, destinationDirectory) || VirtualPath.IsRemotePath(sourcePath) && VirtualPath.IsRemotePath(destinationDirectory)
                    && VirtualPath.ParseRemotePath(sourcePath).ServerId != VirtualPath.ParseRemotePath(destinationDirectory).ServerId)
                {
                    if (existed) throw new IOException(LocalizationText.Get("跨卷移动的目标已存在，请选择其他位置。"));
                    var copied = await context.StepAsync("move-copy:" + sourcePath, async () =>
                    {
                        if (!VirtualPath.IsRemotePath(sourcePath) && !VirtualPath.IsRemotePath(destinationDirectory))
                            return await _localService.CopyWithProgressAsync(sourcePath, destinationDirectory, null, context.CancellationToken);
                        return await TransferAsync(sourcePath, destinationDirectory, context, null);
                    });
                    await RecordAsync(h => h.RecordCreatedAsync(copied, true));
                    if (context.Results.Any(i => i.State == FileTaskItemState.Skipped
                        && (i.Source == sourcePath || i.Source.StartsWith(sourcePath.TrimEnd('/') + "/", StringComparison.Ordinal))))
                        throw new IOException(LocalizationText.Get("来源包含未复制的项目，已保留源文件。"));
                    if (copied != output) throw new IOException(LocalizationText.Get("目标位置发生冲突，请检查已复制的文件。"));
                    await VerifyCopyAsync(sourcePath, copied, context);
                    await Resolve(sourcePath).DeleteAsync(sourcePath, false);
                }
                else await Resolve(sourcePath).MoveAsync(sourcePath, destinationDirectory, overwrite);
                return true;
            });
            if (_ratings != null) await SyncMetadataAsync(() => _ratings.TransferPathAsync(sourcePath, output, true));
            await RecordAsync(h => existed ? h.RecordIrreversibleAsync(output, LocalizationText.Get("覆盖操作不可撤销。"))
                : h.RecordMoveAsync(sourcePath, output));
            context.ReportItem(sourcePath, output, FileTaskItemState.Completed); return true;
        });
    public async Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default)
    {
        await Run(BackgroundTaskKind.Move, "移动文件", sourcePaths, destinationDirectory, async context =>
        {
            foreach (var path in sourcePaths) { await context.CheckpointAsync(); await MoveAsync(path, destinationDirectory); }
            progress?.Report(new() { Percentage = 100 }); return true;
        }, ct);
    }

    private SftpClient Client(string path, FileTaskContext context)
    {
        var id = VirtualPath.ParseRemotePath(path).ServerId;
        return context.Resource("sftp:" + id, () => _connections != null
            ? _connections.LeaseClientAsync(id, context.CancellationToken).GetAwaiter().GetResult()
            : throw new InvalidOperationException("Remote connection service unavailable"));
    }
    private Stream Read(string path, FileTaskContext context) => VirtualPath.IsRemotePath(path)
        ? Client(path, context).OpenRead(VirtualPath.ParseRemotePath(path).RemotePath) : File.OpenRead(path);
    private async Task VerifyCopyAsync(string source, string destination, FileTaskContext context)
    {
        var sourceEntry = await GetEntryAsync(source) ?? throw new FileNotFoundException(source);
        if (sourceEntry.IsDirectory && !sourceEntry.IsSymbolicLink)
        {
            foreach (var item in await GetDirectoryContentsAsync(source, context.CancellationToken))
                await VerifyCopyAsync(item.FullPath, CombinePath(destination, item.Name), context);
        }
        else if (sourceEntry.IsSymbolicLink)
        {
            if (VirtualPath.IsRemotePath(source) || VirtualPath.IsRemotePath(destination)
                || new FileInfo(source).LinkTarget != new FileInfo(destination).LinkTarget)
                throw new IOException(LocalizationText.Get("复制校验失败，已保留源文件。"));
        }
        else
        {
            using var left = Read(source, context); using var right = Read(destination, context);
            var targetEntry = await GetEntryAsync(destination);
            if (targetEntry?.Size != sourceEntry.Size) throw new IOException(LocalizationText.Get("复制校验失败，已保留源文件。"));
            var a = new byte[128 * 1024]; var b = new byte[a.Length];
            while (true)
            {
                await context.CheckpointAsync();
                var count = left.Read(a); if (count == 0) break;
                right.ReadExactly(b.AsSpan(0, count));
                if (!a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count)))
                    throw new IOException(LocalizationText.Get("复制校验失败，已保留源文件。"));
            }
        }
    }
    private async Task<string> TransferAsync(string source, string directory, FileTaskContext context, IProgress<FileOperationProgress>? progress)
    {
        if (!VirtualPath.IsRemotePath(source)) DirectoryAccess.Current.EnsureAccess(source);
        if (!VirtualPath.IsRemotePath(directory)) DirectoryAccess.Current.EnsureAccess(directory);
        return await SftpTransfer.CopyAsync(source, directory, context, path => Client(path, context), progress);
    }
}
