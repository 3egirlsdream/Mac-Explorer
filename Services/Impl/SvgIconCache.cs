using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using MacExplorer.Models;
using SkiaSharp;
using Svg.Skia;

namespace MacExplorer.Services.Impl;

/// <summary>
/// In-memory SVG-to-Bitmap cache for file/folder/AI icons.
/// Generates SVG strings via the existing FileIconRenderer (shared with the MAUI project),
/// renders them to Avalonia Bitmaps using Skia, and caches the results.
/// Zero disk I/O — everything happens in memory.
/// </summary>
public static class SvgIconCache
{
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();

    /// <summary>Clear all cached bitmaps (e.g. on theme change).</summary>
    public static void Clear() => Cache.Clear();

    /// <summary>Get or render a file icon for the given icon-key and extension.</summary>
    public static Bitmap GetFileIcon(string iconKey, string? extension, int size)
    {
        var ext = extension?.TrimStart('.').ToUpperInvariant() ?? "";
        var key = $"file:{iconKey}:{ext}:{size}";
        return Cache.GetOrAdd(key, _ => RenderFileIcon(iconKey, ext, size));
    }

    /// <summary>Get or render a folder icon at the given size.</summary>
    public static Bitmap GetFolderIcon(int size)
    {
        var key = $"folder:{size}";
        return Cache.GetOrAdd(key, _ => RenderFolderIcon(size));
    }

    /// <summary>Get or render an AI virtual-folder icon.</summary>
    public static Bitmap GetAiIcon(string iconKey, int size)
    {
        var key = $"ai:{iconKey}:{size}";
        return Cache.GetOrAdd(key, _ => RenderAiIcon(iconKey, size));
    }

    // ── internal render helpers ──

    private static Bitmap RenderFileIcon(string iconKey, string extension, int size)
    {
        var svg = FileIconRenderer.Render(iconKey, extension, size);
        return RenderSvgToBitmap(svg, size);
    }

    private static Bitmap RenderFolderIcon(int size)
    {
        var svg = FileIconRenderer.RenderFolder(size);
        return RenderSvgToBitmap(svg, size);
    }

    private static Bitmap RenderAiIcon(string iconKey, int size)
    {
        var svg = FileIconRenderer.RenderAiIcon(iconKey, size);
        return RenderSvgToBitmap(svg, size);
    }

    private static Bitmap RenderSvgToBitmap(string svg, int size)
    {
        // Svg.Skia does not implement dominant-baseline="central" and falls
        // back to baseline alignment.  When y was authored for central
        // alignment, baseline places the text slightly *above* the intended
        // position (characters grow upward from the baseline).  We compensate
        // by removing the attribute and using a small positive dy to gently
        // nudge the text back down.
        svg = Regex.Replace(svg, @"\s*dominant-baseline=""central""\s*", " ");
        svg = Regex.Replace(svg, @"<text\b", "<text dy=\"0.30em\"");

        using var svgImage = new SKSvg();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
        svgImage.Load(stream);

        if (svgImage.Picture is null)
            return CreateEmptyBitmap(size);

        var scale = size / svgImage.Picture.CullRect.Width;
        var width = (int)(svgImage.Picture.CullRect.Width * scale);
        var height = (int)(svgImage.Picture.CullRect.Height * scale);

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale, scale);
        canvas.DrawPicture(svgImage.Picture);
        canvas.Flush();

        using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        return new Bitmap(data.AsStream());
    }

    private static Bitmap CreateEmptyBitmap(int size)
    {
        using var bmp = new SKBitmap(size, size);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return new Bitmap(data.AsStream());
    }
}
