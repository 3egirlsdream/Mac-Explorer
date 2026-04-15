using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IImageAnalysisService
{
    Task<ImageAnalysisResult> AnalyzeImageAsync(string filePath, CancellationToken ct = default);
    float ComputeFaceDistance(byte[] print1, byte[] print2);
}
