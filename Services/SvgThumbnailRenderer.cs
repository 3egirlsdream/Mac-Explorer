using System.Xml;
using SkiaSharp;
using Svg.Skia;

namespace MacExplorer.Services;

internal static class SvgThumbnailRenderer
{
    // SVG is rasterized only here; every preview consumer receives a bounded PNG.
    internal static byte[]? RenderPng(byte[] source, int maxPixelSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.Length == 0 || source.Length > BoundedImageDecoder.MaxEncodedBytes) return null;
        try
        {
            using var stream = new MemoryStream(source);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = BoundedImageDecoder.MaxEncodedBytes
            });
            using var svg = new SKSvg();
            svg.Settings.EnableJavaScript = false;
            svg.Settings.EnableExternalJavaScript = false;
            svg.Load(reader);
            ct.ThrowIfCancellationRequested();
            if (svg.SourceDocument is not { } document || document.Width.Value <= 0 || document.Height.Value <= 0)
                return null;
            if (svg.Picture is not { } picture) return null;
            var bounds = picture.CullRect;
            if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height)
                || bounds.Width <= 0 || bounds.Height <= 0) return null;

            var edge = Math.Clamp(maxPixelSize, 1, BoundedImageDecoder.MaxOutputEdge);
            var scale = edge / Math.Max(bounds.Width, bounds.Height);
            var width = Math.Clamp((int)Math.Ceiling(bounds.Width * scale), 1, edge);
            var height = Math.Clamp((int)Math.Ceiling(bounds.Height * scale), 1, edge);
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(scale);
            surface.Canvas.Translate(-bounds.Left, -bounds.Top);
            surface.Canvas.DrawPicture(picture);
            ct.ThrowIfCancellationRequested();
            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }
}
