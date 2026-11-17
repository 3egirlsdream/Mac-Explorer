using Avalonia.Headless.XUnit;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FolderPhotoCover")]
public sealed class FolderArtworkTests
{
    [AvaloniaFact]
    public void EmbeddedIconsMatchGeneratorAndArbitrarySizesUseRequestedDimensions()
    {
        foreach (var style in new[] { FolderPhotoCoverStyle.Frosted, FolderPhotoCoverStyle.Skeuomorphic })
        {
            foreach (var pixels in FolderArtwork.IconSizes)
            {
                var expected = style == FolderPhotoCoverStyle.Frosted ? PlainFolderRenderer.RenderPng(pixels)
                    : SkeuomorphicFolderRenderer.RenderPng(pixels, cacheMaterials: false);
                using var a = SKBitmap.Decode(expected);
                using var b = SKBitmap.Decode(FolderArtwork.ReadIcon(pixels, style));
                Assert.Equal(a.Bytes, b.Bytes);
            }
            using var resized = SKBitmap.Decode(FolderArtwork.ReadIcon(83, style));
            Assert.Equal(83, resized.Width);
            Assert.Equal(83, resized.Height);
        }
    }

    [AvaloniaFact]
    public void MissingOrCorruptAssetsReturnFallbackSignal()
    {
        Assert.Null(FolderArtwork.ReadIcon(48, FolderPhotoCoverStyle.Frosted, _ => throw new FileNotFoundException()));
        Assert.Null(FolderArtwork.ReadIcon(48, FolderPhotoCoverStyle.Skeuomorphic, _ => new MemoryStream([1, 2, 3])));
        Assert.Null(FolderArtwork.ReadIcon(48, FolderPhotoCoverStyle.Default, _ => throw new InvalidOperationException("Must not open an asset")));
    }

    [AvaloniaFact]
    public void CachedPaperMaterialsPreservePixelsAndCancellationReleasesLeases()
    {
        using var first = new SKBitmap(80, 50); first.Erase(new SKColor(80, 150, 210, 120));
        using var second = new SKBitmap(50, 80); second.Erase(SKColors.Coral);
        foreach (var pixels in new[] { 48, 112, 256 })
        {
            using var a = SKBitmap.Decode(SkeuomorphicFolderRenderer.RenderPng(pixels, [first, second], cacheMaterials: false));
            using var b = SKBitmap.Decode(SkeuomorphicFolderRenderer.RenderPng(pixels, [first, second]));
            Assert.Equal(a.Bytes, b.Bytes);
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SkeuomorphicFolderRenderer.RenderPng(256, [first], cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => FrostedFolderRenderer.RenderPng(first, second, 256, token: cancellation.Token));
        Assert.Equal(0, FolderMaterialCache.Shared.GetMemoryState().Pending);
        Assert.InRange(FolderMaterialCache.Shared.CachedBytes, 0, 4L * 1024 * 1024);
    }

    [AvaloniaFact]
    public void EvictedMaterialsLiveUntilLastBorrowerReleasesThem()
    {
        using var cache = new FolderMaterialCache(16 * 16 * 4);
        using var first = cache.Acquire("first", () => Image(SKColors.Red));
        var same = cache.Acquire("first", () => throw new InvalidOperationException("Duplicate creation"));
        Assert.Same(first.Image, same.Image);
        var second = cache.Acquire("second", () => Image(SKColors.Blue));
        Assert.Equal(1024, cache.CachedBytes);
        Assert.Equal(2048, cache.GetMemoryState().Bytes);
        Assert.NotEqual(IntPtr.Zero, first.Image.Handle);
        same.Dispose(); same.Dispose();
        Assert.NotEqual(IntPtr.Zero, first.Image.Handle);
        first.Dispose();
        Assert.Equal(IntPtr.Zero, first.Image.Handle);
        cache.Dispose();
        Assert.NotEqual(IntPtr.Zero, second.Image.Handle);
        second.Dispose();
        Assert.Equal(0, cache.GetMemoryState().Bytes);
        Assert.Equal(0, cache.GetMemoryState().Pending);
    }

    [AvaloniaFact]
    public async Task ConcurrentMissesShareOnePublishedMaterialAndDisposeLosers()
    {
        using var cache = new FolderMaterialCache(1024);
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var images = new System.Collections.Concurrent.ConcurrentBag<SKImage>();
        FolderMaterialCache.Lease Acquire() => cache.Acquire("same", () =>
        {
            var image = Image(SKColors.Coral); images.Add(image); ready.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return image;
        });
        var first = Task.Run(Acquire); var second = Task.Run(Acquire);
        try { Assert.True(ready.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); }
        using var a = await first; using var b = await second;
        Assert.Same(a.Image, b.Image);
        Assert.Single(images, image => image.Handle == IntPtr.Zero);
        cache.TrimMemory(DateTime.MaxValue);
        Assert.Equal(0, cache.CachedBytes);
        Assert.Equal(1024, cache.GetMemoryState().Bytes);
    }

    private static SKImage Image(SKColor color)
    {
        using var bitmap = new SKBitmap(16, 16); bitmap.Erase(color);
        return SKImage.FromBitmap(bitmap);
    }
}
