namespace FKFinder.Services;

public interface IThumbnailService
{
    Task<byte[]?> GetThumbnailAsync(string filePath, int maxPixelSize, CancellationToken ct = default);
    bool IsImageFile(string extension);
    void EvictFromCache(string filePath);
}
