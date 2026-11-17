using SkiaSharp;

namespace MacExplorer.Services.Impl;

internal static class SkeuomorphicFolderRenderer
{
    // Match the default macOS folder's landscape silhouette. Photo cards have
    // independent bounds and use uniform source cropping, never a squashed canvas.
    private static readonly SKRect BackBounds = new(12, 44, 308, 276.5f);
    private static readonly SKRect MainPhotoFrame = new(20, 80, 300, 246.5f);
    private const float BottomRadius = 16;

    internal static byte[] RenderPng(int pixels, IReadOnlyList<SKBitmap>? photos = null,
        CancellationToken token = default, bool cacheMaterials = true)
    {
        token.ThrowIfCancellationRequested();
        pixels = Math.Clamp(pixels, 16, 1024);
        using var surface = SKSurface.Create(new SKImageInfo(pixels, pixels));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(pixels / 320f);
        canvas.Translate(.5f, 0);

        var withPhotos = photos is { Count: > 0 };
        using var grainLease = cacheMaterials ? FolderMaterialCache.Shared.Acquire("paper-grain", LoadPaperGrain) : null;
        using var generatedGrain = cacheMaterials ? null : CreatePaperGrain();
        var grain = grainLease?.Image ?? generatedGrain!;
        if (cacheMaterials)
        {
            using var background = FolderMaterialCache.Shared.Acquire($"paper-back:{pixels}", () =>
            {
                using var layer = SKSurface.Create(new SKImageInfo(pixels, pixels));
                layer.Canvas.Clear(SKColors.Transparent);
                layer.Canvas.Scale(pixels / 320f); layer.Canvas.Translate(.5f, 0);
                DrawBackplate(layer.Canvas, grain);
                return layer.Snapshot();
            });
            canvas.Save(); canvas.ResetMatrix(); canvas.DrawImage(background.Image, 0, 0); canvas.Restore();
        }
        else DrawBackplate(canvas, grain);

        if (photos is { Count: > 0 })
        {
            token.ThrowIfCancellationRequested();
            if (photos.Count > 1) Photo(canvas, grain, photos[1], new SKRect(43, 63, 173, 158), -5.5f, 4.5f);
            if (photos.Count > 2) Photo(canvas, grain, photos[2], new SKRect(148, 65, 293, 168), -5.5f, 4.5f);
            Photo(canvas, grain, photos[0], MainPhotoFrame, 0, 7);
        }

        var face = new SKRect(12, withPhotos ? 240 : 80, 308, 276.5f);
        var topRadius = withPhotos ? 7 : 16;
        using var pocket = PocketPath(face, topRadius);
        Shadow(canvas, pocket, -1, 1.5f, new SKColor(64, 52, 38, 36));
        Shadow(canvas, pocket, 2, 2.8f, new SKColor(60, 49, 35, 38));
        canvas.Save(); canvas.ClipPath(pocket, antialias: true);
        if (cacheMaterials)
        {
            using var stock = FolderMaterialCache.Shared.Acquire($"paper-stock:{pixels}:{withPhotos}", () =>
            {
                using var bitmap = PocketStock(pixels, face, topRadius, token);
                return SKImage.FromBitmap(bitmap);
            });
            canvas.DrawImage(stock.Image, face);
        }
        else
        {
            using var stock = PocketStock(pixels, face, topRadius, token);
            canvas.DrawBitmap(stock, face);
        }
        Grain(canvas, grain, face, 72);
        canvas.Restore();

        token.ThrowIfCancellationRequested();
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        token.ThrowIfCancellationRequested();
        return png.ToArray();
    }

    private static SKImage LoadPaperGrain()
    {
        using var input = FolderArtwork.Open("paper-grain.png");
        using var bitmap = SKBitmap.Decode(input) ?? throw new InvalidDataException("Invalid embedded paper grain.");
        return SKImage.FromBitmap(bitmap);
    }

    private static void DrawBackplate(SKCanvas canvas, SKImage grain)
    {
        using var back = new SKPath();
        back.MoveTo(28, 44); back.LineTo(98, 44);
        back.CubicTo(108, 44, 112, 49, 118, 58);
        back.LineTo(122, 63); back.CubicTo(125, 67, 128, 67, 132, 67);
        back.LineTo(292, 67); back.CubicTo(302, 67, 308, 76, 308, 87);
        back.LineTo(308, 260.5f); back.CubicTo(308, 270, 302, 276.5f, 292, 276.5f);
        back.LineTo(28, 276.5f); back.CubicTo(18, 276.5f, 12, 270, 12, 260.5f);
        back.LineTo(12, 61); back.CubicTo(12, 51, 18, 44, 28, 44); back.Close();
        Shadow(canvas, back, 4, 4.5f, new SKColor(58, 48, 36, 26));
        Paper(canvas, grain, back, BackBounds, new SKColor(173, 173, 154), new SKColor(148, 147, 130), new SKColor(160, 160, 141));

        using var lining = new SKPath();
        lining.MoveTo(132, 67); lining.LineTo(292, 67);
        lining.CubicTo(302, 67, 308, 76, 308, 87);
        lining.LineTo(308, 260.5f); lining.CubicTo(308, 270, 302, 276.5f, 292, 276.5f);
        lining.LineTo(126, 276.5f); lining.LineTo(126, 88);
        lining.CubicTo(126, 76, 128, 70, 132, 67); lining.Close();
        Paper(canvas, grain, lining, new SKRect(126, 67, 308, 276.5f), new SKColor(221, 213, 201), new SKColor(193, 184, 171), new SKColor(204, 194, 181));
    }

    private static SKPath PocketPath(SKRect bounds, float topRadius)
    {
        const float k = .5522848f;
        var path = new SKPath();
        path.MoveTo(bounds.Left + topRadius, bounds.Top); path.LineTo(bounds.Right - topRadius, bounds.Top);
        path.CubicTo(bounds.Right - topRadius * (1 - k), bounds.Top, bounds.Right, bounds.Top + topRadius * (1 - k), bounds.Right, bounds.Top + topRadius);
        path.LineTo(bounds.Right, bounds.Bottom - BottomRadius);
        path.CubicTo(bounds.Right, bounds.Bottom - BottomRadius * (1 - k), bounds.Right - BottomRadius * (1 - k), bounds.Bottom, bounds.Right - BottomRadius, bounds.Bottom);
        path.LineTo(bounds.Left + BottomRadius, bounds.Bottom);
        path.CubicTo(bounds.Left + BottomRadius * (1 - k), bounds.Bottom, bounds.Left, bounds.Bottom - BottomRadius * (1 - k), bounds.Left, bounds.Bottom - BottomRadius);
        path.LineTo(bounds.Left, bounds.Top + topRadius);
        path.CubicTo(bounds.Left, bounds.Top + topRadius * (1 - k), bounds.Left + topRadius * (1 - k), bounds.Top, bounds.Left + topRadius, bounds.Top);
        path.Close();
        return path;
    }

    private static unsafe SKBitmap PocketStock(int pixels, SKRect bounds, float topRadius, CancellationToken token)
    {
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * pixels / 320));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * pixels / 320));
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            var data = (byte*)bitmap.GetPixels();
            var stride = bitmap.RowBytes;
            for (var py = 0; py < height; py++)
            {
                token.ThrowIfCancellationRequested();
                var y = bounds.Top + (py + .5f) * bounds.Height / height;
                var v = (y - bounds.Top) / bounds.Height;
                var t = Math.Clamp(v / .6f, 0, 1);
                var lower = Math.Clamp((v - .6f) / .4f, 0, 1);
                for (var px = 0; px < width; px++)
                {
                    var x = bounds.Left + (px + .5f) * bounds.Width / width;
                    var u = (x - bounds.Left) / bounds.Width;
                    var red = 209.5f - 8 * t - 13 * lower;
                    var green = 198.5f - 8 * t - 13 * lower;
                    var blue = 184.5f - 8 * t - 13 * lower;
                    var distance = -PocketDistance(x, y, bounds, topRadius);
                    var light = 0f;
                    if (distance < 4)
                    {
                        var nx = PocketDistance(x + .1f, y, bounds, topRadius) - PocketDistance(x - .1f, y, bounds, topRadius);
                        var ny = PocketDistance(x, y + .1f, bounds, topRadius) - PocketDistance(x, y - .1f, bounds, topRadius);
                        var length = MathF.Sqrt(nx * nx + ny * ny);
                        var facing = length > .00001f ? (-nx * .42f - ny * .91f) / length : 0;
                        var roll = MathF.Exp(-MathF.Pow((distance - .5f) / (facing > 0 ? .5f : .8f), 2));
                        light = roll * facing * (facing > 0 ? 32 : 6);
                        if (ny > 0 && length > .00001f)
                            light -= 39 * ny / length * MathF.Exp(-Math.Max(0, distance) / 1.35f);
                    }
                    light += MathF.Exp(-(y - bounds.Top) / 3) * 3;
                    light += MathF.Sin(u * MathF.PI) * MathF.Sin(v * MathF.PI) * 1.2f;
                    var offset = py * stride + px * 4;
                    data[offset] = (byte)Math.Clamp(blue + light, 0, 255);
                    data[offset + 1] = (byte)Math.Clamp(green + light, 0, 255);
                    data[offset + 2] = (byte)Math.Clamp(red + light, 0, 255);
                    data[offset + 3] = 255;
                }
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static float PocketDistance(float x, float y, SKRect bounds, float topRadius)
    {
        var radius = y < bounds.Top + topRadius ? topRadius : BottomRadius;
        var cornerY = y < bounds.Top + topRadius ? bounds.Top + radius : bounds.Bottom - radius;
        if (y < bounds.Top + topRadius || y > bounds.Bottom - BottomRadius)
        {
            if (x < bounds.Left + radius)
                return MathF.Sqrt(MathF.Pow(x - bounds.Left - radius, 2) + MathF.Pow(y - cornerY, 2)) - radius;
            if (x > bounds.Right - radius)
                return MathF.Sqrt(MathF.Pow(x - bounds.Right + radius, 2) + MathF.Pow(y - cornerY, 2)) - radius;
        }
        return Math.Max(Math.Max(bounds.Left - x, x - bounds.Right), Math.Max(bounds.Top - y, y - bounds.Bottom));
    }

    private static void Paper(SKCanvas canvas, SKImage grain, SKPath shape, SKRect bounds, SKColor top, SKColor bottom, SKColor middle)
    {
        using var gradient = SKShader.CreateLinearGradient(new SKPoint(bounds.MidX, bounds.Top),
            new SKPoint(bounds.MidX, bounds.Bottom), [top, middle, bottom], [0, .15f, 1], SKShaderTileMode.Clamp);
        using var fill = new SKPaint { IsAntialias = true, Shader = gradient };
        canvas.DrawPath(shape, fill);
        canvas.Save(); canvas.ClipPath(shape, antialias: true);
        Grain(canvas, grain, bounds, 65);
        using var edgeGradient = SKShader.CreateLinearGradient(new SKPoint(bounds.Left, bounds.Top),
            new SKPoint(bounds.Right, bounds.Bottom), [new SKColor(255, 250, 235, 145), new SKColor(108, 96, 78, 22)], null, SKShaderTileMode.Clamp);
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Shader = edgeGradient };
        canvas.DrawPath(shape, rim);
        canvas.Restore();
    }

    private static void Grain(SKCanvas canvas, SKImage grain, SKRect bounds, byte opacity)
    {
        using var texture = grain.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), SKMatrix.CreateScale(.5f, .5f));
        using var paint = new SKPaint { Shader = texture, Color = new SKColor(255, 255, 255, opacity), BlendMode = SKBlendMode.SoftLight };
        canvas.DrawRect(bounds, paint);
    }

    private static void Photo(SKCanvas canvas, SKImage grain, SKBitmap photo, SKRect frame, float angle, float inset)
    {
        canvas.Save(); canvas.RotateDegrees(angle, frame.MidX, frame.MidY);
        using var stock = new SKPath(); stock.AddRoundRect(frame, angle == 0 ? 3 : .6f, angle == 0 ? 3 : .6f);
        Shadow(canvas, stock, 1.4f, 2, new SKColor(54, 44, 29, 34));
        Shadow(canvas, stock, .5f, .55f, new SKColor(69, 55, 38, 54));
        SKColor[] paperColors = angle == 0 ? [new SKColor(239, 231, 221), new SKColor(235, 225, 215)]
            : [new SKColor(248, 244, 234), new SKColor(235, 228, 215)];
        using var paperGradient = SKShader.CreateLinearGradient(new SKPoint(frame.Left, frame.Top),
            new SKPoint(frame.Right, frame.Bottom), paperColors, null, SKShaderTileMode.Clamp);
        using (var paper = new SKPaint { IsAntialias = true, Shader = paperGradient }) canvas.DrawPath(stock, paper);
        canvas.Save(); canvas.ClipPath(stock, antialias: true);
        Grain(canvas, grain, frame, 20);
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f,
                   Color = new SKColor(255, 252, 243, 210) }) canvas.DrawPath(stock, edge);
        canvas.Restore();
        var photoOffset = angle == 0 ? .5f : 0;
        var imageBounds = new SKRect(frame.Left + inset + photoOffset, frame.Top + inset,
            frame.Right - inset + photoOffset, frame.Bottom - inset);
        var cropWidth = Math.Min(photo.Width, photo.Height * imageBounds.Width / imageBounds.Height);
        var cropHeight = Math.Min(photo.Height, photo.Width * imageBounds.Height / imageBounds.Width);
        var source = new SKRect((photo.Width - cropWidth) / 2, (photo.Height - cropHeight) / 2,
            (photo.Width + cropWidth) / 2, (photo.Height + cropHeight) / 2);
        using var image = SKImage.FromBitmap(photo);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, source, imageBounds, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Restore();
    }

    private static SKImage CreatePaperGrain()
    {
        const int size = 512;
        var random = new Random(7123);
        var coarse = new float[33 * 33];
        for (var i = 0; i < coarse.Length; i++) coarse[i] = (float)(random.NextDouble() * 2 - 1);
        using var bitmap = new SKBitmap(size, size);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var cx = x / 16; var cy = y / 16;
            var tx = x % 16 / 16f; var ty = y % 16 / 16f;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            var top = coarse[cy * 33 + cx] * (1 - tx) + coarse[cy * 33 + cx + 1] * tx;
            var bottom = coarse[(cy + 1) * 33 + cx] * (1 - tx) + coarse[(cy + 1) * 33 + cx + 1] * tx;
            var fine = random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5;
            var value = (byte)Math.Clamp(128 + fine * 63 + (top * (1 - ty) + bottom * ty) * 18, 0, 255);
            bitmap.SetPixel(x, y, new SKColor(value, value, value));
        }
        using (var canvas = new SKCanvas(bitmap))
        using (var fiber = new SKPaint { IsAntialias = true, StrokeWidth = .65f, StrokeCap = SKStrokeCap.Round })
        {
            for (var i = 0; i < 6500; i++)
            {
                var x = (float)random.NextDouble() * size; var y = (float)random.NextDouble() * size;
                var angle = (float)random.NextDouble() * MathF.PI * 2;
                var length = 1 + (float)random.NextDouble() * 3;
                fiber.Color = i % 2 == 0 ? new SKColor(255, 255, 255, 44) : new SKColor(0, 0, 0, 36);
                canvas.DrawLine(x, y, x + MathF.Cos(angle) * length, y + MathF.Sin(angle) * length, fiber);
            }
        }
        return SKImage.FromBitmap(bitmap);
    }

    private static void Shadow(SKCanvas canvas, SKPath path, float offset, float blur, SKColor color)
    {
        using var filter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);
        using var paint = new SKPaint { IsAntialias = true, Color = color, MaskFilter = filter };
        canvas.Save(); canvas.Translate(0, offset); canvas.DrawPath(path, paint); canvas.Restore();
    }
}
