using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private readonly Dictionary<string, BatchRenameRequest> _batchSelectionSnapshots = new(StringComparer.Ordinal);
    public string CaptureBatchRenameSelection()
    {
        var id = Guid.NewGuid().ToString("N");
        if (_batchSelectionSnapshots.Count >= 4) _batchSelectionSnapshots.Remove(_batchSelectionSnapshots.Keys.First());
        _batchSelectionSnapshots[id] = CreateBatchRenameRequest();
        return id;
    }
    public BatchRenameRequest GetBatchRenameSelection(string id) => _batchSelectionSnapshots.TryGetValue(id, out var request)
        ? request.Snapshot() : throw new InvalidOperationException("选择快照已过期，请重新生成方案");
    public bool CanBatchRename => !IsBrowseOnly && !IsArchiveView && !IsTrashActive
        && SelectedEntries.Count > 0 && SelectedEntries.All(IsBatchRenameTarget);
    public BatchRenameRequest? PendingBatchRenameRequest { get; private set; }

    internal static bool IsBatchRenameTarget(FileSystemEntry entry) => !entry.IsVirtual
        && Path.IsPathFullyQualified(entry.FullPath) && !VirtualPath.IsRemotePath(entry.FullPath);

    public BatchRenameRequest CreateBatchRenameRequest(IReadOnlyList<BatchRenameRule>? rules = null,
        BatchRenameOptions? options = null, IReadOnlyList<FileSystemEntry>? entries = null)
    {
        var targets = entries ?? GetSelectableEntries().Where(e => _selectedEntriesSet.Contains(e))
            .Concat(SelectedEntries).DistinctBy(e => e.FullPath).ToArray();
        return new BatchRenameRequest { Entries = targets, Rules = rules ?? [], Options = options ?? new() }.Snapshot();
    }

    public void RaiseRequestBatchRename(BatchRenameRequest request)
    {
        if (IsBrowseOnly || IsArchiveView || IsTrashActive || request.Entries.Count == 0
            || request.Entries.Any(e => !IsBatchRenameTarget(e)))
        { StatusText = "请选择可重命名的本地文件或文件夹"; return; }
        PendingBatchRenameRequest = request.Snapshot();
        RequestBatchRename?.Invoke();
    }

    public BatchRenameRequest? TakeBatchRenameRequest()
    {
        var request = PendingBatchRenameRequest; PendingBatchRenameRequest = null; return request;
    }
}
