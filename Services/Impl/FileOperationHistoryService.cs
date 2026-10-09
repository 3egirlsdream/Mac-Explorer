using MacExplorer.Models;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class FileOperationHistoryService : IFileOperationHistoryService
{
    private readonly IFileService _fileService;
    private readonly IDirectoryChangeNotifier? _directoryChangeNotifier;
    private readonly IFileTagService? _fileTagService;
    private readonly ILogger<FileOperationHistoryService>? _logger;
    private readonly IBatchRenameService? _batchRename;
    private readonly SemaphoreSlim _undoGate = new(1, 1);
    private readonly Stack<FileOperationRecord> _history = new();
    private readonly object _lock = new();

    private const string TrashPath = "/.Trash";

    public FileOperationHistoryService(
        IFileService fileService,
        IDirectoryChangeNotifier? directoryChangeNotifier = null,
        IFileTagService? fileTagService = null,
        ILogger<FileOperationHistoryService>? logger = null, IBatchRenameService? batchRename = null)
    {
        _fileService = fileService;
        _directoryChangeNotifier = directoryChangeNotifier;
        _fileTagService = fileTagService;
        _logger = logger;
        _batchRename = batchRename;
    }

    public bool CanUndo
    {
        get { lock (_lock) return _history.Count > 0; }
    }

    public Task RecordRenameAsync(string oldPath, string newPath)
    {
        var record = new FileOperationRecord
        {
            Kind = FileOperationKind.Rename,
            OriginalPath = oldPath,
            CurrentPath = newPath
        };
        lock (_lock) _history.Push(record);
        return Task.CompletedTask;
    }

    public Task<Guid?> RecordBatchRenameAsync(IReadOnlyList<BatchRenamePreviewItem> items, Guid? batchId = null)
    {
        if (items.Count == 0) return Task.FromResult<Guid?>(batchId);
        lock (_lock)
        {
            if (_history.TryPeek(out var previous) && previous.Id == batchId && previous.Kind == FileOperationKind.BatchRename)
            {
                previous.RenameItems.AddRange(items);
                return Task.FromResult<Guid?>(previous.Id);
            }
            var record = new FileOperationRecord { Kind = FileOperationKind.BatchRename, RenameItems = items.ToList() };
            _history.Push(record);
            return Task.FromResult<Guid?>(record.Id);
        }
    }

    public Task<bool> UndoBatchAsync(Guid batchId) => UndoAsync(batchId);

    public Task RecordTrashAsync(string originalPath, string trashedPath)
    {
        var record = new FileOperationRecord
        {
            Kind = FileOperationKind.MoveToTrash,
            OriginalPath = originalPath,
            CurrentPath = trashedPath
        };
        lock (_lock) _history.Push(record);
        return Task.CompletedTask;
    }

    public Task RecordMoveAsync(string oldPath, string newPath)
    {
        var record = new FileOperationRecord
        {
            Kind = FileOperationKind.Move,
            OriginalPath = oldPath,
            CurrentPath = newPath
        };
        lock (_lock) _history.Push(record);
        return Task.CompletedTask;
    }

    public Task<bool> UndoLastAsync() => UndoAsync(null);

    private async Task<bool> UndoAsync(Guid? batchId)
    {
        Subscriptions.SubscriptionAccess.RequireAccess();
        await _undoGate.WaitAsync();
        try { return await UndoCoreAsync(batchId); }
        finally { _undoGate.Release(); }
    }

    private async Task<bool> UndoCoreAsync(Guid? batchId)
    {
        FileOperationRecord? record;
        lock (_lock)
        {
            if (_history.Count == 0) return false;
            if (batchId.HasValue && _history.Peek().Id != batchId) return false;
            record = _history.Pop();
        }

        try
        {
            var affectedDirs = new HashSet<string>(StringComparer.Ordinal);

            switch (record!.Kind)
            {
                case FileOperationKind.BatchRename:
                    var rename = _batchRename ?? new BatchRenameService(_fileService,
                        directoryChangeNotifier: _directoryChangeNotifier, fileTagService: _fileTagService);
                    var inverse = new List<BatchRenamePreviewItem>();
                    foreach (var item in record.RenameItems)
                    {
                        var entry = await _fileService.GetEntryAsync(item.NewPath)
                            ?? throw new FileNotFoundException("重命名后的项目已不存在", item.NewPath);
                        inverse.Add(new BatchRenamePreviewItem { Entry = entry, OriginalPath = item.NewPath,
                            OriginalName = item.NewName, NewPath = item.OriginalPath, NewName = item.OriginalName,
                            HasSourceSnapshot = true, SourceSize = entry.Size, SourceModified = entry.LastModified,
                            SourceCreated = entry.Created, SourceIsDirectory = entry.IsDirectory, SourceIsSymbolicLink = entry.IsSymbolicLink });
                    }
                    var restored = await rename.ExecuteAsync(inverse);
                    var restoredPaths = restored.SuccessfulItems.Select(i => i.OriginalPath).ToHashSet(StringComparer.Ordinal);
                    record.RenameItems.RemoveAll(i => restoredPaths.Contains(i.NewPath));
                    if (record.RenameItems.Count > 0) { lock (_lock) _history.Push(record); return false; }
                    return true;
                case FileOperationKind.Rename:
                    if (_batchRename != null)
                    {
                        var current = await _fileService.GetEntryAsync(record.CurrentPath)
                            ?? throw new FileNotFoundException("重命名后的项目已不存在", record.CurrentPath);
                        var single = await _batchRename.ExecuteAsync([new BatchRenamePreviewItem
                        {
                            OriginalPath = record.CurrentPath, OriginalName = current.Name,
                            NewPath = record.OriginalPath, NewName = Path.GetFileName(record.OriginalPath), SourceIsDirectory = current.IsDirectory
                        }]);
                        if (single.FailedCount > 0) throw new IOException(string.Join("；", single.Errors));
                        return single.SuccessCount == 1;
                    }
                    // Rename back: CurrentPath -> OriginalPath
                    if (File.Exists(record.CurrentPath) || Directory.Exists(record.CurrentPath))
                    {
                        await _fileService.RenameAsync(record.CurrentPath, Path.GetFileName(record.OriginalPath));
                        if (_fileTagService != null)
                            await _fileTagService.UpdatePathAsync(record.CurrentPath, record.OriginalPath);
                        affectedDirs.Add(Path.GetDirectoryName(record.OriginalPath) ?? "");
                        affectedDirs.Add(Path.GetDirectoryName(record.CurrentPath) ?? "");
                    }
                    break;

                case FileOperationKind.Move:
                    // Move back: CurrentPath -> OriginalPath
                    if (File.Exists(record.CurrentPath) || Directory.Exists(record.CurrentPath))
                    {
                        var destDir = Path.GetDirectoryName(record.OriginalPath) ?? "";
                        if (Directory.Exists(destDir))
                        {
                            await _fileService.MoveAsync(record.CurrentPath, destDir, overwrite: false);
                            if (_fileTagService != null)
                                await _fileTagService.UpdatePathAsync(record.CurrentPath, record.OriginalPath);
                            affectedDirs.Add(destDir);
                            affectedDirs.Add(Path.GetDirectoryName(record.CurrentPath) ?? "");
                        }
                    }
                    break;

                case FileOperationKind.MoveToTrash:
                    // Try to restore from trash
                    var trashFile = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + TrashPath,
                        Path.GetFileName(record.OriginalPath));

                    // Also try the trashed path if it was recorded
                    var candidatePath = !string.IsNullOrEmpty(record.CurrentPath) && File.Exists(record.CurrentPath)
                        ? record.CurrentPath
                        : trashFile;

                    if (File.Exists(candidatePath) || Directory.Exists(candidatePath))
                    {
                        var destDir = Path.GetDirectoryName(record.OriginalPath) ?? "";
                        if (Directory.Exists(destDir))
                        {
                            await _fileService.MoveAsync(candidatePath, destDir, overwrite: false);
                            if (_fileTagService != null)
                                await _fileTagService.UpdatePathAsync(candidatePath, record.OriginalPath);
                            affectedDirs.Add(destDir);
                        }
                    }
                    else
                    {
                        _logger?.LogWarning("Could not find trashed file to restore: {Path}", record.OriginalPath);
                        return false;
                    }
                    break;
            }

            if (affectedDirs.Count > 0)
                _directoryChangeNotifier?.NotifyChanged(affectedDirs.ToArray(), null);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to undo file operation: {Kind} {Path}", record!.Kind, record.OriginalPath);
            // Push the record back so it can be retried
            lock (_lock) _history.Push(record);
            return false;
        }
    }
}
