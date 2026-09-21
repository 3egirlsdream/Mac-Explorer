using MacExplorer.Models;

namespace MacExplorer.Services;

public readonly record struct PdfAnalysisProgress(int CompletedPages, int TotalPages);

public interface IPdfTextExtractionService
{
    Task<IReadOnlyList<RecognizedText>> ExtractAsync(string filePath,
        IProgress<PdfAnalysisProgress>? progress = null, CancellationToken ct = default);
}
