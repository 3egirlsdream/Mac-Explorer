using System.IO.Compression;
using System.Xml;
using SkiaSharp;
using Svg;
using Svg.Skia;

namespace MacExplorer.Services;

internal static class SvgThumbnailRenderer
{
    private const int MaxCompressedSvgBytes = 8 * 1024 * 1024;

    internal static bool Supports(string extension) => extension is ".svg" or ".svgz";

    internal static async Task<byte[]?> RenderFileAsync(string path, bool compressed, int maxPixelSize, CancellationToken ct)
    {
        if (!compressed)
        {
            var bytes = await BoundedImageDecoder.ReadSourceAsync(path, ct).ConfigureAwait(false);
            return bytes == null ? null : RenderPng(bytes, maxPixelSize, ct);
        }

        try
        {
            await using var input = File.OpenRead(path);
            if (input.Length > MaxCompressedSvgBytes) return null;
            await using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var svg = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await gzip.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (svg.Length + read > MaxCompressedSvgBytes) return null;
                svg.Write(buffer, 0, read);
            }
            return RenderPng(svg.ToArray(), maxPixelSize, ct);
        }
        catch (InvalidDataException) { return null; }
    }

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
            svg.FromSvgDocument(SvgDocument.Open<ThumbnailSvgDocument>(reader));
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

    private sealed class ThumbnailSvgDocument : SvgDocument
    {
        public ThumbnailSvgDocument()
        {
            LoadOptions = new SvgDocumentLoadOptions
            {
                ProcessingMode = SvgProcessingMode.SecureStatic,
                ExternalResources = SvgExternalResourcePolicy.SameDocumentAndDataOnly
            };
        }
    }
}
