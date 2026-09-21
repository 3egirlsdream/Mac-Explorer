using System.IO.Compression;
using System.Text.Json;
using MacExplorer.PluginSdk;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

public sealed class WebpFileConversionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "macexplorer-webp-test-" + Guid.NewGuid().ToString("N"));

    // Self-contained 32x16 fixtures, generated with libwebp. Static images have opaque red,
    // half-transparent green and transparent vertical bands; animation is red, then blue.
    private const string LosslessWebp = "UklGRjAAAABXRUJQVlA4TCQAAAAvH8ADEBcw/wKCIv9HCwFB0XXLBfwCmbRNTNbkvO6P6H/UBr8=";
    private const string LossyWebp = "UklGRnoAAABXRUJQVlA4WAoAAAAQAAAAHwAADwAAQUxQSBMAAAABIJq26en7fKpMz0XEBFSjzP8CAFZQOCBAAAAAkAIAnQEqIAAQAADAEiWgAnTKEYA/AD9QP6ABDHgA/u2n//92iF/7vJHLrg/9y+f/1oxkCWn/8oQ//WLXCsAAAA==";
    private const string AnimatedWebp = "UklGRoQAAABXRUJQVlA4WAoAAAACAAAAHwAADwAAQU5JTQYAAAAAAAAAAABBTk1GKAAAAAAAAAAAAB8AAA8AAGQAAAJWUDhMDwAAAC8fwAMABxD9j/4HIqL/AQBBTk1GKAAAAAAAAAAAAB8AAA8AAGQAAABWUDhMDwAAAC8fwAMABxDR//4HIqL/AQA=";

    public WebpFileConversionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string WriteFixture(string name, string base64)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return path;
    }

    private static PluginManifest SourceManifest()
        => JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(
            PluginTestEnvironment.Repository, "Plugins", "FileConversion", "plugin.json")), PluginProtocol.Json)!;

    private static FileConversionService NativeService()
    {
        var helper = Path.Combine(PluginTestEnvironment.Repository, "Plugins", "FileConversion", "bin",
            PluginTestEnvironment.Configuration, "net10.0", "osx-arm64", "MacExplorer.FileConversion");
        Assert.True(File.Exists(helper), "Build the application before native conversion tests.");
        return new FileConversionService(helper, TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("image.webp")]
    [InlineData("image.WEBP")]
    [InlineData("image.WeBp")]
    [InlineData("image.svg")]
    [InlineData("image.ico")]
    [InlineData("image.icns")]
    public void ImageCommandsAndServiceAgreeOnSupportedSources(string name)
    {
        var path = Path.Combine(_root, name);
        Assert.Equal(new[] { FileConversionFormat.Png, FileConversionFormat.Jpg },
            new FileConversionService().GetAvailableFormats(path));
        Assert.Equal(new[] { "to-png", "to-jpg" }, SourceManifest().Commands
            .Where(command => command.Match.Matches([new PluginFile(path)]))
            .Select(command => command.Id));
    }

    [Fact]
    public void WebpDoesNotEnableDirectoriesRemotePathsOrBatchImageConversion()
    {
        var directory = Path.Combine(_root, "folder.webp");
        Directory.CreateDirectory(directory);
        var service = new FileConversionService();
        Assert.Empty(service.GetAvailableFormats(directory));
        Assert.Empty(service.GetAvailableFormats("sftp://server/image.webp"));
        Assert.Empty(service.GetAvailableFormats("relative.webp"));
        var path = Path.Combine(_root, "image.webp");
        PluginFile[][] selections =
        [
            [],
            [new PluginFile(directory, IsDirectory: true)],
            [new PluginFile("sftp://server/image.webp", "sftp")],
            [new PluginFile(path, "sftp")],
            [new PluginFile("relative.webp")],
            [new PluginFile(path), new PluginFile(Path.Combine(_root, "other.webp"))]
        ];
        foreach (var files in selections)
            Assert.DoesNotContain(SourceManifest().Commands, command => command.Match.Matches(files));
    }

    [Fact]
    public void BundledPluginMatchesSourceVersionAndAdvertisesWebp()
    {
        using var package = ZipFile.OpenRead(PluginTestEnvironment.BundledPackage);
        using var stream = package.GetEntry("plugin.json")!.Open();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(stream, PluginProtocol.Json)!;
        Assert.Equal(SourceManifest().Version, manifest.Version);
        Assert.Equal(new[] { "to-png", "to-jpg" }, manifest.Commands
            .Where(command => command.Match.Matches([new PluginFile(Path.Combine(_root, "image.WEBP"))]))
            .Select(command => command.Id));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task NativeWebpPreservesSizeTransparencyAndWhiteJpegBackground(bool lossless, bool resize)
    {
        var source = WriteFixture("中文 图片.WEBP", lossless ? LosslessWebp : LossyWebp);
        var original = File.ReadAllBytes(source);
        var service = NativeService();
        Assert.Equal(new ConversionImageSize(32, 16), await service.GetImageSizeAsync(source));
        ConversionImageSize? requested = resize ? new(64, 32) : null;
        var expected = requested ?? new ConversionImageSize(32, 16);
        foreach (var format in new[] { FileConversionFormat.Png, FileConversionFormat.Jpg })
        {
            var outputDirectory = Path.Combine(_root, format.ToString());
            var result = await service.ConvertAsync(new(source, format, requested), outputDirectory);
            Assert.Empty(result.Warnings);
            using var codec = SKCodec.Create(result.OutputPath);
            Assert.NotNull(codec);
            Assert.Equal(format == FileConversionFormat.Png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg,
                codec.EncodedFormat);
            using var image = SKBitmap.Decode(result.OutputPath);
            Assert.NotNull(image);
            Assert.Equal(expected.Width, image.Width);
            Assert.Equal(expected.Height, image.Height);
            var background = image.GetPixel(image.Width - 2, image.Height / 2);
            if (format == FileConversionFormat.Png)
            {
                Assert.Equal(0, background.Alpha);
                Assert.InRange((int)image.GetPixel(image.Width * 3 / 8, image.Height / 2).Alpha, 126, 130);
            }
            else
            {
                Assert.Equal(255, background.Alpha);
                Assert.True(background.Red > 245 && background.Green > 245 && background.Blue > 245);
            }
            Assert.Empty(Directory.GetDirectories(outputDirectory, ".conversion-*"));
        }
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(FileConversionFormat.Png)]
    [InlineData(FileConversionFormat.Jpg)]
    public async Task NativeAnimatedWebpExportsFirstFrameAndReportsAnimationLoss(FileConversionFormat format)
    {
        var source = WriteFixture("animated.webp", AnimatedWebp);
        var original = File.ReadAllBytes(source);
        var service = NativeService();
        Assert.Equal(new ConversionImageSize(32, 16), await service.GetImageSizeAsync(source));
        var result = await service.ConvertAsync(new(source, format), Path.Combine(_root, "output"));
        Assert.Contains("第一帧", Assert.Single(result.Warnings));
        using var image = SKBitmap.Decode(result.OutputPath);
        Assert.NotNull(image);
        Assert.Equal(32, image.Width);
        Assert.Equal(16, image.Height);
        var color = image.GetPixel(16, 8);
        Assert.True(color.Red > 220 && color.Green < 40 && color.Blue < 40,
            "The first frame is red; the second frame must not replace it.");
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task InvalidWebpFailsWithoutLeavingOutputOrChangingSource()
    {
        var source = Path.Combine(_root, "broken.webp");
        await File.WriteAllTextAsync(source, "not a WebP image");
        var original = File.ReadAllBytes(source);
        var service = NativeService();
        await Assert.ThrowsAsync<IOException>(() => service.GetImageSizeAsync(source));
        var outputDirectory = Path.Combine(_root, "output");
        // Supplying a size exercises the conversion decoder instead of only image-info.
        await Assert.ThrowsAsync<IOException>(() => service.ConvertAsync(
            new(source, FileConversionFormat.Png, new(32, 16)), outputDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(outputDirectory));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task CancelledWebpConversionDoesNotCommitOutput()
    {
        var source = WriteFixture("cancel.webp", LosslessWebp);
        var original = File.ReadAllBytes(source);
        var service = NativeService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var outputDirectory = Path.Combine(_root, "output");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConvertAsync(
            new(source, FileConversionFormat.Png, new(32, 16)), outputDirectory, cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(outputDirectory));
        Assert.Equal(original, File.ReadAllBytes(source));
    }
}
