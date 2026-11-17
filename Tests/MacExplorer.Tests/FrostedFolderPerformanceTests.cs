using Avalonia.Headless.XUnit;
using FolderIconDemo;
using MacExplorer.Services.Impl;
using MacExplorer.Controls;
using MacExplorer.Models;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "FolderPhotoCover")]
public sealed class FrostedFolderPerformanceTests
{
    [AvaloniaFact]
    public void WideGlassFoldersMatchDefaultProportionsWithoutStretchingThePhoto()
    {
        using var photo = new SKBitmap(300, 200);
        photo.Erase(SKColors.Red);
        using (var canvas = new SKCanvas(photo))
        using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.Blue })
            canvas.DrawCircle(150, 100, 35, paint);
        using var rendered = SKBitmap.Decode(FrostedFolderRenderer.RenderPng(photo, null, 256));
        var circle = (from y in Enumerable.Range(0, rendered.Height)
                      from x in Enumerable.Range(0, rendered.Width)
                      let color = rendered.GetPixel(x, y)
                      where color.Blue > 200 && color.Red < 50 && color.Green < 50
                      select (X: x, Y: y)).ToArray();
        Assert.NotEmpty(circle);
        var circleWidth = circle.Max(p => p.X) - circle.Min(p => p.X) + 1;
        var circleHeight = circle.Max(p => p.Y) - circle.Min(p => p.Y) + 1;
        Assert.InRange(Math.Abs(circleWidth - circleHeight), 0, 2);

        foreach (var png in new[] { PlainFolderRenderer.RenderPng(256), FrostedFolderRenderer.RenderPng(photo, null, 256) })
        {
            using var folder = SKBitmap.Decode(png);
            var opaque = (from y in Enumerable.Range(0, folder.Height)
                          from x in Enumerable.Range(0, folder.Width)
                          where folder.GetPixel(x, y).Alpha == 255
                          select (X: x, Y: y)).ToArray();
            var width = opaque.Max(p => p.X) - opaque.Min(p => p.X) + 1;
            var height = opaque.Max(p => p.Y) - opaque.Min(p => p.Y) + 1;
            Assert.InRange(width / (float)height, 1.24f, 1.30f);
        }
    }

    [AvaloniaFact]
    public void RasterFollowsRequestedSizeAndPreservesImageChannels()
    {
        using var photo = new SKBitmap(80, 80);
        photo.Erase(new SKColor(215, 45, 20));
        foreach (var pixels in new[] { 48, 112, 256 })
        {
            using var output = SKBitmap.Decode(FrostedFolderRenderer.RenderPng(photo, null, pixels));
            Assert.Equal(pixels, output.Width);
            Assert.Equal(pixels, output.Height);
            Assert.Equal(0, output.GetPixel(0, 0).Alpha);
            var center = output.GetPixel(pixels / 2, pixels / 2);
            Assert.Equal(new SKColor(215, 45, 20), center);
            Assert.Equal(center, output.GetPixel(pixels / 2, (int)(pixels * .74)));
            var frosted = output.GetPixel(pixels / 2, (int)(pixels * .84));
            Assert.NotEqual(center, frosted);
            Assert.True(frosted.Red > frosted.Green && frosted.Red > frosted.Blue);
        }
    }

    [AvaloniaFact]
    public async Task ConcurrentRequestsShareTheExistingThumbnailCache()
    {
        using var cache = new HomeThumbnailCache();
        var service = new FolderCoverRenderService();
        var source = Photo();
        var entry = new FileSystemEntry { FullPath = "/demo/shared", ThumbnailUrl = "v1:11:10" };
        var material = new FolderCoverMaterial(FrostedFolderRenderer.DefaultFrost, 10, false, false);
        var first = cache.AcquireAsync(entry, 112, (_, p, ct) => service.CreateAsync(source, null, p, material, ct), default);
        var second = cache.AcquireAsync(entry, 112, (_, p, ct) => service.CreateAsync(source, null, p, material, ct), default);
        using var a = await first;
        using var b = await second;
        Assert.NotNull(a.Bitmap);
        Assert.Same(a.Bitmap, b.Bitmap);
        Assert.Equal(1, service.Generated);
        using var hit = cache.TryAcquire(entry, 112);
        Assert.Same(a.Bitmap, hit?.Bitmap);
        Assert.Equal(1, service.Generated);
    }

    [AvaloniaFact]
    public async Task ACancelledConsumerDoesNotInvalidateAnotherConsumer()
    {
        using var cache = new HomeThumbnailCache();
        var service = new FolderCoverRenderService();
        var source = Photo();
        var entry = new FileSystemEntry { FullPath = "/demo/shared-cancellation", ThumbnailUrl = "v1" };
        var material = new FolderCoverMaterial(FrostedFolderRenderer.DefaultFrost, 10, false, false);
        using var cancellation = new CancellationTokenSource();
        var cancelled = cache.AcquireAsync(entry, 112,
            (_, p, ct) => service.CreateAsync(source, null, p, material, ct), cancellation.Token);
        var remaining = cache.AcquireAsync(entry, 112,
            (_, p, ct) => service.CreateAsync(source, null, p, material, ct), default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        using var lease = await remaining;
        Assert.NotNull(lease.Bitmap);
        Assert.Equal(1, service.Generated);
    }

    [AvaloniaFact]
    public async Task BackgroundRenderingIsBoundedAndPendingWorkCanBeCancelled()
    {
        var service = new FolderCoverRenderService();
        var material = new FolderCoverMaterial(FrostedFolderRenderer.DefaultFrost, 10, false, false);
        var source = Photo();
        var batch = Enumerable.Range(0, 8).Select(_ => service.CreateAsync(source, null, 112, material, default));
        await Task.WhenAll(batch);
        Assert.InRange(service.MaximumActive, 1, 2);
        Assert.Equal(8, service.Generated);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(source, null, 1024, material, cancellation.Token));
        Assert.Equal(8, service.Generated);
    }

    [AvaloniaFact]
    public async Task ChangedMaterialAndPixelSizeHaveIndependentCacheEntries()
    {
        using var cache = new HomeThumbnailCache();
        var service = new FolderCoverRenderService();
        var source = Photo();
        var file = new FileSystemEntry { FullPath = "/demo/changed", ThumbnailUrl = "v1:11" };
        var material = new FolderCoverMaterial(FrostedFolderRenderer.DefaultFrost, 10, false, false);
        using var first = await cache.AcquireAsync(file, 112, (_, p, ct) => service.CreateAsync(source, null, p, material, ct), default);
        using var larger = await cache.AcquireAsync(file, 256, (_, p, ct) => service.CreateAsync(source, null, p, material, ct), default);
        var changed = new FileSystemEntry { FullPath = file.FullPath, ThumbnailUrl = "v1:30" };
        using var third = await cache.AcquireAsync(changed, 112,
            (_, p, ct) => service.CreateAsync(source, null, p, material with { Frost = .30f }, ct), default);
        Assert.NotSame(first.Bitmap, larger.Bitmap);
        Assert.NotSame(first.Bitmap, third.Bitmap);
        Assert.NotNull(larger.Bitmap);
        Assert.Equal(3, service.Generated);
    }

    private static byte[] Photo()
    {
        using var photo = new SKBitmap(80, 80);
        photo.Erase(SKColors.Coral);
        using var png = photo.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
