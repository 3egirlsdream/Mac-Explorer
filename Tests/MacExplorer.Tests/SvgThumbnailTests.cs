using System.IO.Compression;
using System.Text;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Thumbnail")]
public sealed class SvgThumbnailTests
{
    [Fact]
    public async Task EmbeddedImagePatternRendersToPngAndSurvivesDiskCacheReload()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "svg-thumbnail-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var image = new SKBitmap(2, 2);
            image.Erase(SKColors.Red);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            // Same pattern/use/embedded PNG structure as the QQ color preview SVGs.
            var svg = $$"""
                <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="106" height="74" viewBox="0 0 106 74">
                  <g clip-path="url(#clip)"><rect width="106" height="74" fill="url(#pattern)"/></g>
                  <defs>
                    <pattern id="pattern" patternContentUnits="objectBoundingBox" width="1" height="1">
                      <use xlink:href="#image" transform="scale(0.5)"/>
                    </pattern>
                    <clipPath id="clip"><rect width="106" height="74" rx="10"/></clipPath>
                    <image id="image" width="2" height="2" xlink:href="data:image/png;base64,{{Convert.ToBase64String(encoded.ToArray())}}"/>
                  </defs>
                </svg>
                """;
            var path = Path.Combine(root, "中文 preview.SVG");
            await File.WriteAllTextAsync(path, svg, TestContext.Current.CancellationToken);
            var cache = Path.Combine(root, "cache");
            var service = new MacThumbnailService(cache, 1024 * 1024, 0.8);
            Assert.True(service.IsImageFile(".SVG"));
            Assert.True(MacThumbnailService.SupportsThumbnailExtension(".svg"));
            var result = await service.GetThumbnailResultAsync(path, 212, TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.Equal(result.Bytes, await File.ReadAllBytesAsync(result.CachePath, TestContext.Current.CancellationToken));
            using var bitmap = SKBitmap.Decode(result.Bytes);
            Assert.Equal((212, 148), (bitmap.Width, bitmap.Height));
            Assert.Equal(SKColors.Red, bitmap.GetPixel(106, 74));
            Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
            var reloaded = await new MacThumbnailService(cache, 1024 * 1024, 0.8)
                .GetThumbnailResultAsync(path, 212, TestContext.Current.CancellationToken);
            Assert.NotNull(reloaded);
            Assert.Equal(result.Bytes, reloaded.Bytes);
            Assert.Equal(result.CachePath, reloaded.CachePath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SvgzRendersThroughTheSharedThumbnailCacheAndRejectsOversizedInflation()
    {
        var root = Directory.CreateTempSubdirectory("svgz-thumbnail-").FullName;
        var token = TestContext.Current.CancellationToken;
        try
        {
            const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='40'><rect width='80' height='40' fill='blue'/></svg>";
            var file = Path.Combine(root, "preview.SVGZ");
            await WriteSvgzAsync(file, Encoding.UTF8.GetBytes(svg), token);
            var service = new MacThumbnailService(Path.Combine(root, "cache"), 1024 * 1024, 0.8);
            var result = await service.GetThumbnailResultAsync(file, 48, token);
            Assert.NotNull(result);
            using var bitmap = SKBitmap.Decode(result.Bytes);
            Assert.Equal((48, 24), (bitmap.Width, bitmap.Height));
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(24, 12));

            var oversized = Path.Combine(root, "oversized.svgz");
            await WriteSvgzAsync(oversized, Encoding.UTF8.GetBytes(svg + new string(' ', 8 * 1024 * 1024)), token);
            Assert.Null(await service.GetThumbnailResultAsync(oversized, 48, token));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task WriteSvgzAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await gzip.WriteAsync(bytes, token);
    }

    [Fact]
    public void SvgThumbnailsDoNotReadExternalImages()
    {
        var root = Directory.CreateTempSubdirectory("svg-external-").FullName;
        try
        {
            using var sourceImage = new SKBitmap(16, 16);
            sourceImage.Erase(SKColors.Red);
            using var encoded = sourceImage.Encode(SKEncodedImageFormat.Png, 100);
            var imagePath = Path.Combine(root, "external.png");
            File.WriteAllBytes(imagePath, encoded.ToArray());
            var source = Encoding.UTF8.GetBytes($"""
                <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16">
                    <rect width="16" height="16" fill="blue"/>
                    <image width="16" height="16" href="{new Uri(imagePath).AbsoluteUri}"/>
                </svg>
                """);
            var png = SvgThumbnailRenderer.RenderPng(source, 16, TestContext.Current.CancellationToken);
            Assert.NotNull(png);
            using var result = SKBitmap.Decode(png);
            Assert.Equal(SKColors.Blue, result.GetPixel(8, 8));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(32, 32, 16)]
    [InlineData(10000, 2048, 1024)]
    public void VectorOutputFitsRequestedSizeAndGlobalLimit(int requested, int width, int height)
    {
        var source = Encoding.UTF8.GetBytes("""
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
            <svg xmlns="http://www.w3.org/2000/svg" width="100000" height="50000" viewBox="10 20 100 50">
              <rect x="10" y="20" width="100" height="50" fill="blue"/>
            </svg>
            """);
        var bytes = SvgThumbnailRenderer.RenderPng(source, requested, TestContext.Current.CancellationToken);
        Assert.NotNull(bytes);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.Equal((width, height), (bitmap.Width, bitmap.Height));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(width / 2, height / 2));
    }

    [Theory]
    [InlineData("<svg")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='0' height='0'/>")]
    public void InvalidSvgDoesNotPublishABlankSuccess(string source)
        => Assert.Null(SvgThumbnailRenderer.RenderPng(Encoding.UTF8.GetBytes(source), 128,
            TestContext.Current.CancellationToken));

    [Fact]
    public void CancelledRequestDoesNotRender()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SvgThumbnailRenderer.RenderPng([], 128, cancellation.Token));
    }
}
