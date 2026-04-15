using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CoreGraphics;
using MacExplorer.Services;
using ImageIO;
using UIKit;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacThumbnailService : IThumbnailService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif",
        ".webp", ".heic", ".heif",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".pef", ".raw"
    };

    private readonly ConcurrentDictionary<string, byte[]> _memoryCache = new();
    private readonly string _diskCacheDir;
    private const int MaxMemoryCacheEntries = 500;

    public MacThumbnailService()
    {
        _diskCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MacExplorer",
            "thumbnail-cache");

        if (!Directory.Exists(_diskCacheDir))
            Directory.CreateDirectory(_diskCacheDir);
    }

    public bool IsImageFile(string extension)
    {
        return !string.IsNullOrEmpty(extension) && ImageExtensions.Contains(extension);
    }

    public async Task<byte[]?> GetThumbnailAsync(string filePath, int maxPixelSize, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        var cacheKey = $"{filePath}:{maxPixelSize}";

        // L1: Memory cache
        if (_memoryCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // L2: Disk cache
        var diskPath = GetDiskCachePath(cacheKey);
        if (File.Exists(diskPath))
        {
            try
            {
                var sourceModified = File.GetLastWriteTimeUtc(filePath);
                var cacheModified = File.GetLastWriteTimeUtc(diskPath);
                if (cacheModified > sourceModified)
                {
                    var diskBytes = await File.ReadAllBytesAsync(diskPath, ct);
                    AddToMemoryCache(cacheKey, diskBytes);
                    return diskBytes;
                }
            }
            catch { }
        }

        // Generate thumbnail using native CGImageSource
        var bytes = await Task.Run(() => GenerateThumbnailNative(filePath, maxPixelSize), ct);
        if (bytes == null)
            return null;

        // Store in caches
        AddToMemoryCache(cacheKey, bytes);
        _ = Task.Run(async () =>
        {
            try { await File.WriteAllBytesAsync(diskPath, bytes, CancellationToken.None); }
            catch { }
        });

        return bytes;
    }

    public void EvictFromCache(string filePath)
    {
        var keysToRemove = _memoryCache.Keys.Where(k => k.StartsWith(filePath + ":")).ToList();
        foreach (var key in keysToRemove)
            _memoryCache.TryRemove(key, out _);
    }

    public async Task<byte[]?> GetFaceCropAsync(string filePath, float bx, float by, float bw, float bh, int maxPixelSize = 128, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath) || bw <= 0 || bh <= 0)
            return null;

        var cacheKey = $"face:{filePath}:{bx:F3}:{by:F3}:{bw:F3}:{bh:F3}:{maxPixelSize}";

        if (_memoryCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var diskPath = GetDiskCachePath(cacheKey);
        if (File.Exists(diskPath))
        {
            try
            {
                var sourceModified = File.GetLastWriteTimeUtc(filePath);
                var cacheModified = File.GetLastWriteTimeUtc(diskPath);
                if (cacheModified > sourceModified)
                {
                    var diskBytes = await File.ReadAllBytesAsync(diskPath, ct);
                    AddToMemoryCache(cacheKey, diskBytes);
                    return diskBytes;
                }
            }
            catch { }
        }

        var bytes = await Task.Run(() => GenerateFaceCropNative(filePath, bx, by, bw, bh, maxPixelSize), ct);
        if (bytes == null)
            return null;

        AddToMemoryCache(cacheKey, bytes);
        _ = Task.Run(async () =>
        {
            try { await File.WriteAllBytesAsync(diskPath, bytes, CancellationToken.None); }
            catch { }
        });

        return bytes;
    }

    private byte[]? GenerateFaceCropNative(string filePath, float bx, float by, float bw, float bh, int maxPixelSize)
    {
        try
        {
            var url = Foundation.NSUrl.FromFilename(filePath);
            if (url == null) return null;

            using var imageSource = CGImageSource.FromUrl(url, null);
            if (imageSource == null) return null;

            // Load full image to get dimensions
            using var fullImage = imageSource.CreateImage(0, null);
            if (fullImage == null) return null;

            var imgW = (float)fullImage.Width;
            var imgH = (float)fullImage.Height;

            // Vision bounding box: origin at bottom-left, normalized [0,1]
            // CGImage: origin at top-left, pixel coordinates
            var cropX = bx * imgW;
            var cropY = (1f - by - bh) * imgH; // Flip Y axis
            var cropW = bw * imgW;
            var cropH = bh * imgH;

            // Expand region by 30% for better framing
            var expandX = cropW * 0.3f;
            var expandY = cropH * 0.3f;
            cropX = Math.Max(0, cropX - expandX);
            cropY = Math.Max(0, cropY - expandY);
            cropW = Math.Min(imgW - cropX, cropW + expandX * 2);
            cropH = Math.Min(imgH - cropY, cropH + expandY * 2);

            var cropRect = new CGRect(cropX, cropY, cropW, cropH);
            using var croppedImage = fullImage.WithImageInRect(cropRect);
            if (croppedImage == null) return null;

            // Scale down if needed
            var scale = Math.Min(1f, (float)maxPixelSize / Math.Max(cropW, cropH));
            var outW = (int)(cropW * scale);
            var outH = (int)(cropH * scale);

            using var colorSpace = CGColorSpace.CreateDeviceRGB();
            using var context = new CGBitmapContext(
                null, outW, outH, 8, outW * 4,
                colorSpace, CGImageAlphaInfo.PremultipliedLast);
            context.DrawImage(new CGRect(0, 0, outW, outH), croppedImage);

            using var finalImage = context.ToImage();
            if (finalImage == null) return null;

            using var uiImage = new UIImage(finalImage);
            using var pngData = uiImage.AsPNG();
            if (pngData == null) return null;

            return pngData.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Face crop failed for {filePath}: {ex.Message}");
            return null;
        }
    }

    private byte[]? GenerateThumbnailNative(string filePath, int maxPixelSize)
    {
        try
        {
            var url = Foundation.NSUrl.FromFilename(filePath);
            if (url == null) return null;

            using var imageSource = CGImageSource.FromUrl(url, null);
            if (imageSource == null) return null;

            var options = new CGImageThumbnailOptions
            {
                CreateThumbnailFromImageAlways = true,
                CreateThumbnailWithTransform = true,
                MaxPixelSize = maxPixelSize
            };

            using var cgImage = imageSource.CreateThumbnail(0, options);
            if (cgImage == null) return null;

            using var uiImage = new UIImage(cgImage);
            using var pngData = uiImage.AsPNG();
            if (pngData == null) return null;

            return pngData.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Thumbnail generation failed for {filePath}: {ex.Message}");
            return null;
        }
    }

    private void AddToMemoryCache(string key, byte[] data)
    {
        if (_memoryCache.Count >= MaxMemoryCacheEntries)
        {
            // Simple eviction: remove roughly half the entries
            var keysToRemove = _memoryCache.Keys.Take(MaxMemoryCacheEntries / 2).ToList();
            foreach (var k in keysToRemove)
                _memoryCache.TryRemove(k, out _);
        }
        _memoryCache[key] = data;
    }

    private string GetDiskCachePath(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hashStr = Convert.ToHexString(hash)[..32];
        return Path.Combine(_diskCacheDir, $"{hashStr}.png");
    }
}
