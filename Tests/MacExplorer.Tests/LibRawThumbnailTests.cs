using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services.Impl;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

public sealed class LibRawThumbnailTests
{
    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "TestData", "generated-orientation-6.dng");

    [Fact]
    public void GeneratedDng_DecodesToOrientedPng()
    {
        var bytes = LibRawThumbnailDecoder.Decode(SamplePath, 48, TestContext.Current.CancellationToken);
        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, bytes[..4]);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(32, bitmap.Width);
        Assert.Equal(48, bitmap.Height);
    }

    [Fact]
    public void CancelledDecode_ThrowsAndCanBeRetried()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            LibRawThumbnailDecoder.Decode(SamplePath, 96, cancelled.Token));
        Assert.NotNull(LibRawThumbnailDecoder.Decode(SamplePath, 96, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MonochromeDng_DecodesToGrayPng()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "generated-monochrome.dng");
        var bytes = LibRawThumbnailDecoder.Decode(path, 48, TestContext.Current.CancellationToken);
        Assert.NotNull(bytes);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        var center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        Assert.Equal(center.Red, center.Green);
        Assert.Equal(center.Green, center.Blue);
    }

    [Fact]
    public void TruncatedDng_ReturnsNoImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkraw-truncated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "broken.dng");
            File.WriteAllBytes(path, File.ReadAllBytes(SamplePath)[..1024]);
            Assert.Null(LibRawThumbnailDecoder.Decode(path, 96, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ThumbnailService_UsesRawFallbackForKnownImagesAndIgnoresOldRawByteCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkraw-service-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(cache);
        try
        {
            var source = Path.Combine(root, "中文照片.dng");
            File.Copy(SamplePath, source);
            var oldKey = $"{source}:{File.GetLastWriteTimeUtc(source).Ticks}:48";
            var oldCache = Path.Combine(cache,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldKey))).ToLowerInvariant() + ".png");
            File.Copy(source, oldCache);

            var service = new MacThumbnailService(cache, 1024 * 1024, 0.8);
            Assert.True(service.IsImageFile(".DNG"));
            Assert.True(service.IsImageFile(".RAF"));
            Assert.True(service.IsImageFile(".CRW"));
            var result = await service.GetThumbnailResultAsync(source, 48, TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.NotEqual(oldCache, result.CachePath);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, result.Bytes[..4]);
            using var bitmap = SKBitmap.Decode(result.Bytes);
            Assert.NotNull(bitmap);
            Assert.Equal((32, 48), (bitmap.Width, bitmap.Height));

            // A standard image suffix also reaches LibRaw after both normal decoders fail.
            var known = Path.Combine(root, "raw-content.jpg");
            File.Copy(SamplePath, known);
            Assert.NotNull(await service.GetThumbnailResultAsync(known, 48, TestContext.Current.CancellationToken));

            var unknown = Path.Combine(root, "raw-content.unknown");
            File.Copy(SamplePath, unknown);
            Assert.Null(await service.GetThumbnailResultAsync(unknown, 48, TestContext.Current.CancellationToken));

            var hits = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => service.GetThumbnailResultAsync(source, 48, TestContext.Current.CancellationToken)));
            Assert.All(hits, hit => Assert.Equal(result.CachePath, hit?.CachePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RealCameraSample_WhenProvided_DecodesToPng()
    {
        var path = Environment.GetEnvironmentVariable("FKFINDER_REAL_RAW_SAMPLE");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "Set FKFINDER_REAL_RAW_SAMPLE to run the camera RAW check.");
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        Assert.ThrowsAny<OperationCanceledException>(() => LibRawThumbnailDecoder.Decode(path, 512, cancelled.Token));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var startMemory = Process.GetCurrentProcess().WorkingSet64;
        Assert.True(startMemory > 0, "Process resident memory is unavailable on this host.");
        var memorySamples = new List<long> { startMemory };
        var bytes = LibRawThumbnailDecoder.Decode(path, 512, TestContext.Current.CancellationToken);
        Assert.NotNull(bytes);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(512, Math.Max(bitmap.Width, bitmap.Height));
        for (var i = 0; i < 8; i++)
        {
            if (i > 0)
                Assert.NotNull(LibRawThumbnailDecoder.Decode(path, 512, TestContext.Current.CancellationToken));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            memorySamples.Add(Process.GetCurrentProcess().WorkingSet64);
        }
        var warmGrowth = memorySamples[^1] - memorySamples[1];
        Assert.True(warmGrowth < 128L * 1024 * 1024,
            $"Repeated RAW decodes grew resident memory after warm-up: {string.Join(", ", memorySamples)}.");
        var output = Environment.GetEnvironmentVariable("FKFINDER_REAL_RAW_OUTPUT");
        if (!string.IsNullOrEmpty(output)) File.WriteAllBytes(output, bytes);
        var memoryLog = Environment.GetEnvironmentVariable("FKFINDER_RAW_MEMORY_LOG");
        if (!string.IsNullOrEmpty(memoryLog)) File.WriteAllText(memoryLog,
            $"sample={path}\niterations=8\nresident_memory_samples={string.Join(',', memorySamples)}\n");
    }

    [Fact]
    public async Task RawFilesCanPopulateFolderCover()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkraw-cover-" + Guid.NewGuid().ToString("N"));
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        try
        {
            File.Copy(SamplePath, Path.Combine(photos, "color.dng"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "generated-monochrome.dng"),
                Path.Combine(photos, "gray.dng"));
            var thumbnails = new MacThumbnailService(Path.Combine(root, "cache"), 1024 * 1024, 0.8);
            var cover = await new FolderPhotoCoverService(thumbnails)
                .CreateAsync(photos, 128, TestContext.Current.CancellationToken);
            Assert.NotNull(cover);
            using var bitmap = SKBitmap.Decode(cover.Bytes);
            Assert.NotNull(bitmap);
            Assert.Equal((128, 128), (bitmap.Width, bitmap.Height));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WaitingRawRequest_DoesNotOccupyTheNormalImageGenerationSlot()
    {
        var gate = (SemaphoreSlim?)typeof(MacThumbnailService)
            .GetField("RawDecodeGate", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        Assert.NotNull(gate);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var root = Path.Combine(Path.GetTempPath(), "fkraw-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rawPath = Path.Combine(root, "queued.dng");
        File.Copy(SamplePath, rawPath);
        var rawKey = $"image-v2:{rawPath}:{File.GetLastWriteTimeUtc(rawPath).Ticks}:48";
        var rawStripe = (StringComparer.Ordinal.GetHashCode(rawKey) & int.MaxValue) % 64;
        string? pngPath = null;
        for (var i = 0; i < 64; i++)
        {
            var candidate = Path.Combine(root, $"ordinary-{i}.png");
            File.WriteAllBytes(candidate, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var key = $"image-v2:{candidate}:{File.GetLastWriteTimeUtc(candidate).Ticks}:48";
            if ((StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % 64 != rawStripe)
            {
                pngPath = candidate;
                break;
            }
            File.Delete(candidate);
        }
        Assert.NotNull(pngPath);
        var service = new MacThumbnailService(Path.Combine(root, "cache"), 1024 * 1024, 0.8, 1);
        Task<MacExplorer.Services.ThumbnailResult?>? rawTask = null;
        try
        {
            await gate.WaitAsync(timeout.Token);
            try
            {
                rawTask = service.GetThumbnailResultAsync(rawPath, 48, timeout.Token);
                await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
                Assert.False(rawTask.IsCompleted);
                var ordinary = await service.GetThumbnailResultAsync(pngPath, 48, timeout.Token)
                    .WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);
                Assert.NotNull(ordinary);
            }
            finally { gate.Release(); }
            Assert.NotNull(await rawTask.WaitAsync(timeout.Token));
        }
        finally
        {
            timeout.Cancel();
            try
            {
                if (rawTask != null)
                    try { await rawTask; }
                    catch (OperationCanceledException) { }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}
