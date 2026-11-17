using MacExplorer.Models;
using SkiaSharp;

namespace MacExplorer.Services.Impl;

internal static class FolderArtwork
{
    internal static readonly int[] IconSizes = [16, 32, 48, 64, 96, 128, 160, 192, 224, 256, 512];

    internal static Stream Open(string name)
        // Plain assembly resources also work before Avalonia installs its URI parser and asset services.
        => typeof(FolderArtwork).Assembly.GetManifestResourceStream("MacExplorer.FolderArtwork." + name)
            ?? throw new FileNotFoundException("Embedded folder artwork was not found.", name);

    internal static byte[]? ReadIcon(int pixels, FolderPhotoCoverStyle style, Func<string, Stream>? open = null)
    {
        if (style == FolderPhotoCoverStyle.Default) return null;
        pixels = Math.Clamp(pixels, 16, 1024);
        var sourceSize = IconSizes.FirstOrDefault(size => size >= pixels, IconSizes[^1]);
        return ReadPng($"{style.ToString().ToLowerInvariant()}-{sourceSize}.png", pixels, open);
    }

    internal static byte[]? ReadPreview(FolderPhotoCoverStyle style)
        => ReadPng($"{style.ToString().ToLowerInvariant()}-preview.png", 256);

    private static byte[]? ReadPng(string name, int pixels, Func<string, Stream>? open = null)
    {
        try
        {
            using var input = (open ?? Open)(name);
            using var stream = new MemoryStream(); input.CopyTo(stream);
            var bytes = stream.ToArray();
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null) return null;
            if (bitmap.Width == pixels && bitmap.Height == pixels) return bytes;
            using var resized = bitmap.Resize(new SKImageInfo(pixels, pixels), new SKSamplingOptions(SKFilterMode.Linear));
            using var encoded = resized?.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        { return null; }
    }
}
