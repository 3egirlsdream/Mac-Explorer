using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Thumbnail")]
public sealed class MemoryCacheLifetimeTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [AvaloniaFact]
    public void BorrowedBitmapSurvivesCacheEvictionAndUnusedBitmapsAreNotRooted()
    {
        var cache = new WeakBitmapCache(1024, maxEntries: 1);
        using var first = new Bitmap(new MemoryStream(Png));
        cache.Add("first", first);
        var unused = AddUnusedBitmap(cache);
        Assert.False(cache.TryGet("first", out _));
        Assert.Equal(1, first.PixelSize.Width);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(unused.IsAlive);
        Assert.False(cache.TryGet("unused", out _));
        cache.Add("first", first);
        cache.TrimMemory(DateTime.UtcNow.AddMinutes(1));
        Assert.False(cache.TryGet("first", out _));
        Assert.Equal(1, first.PixelSize.Height);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AddUnusedBitmap(WeakBitmapCache cache)
    {
        var bitmap = new Bitmap(new MemoryStream(Png));
        cache.Add("unused", bitmap);
        return new(bitmap);
    }

    [Fact]
    public async Task IdleThumbnailBytesAreReloadedFromDiskWithoutRegeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-idle-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new MacThumbnailService(root, 1024 * 1024, .8);
            var first = await service.GetOrCreateThumbnailAsync("idle", async (path, token) =>
            {
                await File.WriteAllBytesAsync(path, Png, token);
                return Png.ToArray();
            }, false, default);
            service.TrimMemory(DateTime.UtcNow.AddMinutes(1));
            var second = await service.GetOrCreateThumbnailAsync("idle", (_, _) => throw new Exception("The disk cache should be reused"), false, default);
            Assert.NotNull(first); Assert.NotNull(second);
            Assert.Equal(first.Bytes, second.Bytes);
            Assert.NotSame(first.Bytes, second.Bytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
