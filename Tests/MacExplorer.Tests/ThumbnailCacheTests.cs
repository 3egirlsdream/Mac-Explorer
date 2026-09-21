using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ThumbnailCacheTests
{
    [Theory]
    [InlineData(".psd")]
    [InlineData(".PSD")]
    [InlineData(".psb")]
    public void PhotoshopFilesAreEligibleForQuickLookThumbnails(string extension)
    {
        Assert.True(MacThumbnailService.SupportsThumbnailExtension(extension));
    }

    [Theory]
    [InlineData(".epub")]
    [InlineData(".webarchive")]
    [InlineData(".docm")]
    [InlineData(".xlsm")]
    [InlineData(".pptm")]
    [InlineData(".avif")]
    [InlineData(".jxl")]
    [InlineData(".srt")]
    [InlineData(".toml")]
    [InlineData(".flac")]
    [InlineData(".vob")]
    public void ExpandedSuperPreviewFormatsAreEligibleForThumbnails(string extension)
    {
        Assert.True(MacThumbnailService.SupportsThumbnailExtension(extension));
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task MenuByteCache_ConcurrentConsumersShareCachedBytes()
    {
        var cache = new FileListView.ByteLruCache(8 * 1024 * 1024);
        var consumers = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            cache.GetOrAdd("icon", () => OnePixelPng.ToArray()))));

        Assert.All(consumers, bytes => Assert.Same(consumers[0], bytes));
        Assert.Equal(1, cache.Count);


    }

    [Fact]
    public void MenuByteCache_EvictsLeastRecentlyUsedBytesAtCapacity()
    {
        var cache = new FileListView.ByteLruCache(10);
        byte[] first = [1, 1, 1, 1, 1], second = [2, 2, 2, 2, 2], third = [3, 3, 3, 3, 3];
        cache.GetOrAdd("first", () => first);
        cache.GetOrAdd("second", () => second);
        Assert.Same(first, cache.GetOrAdd("first", () => throw new InvalidOperationException("Expected a cache hit.")));
        cache.GetOrAdd("third", () => third);

        Assert.Equal(2, cache.Count);
        Assert.Equal(10, cache.ByteCount);
        Assert.Same(first, cache.GetOrAdd("first", () => throw new InvalidOperationException("The recently read entry was evicted.")));
        Assert.Same(third, cache.GetOrAdd("third", () => throw new InvalidOperationException("The new entry was evicted.")));
        var regenerated = new byte[5];
        Assert.Same(regenerated, cache.GetOrAdd("second", () => regenerated));
    }

    [Fact]
    public async Task ThumbnailResults_UseStablePathAcrossGenerationMemoryDiskAndConcurrency()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var root = CreateTemporaryDirectory();
        var cacheDirectory = Path.Combine(root, "cache");
        var sourcePath = Path.Combine(root, "source.png");
        await File.WriteAllBytesAsync(sourcePath, OnePixelPng, testCancellation);
        try
        {
            var service = new MacThumbnailService(cacheDirectory, 1024 * 1024, 0.8);
            var generated = await service.GetThumbnailResultAsync(sourcePath, 128, testCancellation);
            Assert.NotNull(generated);
            var memoryHit = await service.GetThumbnailResultAsync(sourcePath, 128, testCancellation);
            Assert.NotNull(memoryHit);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => service.GetThumbnailResultAsync(sourcePath, 128, testCancellation)));

            Assert.True(File.Exists(generated.CachePath));
            Assert.Equal(generated.CachePath, memoryHit.CachePath);
            Assert.All(concurrent, result =>
            {
                Assert.NotNull(result);
                Assert.Equal(generated.CachePath, result.CachePath);
            });

            var freshService = new MacThumbnailService(cacheDirectory, 1024 * 1024, 0.8);
            var diskHit = await freshService.GetThumbnailResultAsync(sourcePath, 128, testCancellation);
            Assert.NotNull(diskHit);
            Assert.Equal(generated.CachePath, diskHit.CachePath);

            File.Delete(generated.CachePath);
            var recreated = await service.GetThumbnailResultAsync(sourcePath, 128, testCancellation);
            Assert.NotNull(recreated);
            Assert.Equal(generated.CachePath, recreated.CachePath);
            Assert.True(File.Exists(recreated.CachePath));
            Assert.Equal(generated.Bytes, recreated.Bytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ThumbnailDiskCache_TrimsEveryWriteToTargetAndProtectsCurrentResult()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        const long maxBytes = 16 * 1024;
        const long targetBytes = (long)(maxBytes * 0.8);
        var root = CreateTemporaryDirectory();
        var cacheDirectory = Path.Combine(root, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var ignoredTemporaryFile = Path.Combine(cacheDirectory, ".tmp-abandoned");
        await File.WriteAllBytesAsync(ignoredTemporaryFile, new byte[10_000], testCancellation);
        try
        {
            var service = new MacThumbnailService(cacheDirectory, maxBytes, 0.8);
            var firstSource = Path.Combine(root, "first.png");
            await File.WriteAllBytesAsync(firstSource, OnePixelPng, testCancellation);
            await SeedOldCacheFilesAsync(cacheDirectory, "round-one", testCancellation);

            var first = await service.GetThumbnailResultAsync(firstSource, 128, testCancellation);
            Assert.NotNull(first);

            Assert.True(File.Exists(first.CachePath));
            var firstCacheBytes = GetFinalCacheBytes(cacheDirectory);
            Assert.True(firstCacheBytes <= targetBytes, $"Expected <= {targetBytes} bytes, found {firstCacheBytes}: {string.Join(", ", DescribeCacheFiles(cacheDirectory))}");
            Assert.True(File.Exists(ignoredTemporaryFile));

            var secondSource = Path.Combine(root, "second.png");
            await File.WriteAllBytesAsync(secondSource, OnePixelPng, testCancellation);
            await SeedOldCacheFilesAsync(cacheDirectory, "round-two", testCancellation);

            var second = await service.GetThumbnailResultAsync(secondSource, 128, testCancellation);
            Assert.NotNull(second);

            Assert.True(File.Exists(second.CachePath));
            var secondCacheBytes = GetFinalCacheBytes(cacheDirectory);
            Assert.True(secondCacheBytes <= targetBytes, $"Expected <= {targetBytes} bytes, found {secondCacheBytes}: {string.Join(", ", DescribeCacheFiles(cacheDirectory))}");
            Assert.True(File.Exists(ignoredTemporaryFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedOldCacheFilesAsync(
        string directory,
        string prefix,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 4; i++)
        {
            var path = Path.Combine(directory, $"{prefix}-{i}.png");
            await File.WriteAllBytesAsync(path, new byte[8 * 1024], cancellationToken);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(2 + i));
        }
    }

    private static long GetFinalCacheBytes(string directory) => Directory
        .EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly)
        .Where(path => !Path.GetFileName(path).StartsWith(".tmp-", StringComparison.Ordinal))
        .Sum(path => new FileInfo(path).Length);

    private static IEnumerable<string> DescribeCacheFiles(string directory) => Directory
        .EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly)
        .Select(path => $"{Path.GetFileName(path)}={new FileInfo(path).Length}");

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fkfinder-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
