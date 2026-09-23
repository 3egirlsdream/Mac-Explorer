using System.Text;
using MacExplorer.Models;
using SkiaSharp;
using Svg.Skia;

namespace MacExplorer.Services.Impl;

/// <summary>Builds a folder-sized cover from the first three direct child photos.</summary>
internal sealed class FolderPhotoCoverService(IThumbnailService thumbnails)
{
    private static readonly SemaphoreSlim CoverGate = new(2);
    private static readonly Lazy<SKImage> FolderArtwork = new(LoadFolderArtwork);

    public async Task<ThumbnailResult?> CreateAsync(string folderPath, int pixels, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(folderPath)) return null;
        await CoverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = SelectPhotoPaths(folderPath, thumbnails.IsImageFile, cancellationToken);
            if (paths.Length == 0) return null;

            var photos = new List<SKBitmap>(paths.Length);
            try
            {
                foreach (var path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var result = await thumbnails.GetThumbnailResultAsync(path, pixels, cancellationToken).ConfigureAwait(false);
                        if (result?.Bytes is not { Length: > 0 } bytes) continue;
                        var bitmap = SKBitmap.Decode(bytes);
                        if (bitmap != null) photos.Add(bitmap);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        // A broken photo does not prevent the remaining cards from appearing.
                    }
                }
                if (photos.Count == 0) return null;
                cancellationToken.ThrowIfCancellationRequested();
                return new ThumbnailResult(Compose(photos, pixels), folderPath);
            }
            finally
            {
                foreach (var photo in photos) photo.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally { CoverGate.Release(); }
    }

    internal static string[] SelectPhotoPaths(string folderPath, Func<string, bool> isImageFile, CancellationToken token)
    {
        var first = new List<string>(3);
        foreach (var path in Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            if (!isImageFile(Path.GetExtension(path))) continue;
            var insert = first.FindIndex(existing => CompareNames(path, existing) < 0);
            if (insert < 0) insert = first.Count;
            if (insert >= 3) continue;
            first.Insert(insert, path);
            if (first.Count > 3) first.RemoveAt(3);
        }
        return first.ToArray();
    }

    private static int CompareNames(string left, string right)
    {
        var leftName = Path.GetFileName(left);
        var rightName = Path.GetFileName(right);
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(leftName, rightName);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(leftName, rightName);
    }

    private static byte[] Compose(IReadOnlyList<SKBitmap> photos, int pixels)
    {
        pixels = Math.Clamp(pixels, 32, 256);
        using var surface = SKSurface.Create(new SKImageInfo(pixels, pixels, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var bounds = new SKRect(0, 0, pixels, pixels);
        using var imagePaint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        canvas.DrawImage(FolderArtwork.Value, bounds, sampling, imagePaint);

        var scale = pixels / 56f;
        if (photos.Count == 1)
        {
            DrawCard(canvas, photos[0], 28, 29, 23, 35, 0, scale);
        }
        else if (photos.Count == 2)
        {
            DrawCard(canvas, photos[1], 36, 31, 19, 30, 6, scale);
            DrawCard(canvas, photos[0], 22, 29, 21, 35, -5, scale);
        }
        else
        {
            DrawCard(canvas, photos[1], 17, 31, 18, 30, -6, scale);
            DrawCard(canvas, photos[2], 39, 31, 18, 30, 6, scale);
            DrawCard(canvas, photos[0], 28, 29, 20, 35, 0, scale);
        }

        // The original artwork forms the front of the pocket and hides each card's lower edge.
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 39 * scale, pixels, pixels));
        canvas.DrawImage(FolderArtwork.Value, bounds, sampling, imagePaint);
        canvas.Restore();
        using (var seam = new SKPaint { IsAntialias = true, Color = new SKColor(18, 87, 125, 45), StrokeWidth = 0.7f * scale })
            canvas.DrawLine(3 * scale, 39 * scale, 53 * scale, 39 * scale, seam);
        using (var highlight = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 48), StrokeWidth = 0.5f * scale })
            canvas.DrawLine(3 * scale, 40 * scale, 53 * scale, 40 * scale, highlight);
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 90);
        return encoded.ToArray();
    }

    private static void DrawCard(SKCanvas canvas, SKBitmap photo, float centerX, float centerY,
        float width, float height, float angle, float scale)
    {
        canvas.Save();
        canvas.Translate(centerX * scale, centerY * scale);
        canvas.RotateDegrees(angle);
        var frame = new SKRect(-width * scale / 2, -height * scale / 2,
            width * scale / 2, height * scale / 2);
        var radius = 1.6f * scale;
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(26, 52, 75, 45) })
            canvas.DrawRoundRect(new SKRect(frame.Left, frame.Top + 0.6f * scale, frame.Right, frame.Bottom + 0.6f * scale), radius, radius, shadow);
        using (var border = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 220) })
            canvas.DrawRoundRect(frame, radius, radius, border);

        var inset = 0.85f * scale;
        var imageBounds = new SKRect(frame.Left + inset, frame.Top + inset, frame.Right - inset, frame.Bottom - inset);
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(imageBounds, radius - inset));
        var sourceSide = Math.Min(photo.Width, photo.Height * imageBounds.Width / imageBounds.Height);
        var sourceHeight = Math.Min(photo.Height, photo.Width * imageBounds.Height / imageBounds.Width);
        var source = new SKRect((photo.Width - sourceSide) / 2, (photo.Height - sourceHeight) / 2,
            (photo.Width + sourceSide) / 2, (photo.Height + sourceHeight) / 2);
        using (var image = SKImage.FromBitmap(photo))
        using (var paint = new SKPaint { IsAntialias = true })
            canvas.DrawImage(image, source, imageBounds,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Restore();
        canvas.Restore();
    }

    private static SKImage LoadFolderArtwork()
    {
        var nativePath = Path.Combine(AppContext.BaseDirectory, "MacExplorer.Folder.png");
        if (OperatingSystem.IsMacOS() && File.Exists(nativePath))
        {
            var native = SKImage.FromEncodedData(nativePath);
            if (native != null) return native;
        }

        using var svg = new SKSvg();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(FileIconRenderer.RenderFolder(256)));
        svg.Load(stream);
        using var surface = SKSurface.Create(new SKImageInfo(256, 256));
        surface.Canvas.Clear(SKColors.Transparent);
        if (svg.Picture != null) surface.Canvas.DrawPicture(svg.Picture);
        return surface.Snapshot();
    }
}
