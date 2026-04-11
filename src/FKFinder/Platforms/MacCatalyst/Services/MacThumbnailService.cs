using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CoreGraphics;
using FKFinder.Services;
using ImageIO;
using UIKit;

namespace FKFinder.Platforms.MacCatalyst.Services;

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
            "FKFinder",
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
        var diskPath = GetDiskCachePath(filePath, maxPixelSize);
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

    private string GetDiskCachePath(string filePath, int maxPixelSize)
    {
        var input = $"{filePath}:{maxPixelSize}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hashStr = Convert.ToHexString(hash)[..32];
        return Path.Combine(_diskCacheDir, $"{hashStr}.png");
    }
}
