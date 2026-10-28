using MacExplorer.Controls;
using Avalonia.Headless.XUnit;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "FolderPhotoCover")]
public sealed class FolderPhotoCoverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("FolderPhotoCover_").FullName;

    [Fact]
    [Trait("Kind", "Unit")]
    public void SelectsOnlyFirstThreeDirectImagesByFileName()
    {
        var folder = Path.Combine(_root, "Album");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "d.png", "B.JPG", "a.png", "c.png", "notes.txt" })
            File.WriteAllBytes(Path.Combine(folder, name), [1]);
        var nested = Path.Combine(folder, "Nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "00.png"), [1]);

        var paths = FolderPhotoCoverService.SelectPhotoPaths(folder,
            extension => extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase), CancellationToken.None);

        Assert.Equal(new[] { "a.png", "B.JPG", "c.png" }, paths.Select(Path.GetFileName));
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task EmptyMissingAndBrokenPhotosFallBackWithoutReadingBeyondFirstThree()
    {
        var folder = Path.Combine(_root, "Album");
        Directory.CreateDirectory(folder);
        var thumbnails = new StubThumbnails();
        var service = new FolderPhotoCoverService(thumbnails);
        Assert.Null(await service.CreateAsync(folder, 64, CancellationToken.None));
        Assert.Null(await service.CreateAsync(Path.Combine(_root, "missing"), 64, CancellationToken.None));

        foreach (var name in new[] { "a.png", "b.png", "c.png", "d.png" })
            File.WriteAllBytes(Path.Combine(folder, name), [1]);
        thumbnails.BrokenNames.Add("b.png");
        var result = await service.CreateAsync(folder, 64, CancellationToken.None);

        Assert.NotNull(result);
        using var bitmap = SKBitmap.Decode(result.Bytes);
        Assert.Equal(64, bitmap.Width);
        Assert.Equal(64, bitmap.Height);
        Assert.Equal(new[] { "a.png", "b.png", "c.png" }, thumbnails.Requested.Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(1, 64)]
    [InlineData(2, 64)]
    [InlineData(3, 64)]
    [InlineData(3, 112)]
    [Trait("Kind", "Unit")]
    public async Task OneToThreePhotosRemainVisibleInsideTheFolder(int count, int pixels)
    {
        var folder = Path.Combine(_root, $"Album-{count}");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "a.png", "b.png", "c.png" }.Take(count))
            File.WriteAllBytes(Path.Combine(folder, name), [1]);
        var thumbnails = new StubThumbnails();

        var result = await new FolderPhotoCoverService(thumbnails).CreateAsync(folder, pixels, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(count, thumbnails.Requested.Count);
        // Photo overlays must preserve the ordinary folder artwork outside the cards.
        using var folderImage = SKImage.FromEncodedData(SvgIconCache.RenderFolderPng(256));
        using var expectedSurface = SKSurface.Create(new SKImageInfo(pixels, pixels, SKColorType.Bgra8888, SKAlphaType.Premul));
        expectedSurface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true };
        expectedSurface.Canvas.DrawImage(folderImage, new SKRect(0, 0, pixels, pixels),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        using var expectedImage = expectedSurface.Snapshot();
        using var expected = SKBitmap.FromImage(expectedImage);
        using var bitmap = SKBitmap.Decode(result.Bytes);
        Assert.Equal(pixels, bitmap.Width);
        Assert.Equal(pixels, bitmap.Height);
        for (var y = 0; y < pixels; y++)
        {
            if (y >= pixels * 0.15 && y <= pixels * 0.9) continue;
            for (var x = 0; x < pixels; x++)
                Assert.Equal(expected.GetPixel(x, y), bitmap.GetPixel(x, y));
        }
        var colors = new int[3];
        var lowerPhotoPixels = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            var isPhoto = false;
            if (pixel.Red > 190 && pixel.Green < 80 && pixel.Blue < 80) { colors[0]++; isPhoto = true; }
            if (pixel.Green > 190 && pixel.Red < 80 && pixel.Blue < 80) { colors[1]++; isPhoto = true; }
            if (pixel.Red > 190 && pixel.Blue > 190 && pixel.Green < 80) { colors[2]++; isPhoto = true; }
            if (isPhoto && y >= pixels * 0.60 && y < pixels * 0.68) lowerPhotoPixels++;
        }
        Assert.All(colors.Take(count), pixels => Assert.True(pixels > 10));
        Assert.True(lowerPhotoPixels > pixels / 2, "Photos should fill the lower part of the folder pocket.");
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task CoverRequestsRunOnlyWhenEnabledAndCancelWhenNoLongerVisible()
    {
        var folder = Path.Combine(_root, "Album");
        Directory.CreateDirectory(folder);
        var entry = new MacExplorer.Models.FileSystemEntry
        {
            FullPath = folder, Name = "Album", IsDirectory = true
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var images = new FastFileListImages
        {
            FolderCoverProvider = async (_, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); }
                return null;
            }
        };
        try
        {
            images.UpdateVisible([entry], 64, folderCoversEnabled: false);
            Assert.Equal(0, images.PendingCount);
            images.UpdateVisible([entry], 64, folderCoversEnabled: true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            images.UpdateVisible([], 64, folderCoversEnabled: true);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, images.PendingCount);
        }
        finally { images.Clear(); }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task RapidViewportChangesDoNotQueuePerFolderCoverRequests()
    {
        var started = 0;
        var images = new FastFileListImages
        {
            FolderCoverProvider = (_, _, _) =>
            {
                Interlocked.Increment(ref started);
                return Task.FromResult<ThumbnailResult?>(null);
            }
        };
        try
        {
            for (var i = 0; i < 200; i++)
            {
                var entry = new MacExplorer.Models.FileSystemEntry
                {
                    FullPath = Path.Combine(_root, $"Album-{i}"), Name = $"Album-{i}", IsDirectory = true
                };
                images.UpdateVisible([entry], 64, folderCoversEnabled: true);
                Assert.Equal(0, images.PendingCount);
            }
            images.UpdateVisible([], 64, folderCoversEnabled: true);
            await Task.Delay(220);
            Assert.Equal(0, started);
        }
        finally { images.Clear(); }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public void ApplicationLinksVirtualAndUnreadableFoldersDoNotRequestCovers()
    {
        var entries = new[]
        {
            new MacExplorer.Models.FileSystemEntry { FullPath = Path.Combine(_root, "App.app"), Name = "App.app", IsDirectory = true },
            new MacExplorer.Models.FileSystemEntry { FullPath = Path.Combine(_root, "Link"), Name = "Link", IsDirectory = true, IsSymbolicLink = true },
            new MacExplorer.Models.FileSystemEntry { FullPath = Path.Combine(_root, "Virtual"), Name = "Virtual", IsDirectory = true, IsVirtual = true },
            new MacExplorer.Models.FileSystemEntry { FullPath = Path.Combine(_root, "Unreadable"), Name = "Unreadable", IsDirectory = true, IsReadable = false }
        };
        var requests = 0;
        var images = new FastFileListImages
        {
            FolderCoverProvider = (_, _, _) =>
            {
                requests++;
                return Task.FromResult<ThumbnailResult?>(null);
            }
        };
        try
        {
            images.UpdateVisible(entries, 64, folderCoversEnabled: true);
            Assert.Equal(0, images.PendingCount);
            Assert.Equal(0, requests);
        }
        finally { images.Clear(); }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public async Task RefreshInvalidatesCachedCoverWithoutDiscardingFolderIcon()
    {
        var folder = Path.Combine(_root, "Album");
        Directory.CreateDirectory(folder);
        var entry = new MacExplorer.Models.FileSystemEntry
        {
            FullPath = folder, Name = "Album", IsDirectory = true
        };
        using var sample = new SKBitmap(32, 32);
        sample.Erase(SKColors.Red);
        using var encoded = sample.Encode(SKEncodedImageFormat.Png, 100);
        var images = new FastFileListImages
        {
            FolderCoverProvider = (_, _, _) => Task.FromResult<ThumbnailResult?>(
                new ThumbnailResult(encoded.ToArray(), folder))
        };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iconLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        images.Changed += () =>
        {
            if (images.CachedBytes > 0) loaded.TrySetResult();
            if (images.Get(entry, false) != null) iconLoaded.TrySetResult();
        };
        try
        {
            images.UpdateVisible([entry], 64, folderCoversEnabled: true);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await iconLoaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var folderIcon = images.Get(entry, false);
            var cover = images.Get(entry, true);
            Assert.NotNull(cover);
            images.CancelFolderCoverRequests();
            images.UpdateVisible([entry], 64, folderCoversEnabled: false);
            Assert.Same(cover, images.Get(entry, true));
            images.UpdateVisible([entry], 64, folderCoversEnabled: true);
            Assert.Same(cover, images.Get(entry, true));
            images.InvalidateFolderCovers();
            Assert.Equal(0, images.CachedBytes);
            Assert.Same(folderIcon, images.Get(entry, false));
            Assert.Same(folderIcon, images.Get(entry, true));
        }
        finally { images.Clear(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class StubThumbnails : IThumbnailService
    {
        public List<string> Requested { get; } = [];
        public HashSet<string> BrokenNames { get; } = [];

        public Task<ThumbnailResult?> GetThumbnailResultAsync(string filePath, int maxPixelSize,
            CancellationToken ct = default)
        {
            Requested.Add(filePath);
            if (BrokenNames.Contains(Path.GetFileName(filePath)))
                return Task.FromResult<ThumbnailResult?>(new ThumbnailResult([1, 2, 3], filePath));
            using var bitmap = new SKBitmap(32, 32);
            bitmap.Erase(Path.GetFileName(filePath) switch
            {
                "a.png" => SKColors.Red,
                "b.png" => SKColors.Lime,
                _ => SKColors.Magenta
            });
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult<ThumbnailResult?>(new ThumbnailResult(encoded.ToArray(), filePath));
        }

        public async Task<byte[]?> GetThumbnailAsync(string filePath, int maxPixelSize, CancellationToken ct = default)
            => (await GetThumbnailResultAsync(filePath, maxPixelSize, ct))?.Bytes;
        public Task<byte[]?> GetFaceCropAsync(string filePath, float bx, float by, float bw, float bh,
            int maxPixelSize = 128, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public bool IsImageFile(string extension) => extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
        public void EvictFromCache(string filePath) { }
    }

}
