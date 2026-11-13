using MacExplorer.Services.Impl;
using MacExplorer.Models;
using Microsoft.Extensions.Logging;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private DocumentIndexService? _documentIndex;
    private CancellationTokenSource? _documentIndexCts;
    internal void UseDocumentIndex(DocumentIndexService service)
    { _documentIndex = service; _ai.UsesDocumentIndex = true; }

    private void CancelDocumentIndex()
    {
        _documentIndexCts?.Cancel();
        _documentIndexCts?.Dispose();
        _documentIndexCts = null;
    }

    private async Task IndexVisibleDocumentsAsync(IReadOnlyList<FileSystemEntry> entries, CancellationToken directoryToken)
    {
        CancelDocumentIndex();
        if (_documentIndex == null || !_workspaceVisible || !IsAiAnalysisEnabled || IsSearchMode
            || (_settingsService?.Get("ai_analysis_enabled", true) == false)) return;
        _documentIndexCts = CancellationTokenSource.CreateLinkedTokenSource(directoryToken);
        var token = _documentIndexCts.Token;
        try { await _documentIndex.IndexDirectoryAsync(entries, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _logger?.LogWarning(ex, "Document indexing failed"); }
    }

    private void ResumeDocumentIndex()
    {
        if (_disposed || IsHomePage || IsAiView || IsRemoteView || IsArchiveView || IsTagView || IsSearchMode) return;
        CancellationToken token;
        lock (_directoryWorkLock) token = _directoryWorkCts.Token;
        _ = IndexVisibleDocumentsAsync(_sortFilter.RawEntries.ToArray(), token);
    }
}
