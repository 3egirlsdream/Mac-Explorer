using System.Diagnostics;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

public sealed class NativeImageThumbnailTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fkfinder-image-helper-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task ImageAndFaceModesResizeAndSelectVisionCoordinates()
    {
        var source = Path.Combine(_root, "中文 image.png");
        using (var image = new SKBitmap(800, 400))
        using (var canvas = new SKCanvas(image))
        using (var paint = new SKPaint { Color = SKColors.Red })
        {
            canvas.Clear(SKColors.Blue);
            canvas.DrawRect(new SKRect(0, 0, 400, 200), paint);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            using var output = File.Create(source); png.SaveTo(output);
        }
        var thumbnail = await RunAsync(source, "image");
        using (var image = SKBitmap.Decode(thumbnail))
        {
            Assert.Equal((128, 64), (image.Width, image.Height));
            Assert.True(image.GetPixel(16, 16).Red > 200);
            Assert.True(image.GetPixel(110, 50).Blue > 200);
        }
        var face = await RunAsync(source, "face", "0.15", "0.65", "0.2", "0.2");
        using var crop = SKBitmap.Decode(face);
        Assert.InRange(Math.Max(crop.Width, crop.Height), 126, 128);
        Assert.True(crop.GetPixel(crop.Width / 2, crop.Height / 2).Red > 200);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task ImageModeAppliesExifOrientation()
    {
        var source = Path.Combine(_root, "rotated.jpg");
        using var image = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(image); canvas.Clear(SKColors.Red);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        var bytes = jpeg.ToArray();
        // JPEG APP1: little-endian TIFF with one orientation=6 entry.
        byte[] exif = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0,
            1, 0, 0x12, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        await File.WriteAllBytesAsync(source, [..bytes[..2], ..exif, ..bytes[2..]], TestContext.Current.CancellationToken);
        using var rotated = SKBitmap.Decode(await RunAsync(source, "image"));
        Assert.Equal((64, 128), (rotated.Width, rotated.Height));
    }

    private async Task<string> RunAsync(string source, params string[] mode)
    {
        var output = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MacExplorer.Thumbnail"))
            { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { source, output, "128" }.Concat(mode)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        Assert.True(File.Exists(output));
        return output;
    }

    public void Dispose() => Directory.Delete(_root, true);
}
