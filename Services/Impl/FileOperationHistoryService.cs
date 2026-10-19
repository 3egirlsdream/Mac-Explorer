using MacExplorer.Models;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public sealed class FileOperationHistoryService : IFileOperationHistoryService
{
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

    private async Task AddAsync(FileOperationKind kind, string original, string current, string? reason = null)
    {
        if (IsUndoing) return;
        var record = new FileOperationRecord { Kind = kind, OriginalPath = original, CurrentPath = current,
            TaskId = FileTaskContext.Current?.Task.Id, UnavailableReason = reason };
        if (VirtualPath.IsRemotePath(original) || VirtualPath.IsRemotePath(current)) record.UnavailableReason = LocalizationText.Get("远程修改不可撤销。");
        if (record.UnavailableReason == null)
        {
            try
            {
                record.Snapshot = await Task.Run(() => FileIdentitySnapshot.Capture(current, kind is FileOperationKind.Copy or FileOperationKind.Create));
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
        Changed(); return Task.FromResult<Guid?>(record.Id);
    }
    public async Task<bool> UndoBatchAsync(Guid batchId) => (await UndoAsync(batchId)).Success;
    public async Task<bool> UndoLastAsync()
    {
        var record = Records.LastOrDefault(r => r.HasUndoableItems);
        return record != null && (await UndoAsync(record.Id)).Success;
    }
    public void ClearHistory() { lock (_lock) _records.Clear(); Changed(); }

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
            return await FileTaskContext.RunAsync(_tasks, new(BackgroundTaskKind.Generic, LocalizationText.Get("撤销操作"), paths), async context =>
            {
                Undoing.Value = true;
                context.Task.CanRetry = false;
                var warnings = new List<string>(); var failures = new List<string>();
                try
                {
                    foreach (var item in record.Children.Prepend(record).Reverse().Where(r => r.CanUndo).ToArray())
                    {
                        await context.CheckpointAsync();
                        try { await UndoRecordAsync(item, warnings); item.IsUndone = true; }
                        catch (Exception ex) { failures.Add(ex.Message); _logger?.LogWarning(ex, "Undo failed for {Id}", item.Id); }
                    }
                    Changed();
                    if (failures.Count > 0) context.Task.ErrorMessage = string.Join("；", failures);
                    return new UndoOperationResult(failures.Count == 0,
                        failures.Count == 0 ? LocalizationText.Get("已撤销") : string.Join("；", failures), warnings);
                }
                finally { Undoing.Value = false; }
            });
        }
        finally { _undoGate.Release(); }
    }

    private async Task UndoRecordAsync(FileOperationRecord record, List<string> warnings)
    {
        Subscriptions.SubscriptionAccess.RequireAccess();
        if (record.Kind == FileOperationKind.BatchRename)
        {
            var rename = _rename ?? new BatchRenameService(_files, directoryChangeNotifier: _changes, fileTagService: _tags);
            var inverse = new List<BatchRenamePreviewItem>();
            foreach (var item in record.RenameItems)
            {
                if (record.RenameSnapshots.GetValueOrDefault(item.NewPath) is { } identity
                    && identity != FileIdentitySnapshot.Capture(item.NewPath, false))
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
        if (record.Snapshot is { } snapshot && snapshot != await Task.Run(() => FileIdentitySnapshot.Capture(record.CurrentPath, content)))
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
