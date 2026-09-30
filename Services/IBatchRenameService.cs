using MacExplorer.Models;

namespace MacExplorer.Services;

/// <summary>
/// Generates batch rename previews, validates conflicts, and executes renames.
/// </summary>
public interface IBatchRenameService
{
    Task<BatchRenamePlan> GeneratePreviewAsync(BatchRenameRequest request,
        CancellationToken cancellationToken = default);

    Task<BatchRenameResult> ExecuteAsync(BatchRenamePlan plan,
        IProgress<BatchRenameProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Generate preview items for the given entries and rules.</summary>
    List<BatchRenamePreviewItem> GeneratePreview(
        IReadOnlyList<FileSystemEntry> entries,
        IReadOnlyList<BatchRenameRule> rules);

    /// <summary>Execute the batch rename based on the preview items.</summary>
    Task<BatchRenameResult> ExecuteAsync(
        List<BatchRenamePreviewItem> previewItems,
        IProgress<BatchRenameProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
