using Avalonia.Headless.XUnit;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed class HomeThumbnailCacheTests
{
    [AvaloniaFact]
    public async Task ExpandedFolderReusesDecodedPreviewWithoutCallingProviderAgain()
    {
        using var cache = new HomeThumbnailCache();
        var file = new FileSystemEntry { FullPath = "/tmp/image.png", Name = "image.png" };
        var calls = 0;
        using var bitmap = new SkiaSharp.SKBitmap(2, 2);
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        Task<ThumbnailResult?> Provider(FileSystemEntry _, int pixels, CancellationToken token)
        {
            calls++;
            return Task.FromResult<ThumbnailResult?>(new ThumbnailResult(encoded.ToArray(), "cache.png"));
        }
        using var preview = await cache.AcquireAsync(file, 96, Provider, default);
        Assert.NotNull(preview.Bitmap);
        using var expanded = await cache.AcquireAsync(file, 96, Provider, default);
        Assert.Same(preview.Bitmap, expanded.Bitmap);
        Assert.Equal(1, calls);
        preview.Dispose(); expanded.Dispose();
        using var reopened = cache.TryAcquire(file, 96);
        Assert.NotNull(reopened);
        Assert.Same(preview.Bitmap, reopened.Bitmap);
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public async Task CancellingOneConsumerDoesNotCancelTheOther()
    {
        using var cache = new HomeThumbnailCache();
        var file = new FileSystemEntry { FullPath = "/tmp/image.png" };
        var started = new TaskCompletionSource<CancellationToken>();
        var completed = new TaskCompletionSource<ThumbnailResult?>();
        Task<ThumbnailResult?> Provider(FileSystemEntry _, int pixels, CancellationToken token)
        { started.TrySetResult(token); return completed.Task; }
        using var cancel = new CancellationTokenSource();
        var first = cache.AcquireAsync(file, 96, Provider, cancel.Token);
        var second = cache.AcquireAsync(file, 96, Provider, default);
        var providerToken = await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        Assert.False(providerToken.IsCancellationRequested);
        completed.SetResult(null);
        using var result = await second;
    }
}
