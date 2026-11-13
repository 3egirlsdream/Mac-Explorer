using MacExplorer.Services.Subscriptions;
using MacExplorer.Models;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed class FileOperationHistoryService : IFileOperationHistoryService
{
    internal const int HistoryLimit = 100;
    private readonly IFileService _files;
    private readonly IDirectoryChangeNotifier? _changes;
    private readonly IFileTagService? _tags;
    private readonly IBatchRenameService? _rename;
    private readonly IAiTagService? _analysis;
    private readonly IPinnedFolderService? _pins;
    private readonly ILogger<FileOperationHistoryService>? _logger;
    private readonly IBackgroundTaskManager? _tasks;
    private readonly IRatingService? _ratings;
    private readonly Indexing.IFileIndexWriter? _index;
    private readonly List<FileOperationRecord> _records = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _undoGate = new(1, 1);
    private static readonly AsyncLocal<bool> Undoing = new();
    internal static bool IsUndoing => Undoing.Value;
    private sealed class ReplacementBaseline
    { internal FileIdentitySnapshot? Snapshot, BackupSnapshot; }
    private static ReplacementBaseline GetReplacementBaseline(FileTaskContext context, string target)
        => context.Remember("replacement-baseline:" + target, () => new ReplacementBaseline());
    internal static void PrepareReplacementBaseline(FileTaskContext context, string target, FileIdentitySnapshot snapshot, FileIdentitySnapshot backupSnapshot)
    {
        var baseline = GetReplacementBaseline(context, target);
        baseline.Snapshot = snapshot; baseline.BackupSnapshot = backupSnapshot;
    }
    public event Action? HistoryChanged;
    public IReadOnlyList<FileOperationRecord> Records { get { lock (_lock) return _records.ToArray(); } }
    public bool CanUndo => Records.Any(r => r.HasUndoableItems);

    public FileOperationHistoryService(IFileService fileService, IDirectoryChangeNotifier? directoryChangeNotifier = null,
        IFileTagService? fileTagService = null, ILogger<FileOperationHistoryService>? logger = null,
        IBatchRenameService? batchRename = null, IBackgroundTaskManager? tasks = null,
        IAiTagService? analysis = null, IPinnedFolderService? pins = null, IRatingService? ratings = null, Indexing.IFileIndexWriter? index = null)
    { _files = fileService; _changes = directoryChangeNotifier; _tags = fileTagService; _logger = logger;
        _rename = batchRename; _tasks = tasks; _analysis = analysis; _pins = pins; _ratings = ratings; _index = index; }

    public Task RecordRenameAsync(string oldPath, string newPath) => AddAsync(FileOperationKind.Rename, oldPath, newPath);
    public Task RecordMoveAsync(string oldPath, string newPath) => AddAsync(FileOperationKind.Move, oldPath, newPath);
    public Task RecordTrashAsync(string originalPath, string trashedPath)
        => AddAsync(FileOperationKind.MoveToTrash, originalPath, trashedPath,
            string.IsNullOrEmpty(trashedPath) ? LocalizationText.Get("无法准确定位废纸篓中的项目。") : null);
    public Task RecordCreatedAsync(string path, bool isCopy = false)
        => AddAsync(isCopy ? FileOperationKind.Copy : FileOperationKind.Create, "", path);
    public Task RecordIrreversibleAsync(string path, string reason) => AddAsync(FileOperationKind.Irreversible, path, path, reason);
    public Task RecordRemovedDirectoryAsync(string path) => AddAsync(FileOperationKind.RemoveEmptyDirectory, path, path);
    public async Task RecordReplacementAsync(string source, string target, string backup, string ownerDirectory)
    {
        var taskId = FileTaskContext.Current?.Task.Id;
        var existing = Records.SelectMany(r => r.Children.Prepend(r)).FirstOrDefault(r => r.TaskId == taskId && r.Kind == FileOperationKind.Replace && r.CurrentPath == target);
        if (existing != null) { if (source.Length > 0) existing.OriginalPath = source; return; }
        var context = FileTaskContext.Current;
        var baseline = context == null ? null : GetReplacementBaseline(context, target);
        var token = context?.CancellationToken ?? CancellationToken.None;
        var record = new FileOperationRecord { Kind = FileOperationKind.Replace, OriginalPath = source, CurrentPath = target,
            BackupPath = backup, BackupDirectory = ownerDirectory,
            Snapshot = baseline?.Snapshot,
            BackupSnapshot = baseline?.BackupSnapshot ?? await Task.Run(() => FileIdentitySnapshot.Capture(backup, true, token), token) };
        await AddAsync(FileOperationKind.Replace, source, target, replacement: record);
    }

    private async Task AddAsync(FileOperationKind kind, string original, string current, string? reason = null, FileOperationRecord? replacement = null)
    {
        if (IsUndoing) return;
        var taskId = FileTaskContext.Current?.Task.Id;
        if (kind is FileOperationKind.Copy or FileOperationKind.Create or FileOperationKind.Move)
        {
            if (FileTaskContext.Current?.IsDone("merge-output:" + current) == true) return;
            if (Records.SelectMany(r => r.Children.Prepend(r)).Any(r => r.TaskId == taskId && r.Kind == FileOperationKind.Replace && r.CurrentPath == current)) return;
        }
        var record = replacement ?? new FileOperationRecord { Kind = kind, OriginalPath = original, CurrentPath = current, UnavailableReason = reason };
        record.TaskId = taskId;
        if (VirtualPath.IsRemotePath(original) || VirtualPath.IsRemotePath(current)) record.UnavailableReason = LocalizationText.Get("远程修改不可撤销。");
        if (record.UnavailableReason == null)
        {
            try
            {
                var token = FileTaskContext.Current?.CancellationToken ?? CancellationToken.None;
                record.Snapshot ??= await Task.Run(() => FileIdentitySnapshot.Capture(current, kind is FileOperationKind.Copy or FileOperationKind.Create or FileOperationKind.Replace, token), token);
                if (record.Snapshot == null && kind is FileOperationKind.Copy or FileOperationKind.Create)
                    record.UnavailableReason = LocalizationText.Get("项目已不存在。");
            }
            catch (Exception ex) { record.UnavailableReason = ex.Message; }
        }
        lock (_lock)
        {
            var parent = record.TaskId == null ? null : _records.LastOrDefault(r => r.TaskId == record.TaskId);
            if (parent == null) { parent = record; _records.Add(record); }
            else if (parent.Kind == FileOperationKind.Copy && record.Kind == FileOperationKind.Move && parent.CurrentPath == current)
            {
                parent.Kind = record.Kind; parent.OriginalPath = original; parent.Snapshot = record.Snapshot;
            }
            else if (parent.Kind != record.Kind || parent.OriginalPath != original || parent.CurrentPath != current)
            {
                if (!parent.Children.Any(r => r.Kind == kind && r.OriginalPath == original && r.CurrentPath == current))
                    parent.Children.Add(record);
                // A mixed task may still contain reversible items.
            }
            if (FileTaskContext.Current is { } context) context.Task.HistoryId = parent.Id;
        }
        TrimHistory();
        Changed();
    }

    public Task<Guid?> RecordBatchRenameAsync(IReadOnlyList<BatchRenamePreviewItem> items, Guid? batchId = null)
    {
        if (items.Count == 0 || IsUndoing) return Task.FromResult(batchId);
        FileOperationRecord record;
        lock (_lock)
        {
            record = _records.LastOrDefault(r => r.Id == batchId && r.Kind == FileOperationKind.BatchRename)
                ?? new FileOperationRecord { Kind = FileOperationKind.BatchRename, TaskId = FileTaskContext.Current?.Task.Id };
            foreach (var item in items)
            {
                record.RenameSnapshots.TryAdd(item.NewPath, FileIdentitySnapshot.Capture(item.NewPath, false));
                if (!record.RenameItems.Any(i => i.OriginalPath == item.OriginalPath && i.NewPath == item.NewPath)) record.RenameItems.Add(item);
            }
            if (!_records.Contains(record)) _records.Add(record);
            if (FileTaskContext.Current is { } context) context.Task.HistoryId = record.Id;
        }
        TrimHistory();
        Changed(); return Task.FromResult<Guid?>(record.Id);
    }
    public async Task<bool> UndoBatchAsync(Guid batchId) => (await UndoAsync(batchId)).Success;
    public async Task<bool> UndoLastAsync()
    {
        var record = Records.LastOrDefault(r => r.HasUndoableItems);
        return record != null && (await UndoAsync(record.Id)).Success;
    }
    public void ClearHistory()
        => _ = ClearHistoryAsync();

    public async Task ClearHistoryAsync()
    {
        var active = _tasks?.Tasks.Where(t => t.IsActive).Select(t => t.Id).ToHashSet() ?? [];
        FileOperationRecord[] records;
        lock (_lock)
        {
            records = _records.Where(r => r.TaskId == null || !active.Contains(r.TaskId)).ToArray();
            foreach (var record in records) _records.Remove(record);
        }
        Changed(); await CleanupBackups(records);
    }
    private static Task CleanupBackups(IEnumerable<FileOperationRecord> records)
    {
        var backups = records.SelectMany(r => r.Children.Prepend(r)).Where(r => r.BackupDirectory != null).ToArray();
        if (backups.Length == 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            foreach (var record in backups)
                try
                {
                    if (Directory.Exists(record.BackupDirectory) && record.BackupPath != null
                        && record.BackupSnapshot == FileIdentitySnapshot.Capture(record.BackupPath, true)
                        && Path.GetFileName(record.BackupDirectory).StartsWith(".MacExplorer-replace-", StringComparison.Ordinal))
                        Directory.Delete(record.BackupDirectory, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex); }
        });
    }

    private void TrimHistory()
    {
        FileOperationRecord[] candidates;
        lock (_lock)
        {
            if (_records.Count <= HistoryLimit) return;
            candidates = _records.ToArray();
        }
        // Snapshot the task manager outside the history lock; never split an active undo group.
        var active = _tasks?.Tasks.Where(task => task.IsActive).Select(task => task.Id).ToHashSet() ?? [];
        if (FileTaskContext.Current is { } context) active.Add(context.Task.Id);
        lock (_lock)
        {
            foreach (var record in candidates)
            {
                if (_records.Count <= HistoryLimit) break;
                if (record.TaskId == null || !active.Contains(record.TaskId)) { _records.Remove(record); _ = CleanupBackups([record]); }
            }
        }
    }

    private static IEnumerable<string> Paths(FileOperationRecord record) =>
        new[] { record.OriginalPath, record.CurrentPath }.Where(p => p.Length > 0)
            .Concat(record.RenameItems.SelectMany(i => new[] { i.OriginalPath, i.NewPath }))
            .Concat(record.Children.SelectMany(Paths));

    public async Task<UndoOperationResult> UndoAsync(Guid id)
    {
        await _undoGate.WaitAsync();
        try
        {
            var records = Records;
            var record = records.FirstOrDefault(r => r.Id == id);
            if (record == null || !record.CanUndo && !record.Children.Any(c => c.CanUndo))
                return new(false, record?.UnavailableReason ?? LocalizationText.Get("此操作已不可撤销。"), []);
            if (_tasks?.Tasks.Any(t => t.Id == record.TaskId && t.IsActive && t.State != BackgroundTaskState.NeedsAttention) == true)
                return new(false, LocalizationText.Get("请先完成或取消该任务。"), []);
            var index = Array.FindIndex(records.ToArray(), r => r.Id == id);
            var paths = Paths(record).Select(BackgroundTaskManager.Normalize).ToArray();
            if (records.Skip(index + 1).Where(r => !r.IsUndone || r.HasUndoableItems).SelectMany(Paths)
                .Select(BackgroundTaskManager.Normalize).Any(p => paths.Any(q => BackgroundTaskManager.Overlaps(p, q))))
                return new(false, LocalizationText.Get("请先撤销涉及相同文件的后续操作。"), []);
            var undoFeatures = paths.Any(VirtualPath.IsRemotePath) ? PremiumFeature.Network : PremiumFeature.None;
            if (record.Children.Prepend(record).Any(r => r.Kind == FileOperationKind.BatchRename)) undoFeatures |= PremiumFeature.BatchRename;
            return await FeatureAccess.RunUndoAsync(undoFeatures, () => FileTaskContext.RunAsync(_tasks,
                new(BackgroundTaskKind.Generic, LocalizationText.Get("撤销操作"), paths) { RequiredFeatures = undoFeatures }, async context =>
            {
                Undoing.Value = true;
                context.Task.CanRetry = false;
                var warnings = new List<string>(); var failures = new List<string>();
                try
                {
                    foreach (var item in record.Children.Prepend(record).Reverse().Where(r => r.CanUndo).ToArray())
                    {
                        await context.CheckpointAsync();
                        try { await UndoRecordAsync(item, warnings, context.CancellationToken); item.IsUndone = true; }
                        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { failures.Add(ex.Message); _logger?.LogWarning(ex, "Undo failed for {Id}", item.Id); }
                    }
                    Changed();
                    if (failures.Count > 0) context.Task.ErrorMessage = string.Join("；", failures);
                    return new UndoOperationResult(failures.Count == 0,
                        failures.Count == 0 ? LocalizationText.Get("已撤销") : string.Join("；", failures), warnings);
                }
                finally { Undoing.Value = false; }
            }));
        }
        finally { _undoGate.Release(); }
    }

    private async Task UndoRecordAsync(FileOperationRecord record, List<string> warnings, CancellationToken token)
    {
        if (record.Kind == FileOperationKind.RemoveEmptyDirectory)
        {
            if (File.Exists(record.OriginalPath)) throw new IOException(LocalizationText.Get("conflict.changed"));
            Directory.CreateDirectory(record.OriginalPath); return;
        }
        if (record.Kind == FileOperationKind.Replace)
        {
            if (record.BackupPath == null || record.Snapshot != await Task.Run(() => FileIdentitySnapshot.Capture(record.CurrentPath, true, token), token)
                || record.BackupSnapshot != await Task.Run(() => FileIdentitySnapshot.Capture(record.BackupPath, true, token), token))
                throw new IOException(LocalizationText.Get("项目已被修改或替换，无法安全撤销。"));
            if (record.OriginalPath.Length > 0)
            {
                if (await _files.ExistsAsync(record.OriginalPath)) throw new IOException(LocalizationText.Get("恢复位置已被占用，未覆盖现有项目。"));
                await _files.MoveAsync(record.CurrentPath, Path.GetDirectoryName(record.OriginalPath)!);
                foreach (var action in MetadataActions(record.CurrentPath, record.OriginalPath))
                    try { await action(); } catch (Exception ex) { warnings.Add(ex.Message); }
            }
            else
            {
                var discarded = Path.Combine(record.BackupDirectory!, "replaced-output");
                await Task.Run(() => CompositeFileService.MoveOwnedPath(record.CurrentPath, discarded));
                try { await Task.Run(() => CompositeFileService.MoveOwnedPath(record.BackupPath, record.CurrentPath)); }
                catch { await Task.Run(() => CompositeFileService.MoveOwnedPath(discarded, record.CurrentPath)); throw; }
                if (_tags != null) await _tags.DeletePathAsync(record.CurrentPath);
                if (_analysis != null) await _analysis.DeleteAnalysisForPathPrefixAsync(record.CurrentPath);
            }
            if (record.OriginalPath.Length > 0) await Task.Run(() => CompositeFileService.MoveOwnedPath(record.BackupPath, record.CurrentPath));
            foreach (var action in MetadataActions(record.BackupPath, record.CurrentPath))
                try { await action(); } catch (Exception ex) { warnings.Add(ex.Message); }
            if (record.BackupDirectory != null && Directory.Exists(record.BackupDirectory)) Directory.Delete(record.BackupDirectory, true);
            _changes?.NotifyChanged(new[] { Path.GetDirectoryName(record.CurrentPath)!, Path.GetDirectoryName(record.OriginalPath) ?? Path.GetDirectoryName(record.CurrentPath)! }, null);
            return;
        }
        if (record.Kind == FileOperationKind.BatchRename)
        {
            var rename = _rename ?? new BatchRenameService(_files, directoryChangeNotifier: _changes, fileTagService: _tags);
            var inverse = new List<BatchRenamePreviewItem>();
            foreach (var item in record.RenameItems)
            {
                if (record.RenameSnapshots.GetValueOrDefault(item.NewPath) is { } identity
                    && identity != FileIdentitySnapshot.Capture(item.NewPath, false, token))
                    throw new IOException(LocalizationText.Get("项目已被修改或替换，无法安全撤销。"));
                var entry = await _files.GetEntryAsync(item.NewPath) ?? throw new FileNotFoundException(LocalizationText.Get("重命名后的项目已不存在"), item.NewPath);
                inverse.Add(new() { Entry = entry, OriginalPath = item.NewPath, OriginalName = item.NewName,
                    NewPath = item.OriginalPath, NewName = item.OriginalName, HasSourceSnapshot = true,
                    SourceSize = entry.Size, SourceModified = entry.LastModified, SourceCreated = entry.Created,
                    SourceIsDirectory = entry.IsDirectory, SourceIsSymbolicLink = entry.IsSymbolicLink });
            }
            var result = await rename.ExecuteAsync(inverse);
            var restored = result.SuccessfulItems.Select(i => i.OriginalPath).ToHashSet(StringComparer.Ordinal);
            record.RenameItems.RemoveAll(i => restored.Contains(i.NewPath));
            warnings.AddRange(result.Warnings);
            if (record.RenameItems.Count > 0) throw new IOException(string.Join("；", result.Errors.DefaultIfEmpty(LocalizationText.Get("部分项目未撤销。"))));
            return;
        }
        if (!await _files.ExistsAsync(record.CurrentPath)) throw new FileNotFoundException(LocalizationText.Get("项目已不存在。"), record.CurrentPath);
        var content = record.Kind is FileOperationKind.Copy or FileOperationKind.Create;
        if (record.Snapshot is { } snapshot && snapshot != await Task.Run(() => FileIdentitySnapshot.Capture(record.CurrentPath, content, token), token))
            throw new IOException(LocalizationText.Get("项目已被修改或替换，无法安全撤销。"));
        string? restoredPath = null;
        if (content)
        {
            await FileTrash.MoveAsync(_files, record.CurrentPath);
            try
            {
                if (_tags != null) await _tags.DeletePathAsync(record.CurrentPath);
                if (_ratings != null) await _ratings.DeletePathAsync(record.CurrentPath);
            }
            catch (Exception ex) { warnings.Add(ex.Message); }
        }
        else
        {
            if (await _files.ExistsAsync(record.OriginalPath)) throw new IOException(LocalizationText.Get("恢复位置已被占用，未覆盖现有项目。"));
            var parent = Path.GetDirectoryName(record.OriginalPath)!;
            if (!await _files.ExistsAsync(parent)) throw new DirectoryNotFoundException(LocalizationText.Get("恢复目录已不存在。"));
            var previousPath = record.CurrentPath;
            var current = previousPath;
            var name = Path.GetFileName(record.OriginalPath);
            if (Path.GetDirectoryName(current) != parent)
            {
                var intermediate = _files.CombinePath(parent, Path.GetFileName(current));
                if (await _files.ExistsAsync(intermediate))
                {
                    var temporaryName = ".macexplorer-undo-" + Guid.NewGuid().ToString("N");
                    await _files.RenameAsync(current, temporaryName);
                    current = record.CurrentPath = _files.CombinePath(_files.GetParentPath(current), temporaryName);
                }
                await _files.MoveAsync(current, parent, false);
                current = record.CurrentPath = _files.CombinePath(parent, Path.GetFileName(current));
            }
            if (Path.GetFileName(current) != name) await _files.RenameAsync(current, name);
            restoredPath = record.OriginalPath;
            foreach (var action in MetadataActions(previousPath, restoredPath))
                try { await action(); } catch (Exception ex) { warnings.Add(ex.Message); }
        }
        try { _changes?.NotifyChanged(new[] { Path.GetDirectoryName(record.CurrentPath)!, Path.GetDirectoryName(restoredPath ?? record.CurrentPath)! }, null); }
        catch (Exception ex) { warnings.Add(ex.Message); }
    }
    private IEnumerable<Func<Task>> MetadataActions(string source, string destination)
    {
        if (_ratings != null) yield return () => _ratings.TransferPathAsync(source, destination, true);
        if (_index != null) yield return () => _index.RenameEntryAsync(source, destination, Path.GetFileName(destination));
        if (_tags != null) yield return () => _tags.UpdatePathAsync(source, destination);
        if (_analysis != null) yield return () => _analysis.UpdateFilePathAsync(source, destination);
        if (_pins != null) yield return () => _pins.UpdateFolderPathAsync(source, destination, Path.GetFileName(destination));
    }
    private void Changed()
    {
        foreach (var handler in HistoryChanged?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch (Exception ex) { _logger?.LogWarning(ex, "History observer failed"); }
    }
}
