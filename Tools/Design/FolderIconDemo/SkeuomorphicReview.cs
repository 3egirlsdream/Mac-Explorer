using MacExplorer.Services.Impl;
using SkiaSharp;

namespace FolderIconDemo;

internal static class SkeuomorphicReview
{
    // Only photographic interiors are passed to the renderer. The full reference
    // crop is displayed separately so geometry and material can be compared 1:1.
    internal static SKBitmap MainPhoto(SKBitmap reference) => Crop(reference, new SKRectI(80, 446, 562, 769));
    internal static SKBitmap RearPhoto(SKBitmap reference) => Crop(reference, new SKRectI(660, 306, 880, 454));
    internal static SKBitmap SunsetPhoto(SKBitmap reference) => Crop(reference, new SKRectI(948, 654, 1168, 800));

    private static SKBitmap Crop(SKBitmap source, SKRectI bounds)
    {
        var result = new SKBitmap();
        if (source.ExtractSubset(result, bounds)) return result;
        result.Dispose();
        throw new InvalidDataException("The review expects the original 1536 × 1024 reference.");
    }

    internal static void Export(string referencePath, string output, string? defaultFolderPath = null, bool frosted = false)
    {
        Directory.CreateDirectory(output);
        using var reference = SKBitmap.Decode(referencePath);
        using var first = MainPhoto(reference);
        using var second = RearPhoto(reference);
        using var third = SunsetPhoto(reference);
        using var referenceCrop = Crop(reference, new SKRectI(0, 252, 640, 892));
        using (var png = referenceCrop.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(output, "reference-normalized.png"), png.ToArray());
        var artwork = frosted ? FrostedFolderRenderer.RenderPng(first, second, 640)
            : SkeuomorphicFolderRenderer.RenderPng(640, [first, second, third]);
        var plainArtwork = frosted ? PlainFolderRenderer.RenderPng(640) : SkeuomorphicFolderRenderer.RenderPng(640);
        File.WriteAllBytes(Path.Combine(output, "rendered-transparent.png"), artwork);
        using var image = SKImage.FromEncodedData(artwork);
        using var surface = SKSurface.Create(new SKImageInfo(1280, 640));
        surface.Canvas.Clear(new SKColor(248, 245, 242));
        surface.Canvas.DrawBitmap(referenceCrop, 0, 0);
        surface.Canvas.DrawImage(image, 640, 0);
        using var comparison = surface.Snapshot();
        using var encoded = comparison.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(output, "comparison.png"), encoded.ToArray());
        File.WriteAllBytes(Path.Combine(output, "plain-transparent.png"), plainArtwork);
        if (defaultFolderPath != null)
        {
            using var defaultFolder = SKImage.FromEncodedData(defaultFolderPath);
            using var plain = SKImage.FromEncodedData(plainArtwork);
            using var stage = SKSurface.Create(new SKImageInfo(1920, 640));
            stage.Canvas.Clear(new SKColor(248, 245, 242));
            stage.Canvas.DrawImage(defaultFolder, new SKRect(0, 0, 640, 640), new SKSamplingOptions(SKFilterMode.Linear));
            stage.Canvas.DrawImage(plain, 640, 0);
            stage.Canvas.DrawImage(image, 1280, 0);
            using var preview = stage.Snapshot();
            using var png = preview.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(output, "default-proportions.png"), png.ToArray());
        }
        Console.WriteLine("REVIEW " + Path.Combine(output, "comparison.png"));
    }
}
