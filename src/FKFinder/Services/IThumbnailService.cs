namespace FKFinder.Services;

public interface IThumbnailService
{
    Task<byte[]?> GetThumbnailAsync(string filePath, int maxPixelSize, CancellationToken ct = default);
    Task<byte[]?> GetFaceCropAsync(string filePath, float bx, float by, float bw, float bh, int maxPixelSize = 128, CancellationToken ct = default);
    bool IsImageFile(string extension);
    void EvictFromCache(string filePath);
}
