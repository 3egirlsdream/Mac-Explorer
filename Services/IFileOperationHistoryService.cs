using MacExplorer.Models;

namespace MacExplorer.Services;

/// <summary>
/// Records and undoes reversible local file operations.
/// </summary>
public interface IFileOperationHistoryService
{
    /// <summary>Record a rename operation for potential undo.</summary>
    Task RecordRenameAsync(string oldPath, string newPath);

    async Task<Guid?> RecordBatchRenameAsync(IReadOnlyList<BatchRenamePreviewItem> items, Guid? batchId = null)
    {
        foreach (var item in items) await RecordRenameAsync(item.OriginalPath, item.NewPath);
        return null;
    }
    Task<bool> UndoBatchAsync(Guid batchId) => Task.FromResult(false);

    /// <summary>Record a move-to-trash operation for potential undo.</summary>
    Task RecordTrashAsync(string originalPath, string trashedPath);

    /// <summary>Record a move operation for potential undo.</summary>
    Task RecordMoveAsync(string oldPath, string newPath);

    /// <summary>Undo the most recent reversible operation. Returns true if an operation was undone.</summary>
    Task<bool> UndoLastAsync();

    /// <summary>Check if there are any undoable operations.</summary>
    bool CanUndo { get; }
}

/// <summary>Represents a single undoable file operation.</summary>
public enum FileOperationKind
{
    Rename,
    MoveToTrash,
    Move,
    BatchRename
}

/// <summary>Represents a recorded file operation that can be undone.</summary>
public class FileOperationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<BatchRenamePreviewItem> RenameItems { get; set; } = [];
    public FileOperationKind Kind { get; set; }
    public string OriginalPath { get; set; } = "";
    public string CurrentPath { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
