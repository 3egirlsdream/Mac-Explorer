using Avalonia.Media.Imaging;
using SkiaSharp;

namespace MacExplorer.Services.Impl;

// One normalized cover feeds the sharp face and the curved, scattering pocket.
internal static class FrostedFolderRenderer
{
    internal const float DefaultFrost = .11f;
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly SKRect Front = FrostedFolderDepth.Front;

    public static Bitmap Render(SKBitmap cover, SKBitmap? rear, int pixels = 256,
        float frost = DefaultFrost, float sigma = 10, bool dark = false, CancellationToken token = default)
    {
        using var stream = new MemoryStream(RenderPng(cover, rear, pixels, frost, sigma, dark, token));
        return new Bitmap(stream);
    }

    public static byte[] RenderPng(SKBitmap cover, SKBitmap? rear, int pixels,
        float frost = DefaultFrost, float sigma = 10, bool dark = false, CancellationToken token = default)
    {
        pixels = Math.Clamp(pixels, 32, 1024);
        token.ThrowIfCancellationRequested();
        var textureWidth = Math.Max(1, (int)Math.Round(pixels * Front.Width / 320d));
        var textureHeight = Math.Max(1, (int)Math.Round(pixels * Front.Height / 320d));
        using var surface = SKSurface.Create(new SKImageInfo(pixels, pixels));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(pixels / 320f);

        using var back = FrostedFolderDepth.CreateBackplate();
        Shadow(c, back, 0, 1, 2.5f, new SKColor(57, 86, 117, dark ? (byte)24 : (byte)12));
        FrostedFolderDepth.DrawBackplate(c, back, Front, FrostedFolderDepth.TabTop);

        if (rear is not null)
        {
            c.Save();
            c.RotateDegrees(-2.4f, 160, 174);
            var sheetRect = new SKRect(22, 76, 294, 267);
            using var sheet = new SKPath(); sheet.AddRoundRect(sheetRect, 10, 10);
            Shadow(c, sheet, 0, .4f, 1.4f, new SKColor(18, 35, 56, 32));
            c.Save(); c.ClipPath(sheet, antialias: true);
            // The exposed strip shows image content rather than empty sky.
            DrawCover(c, rear, sheetRect, .38f);
            c.Restore();
            Stroke(c, sheet, new SKColor(218, 237, 249, 80), .35f);
            c.Restore();
        }

        using var front = new SKPath(); front.AddRoundRect(Front, FrostedFolderDepth.CornerRadius, FrostedFolderDepth.CornerRadius);
        Shadow(c, front, 0, 2, 5, new SKColor(35, 48, 69, dark ? (byte)38 : (byte)17));
        Shadow(c, front, 0, .2f, .8f, new SKColor(68, 103, 131, 9));
        FrostedFolderDepth.DrawOpeningShadow(c, back, Front);

        using var normalized = SKSurface.Create(new SKImageInfo(textureWidth, textureHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        normalized.Canvas.Clear(new SKColor(178, 209, 232));
        DrawCover(normalized.Canvas, cover, new SKRect(0, 0, textureWidth, textureHeight));
        using var clear = normalized.Snapshot();
        using var blurredSurface = SKSurface.Create(new SKImageInfo(textureWidth, textureHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var blur = SKImageFilter.CreateBlur(sigma * textureWidth / Front.Width, sigma * textureHeight / Front.Height, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { ImageFilter = blur })
            blurredSurface.Canvas.DrawImage(clear, 0, 0, Sampling, p);
        using var blurred = blurredSurface.Snapshot();
        using var glass = GlassBody(clear, blurred, frost, pixels, token);
        c.DrawBitmap(glass, new SKRect(0, 0, 320, 320));

        using var output = surface.Snapshot();
        using var encoded = output.Encode(SKEncodedImageFormat.Png, 100);
        token.ThrowIfCancellationRequested();
        return encoded.ToArray();
    }

    // The pocket and the rim share one optical surface. No perimeter strokes
    // are composited on top: coverage, refraction and lighting meet at one edge.
    private static unsafe SKBitmap GlassBody(SKImage clear, SKImage blurred, float frost, int pixels, CancellationToken token)
    {
        var scale = pixels / 320f;
        var textureWidth = clear.Width; var textureHeight = clear.Height;
        var info = new SKImageInfo(textureWidth, textureHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var sharpBitmap = new SKBitmap(info);
        using var softBitmap = new SKBitmap(info);
        if (!clear.ReadPixels(info, sharpBitmap.GetPixels(), sharpBitmap.RowBytes, 0, 0)
            || !blurred.ReadPixels(info, softBitmap.GetPixels(), softBitmap.RowBytes, 0, 0))
            throw new InvalidOperationException("Could not read the prepared folder cover.");
        var sharp = (byte*)sharpBitmap.GetPixels();
        var soft = (byte*)softBitmap.GetPixels();
        var sharpStride = sharpBitmap.RowBytes;
        var softStride = softBitmap.RowBytes;
        var bitmap = new SKBitmap(new SKImageInfo(pixels, pixels, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            bitmap.Erase(SKColors.Transparent);
            var output = (byte*)bitmap.GetPixels();
            var outputStride = bitmap.RowBytes;
            var lip = Front.Bottom - Front.Height * frost;
            var height = Front.Bottom - lip;
            var firstX = Math.Max(0, (int)MathF.Floor(Front.Left * scale) - 1);
            var lastX = Math.Min(pixels, (int)MathF.Ceiling(Front.Right * scale) + 1);
            var lastY = Math.Min(pixels, (int)MathF.Ceiling(Front.Bottom * scale) + 1);
            for (var py = Math.Max(0, (int)MathF.Floor(Front.Top * scale) - 1); py < lastY; py++)
            {
                if ((py & 15) == 0) token.ThrowIfCancellationRequested();
                for (var px = firstX; px < lastX; px++)
                {
                    var x = (px + .5f) / scale;
                    var y = (py + .5f) / scale;
                    var d = -Distance(x, y);
                    if (d < -.5f / scale) continue;
                    var gx = Distance(x + .05f, y) - Distance(x - .05f, y);
                    var gy = Distance(x, y + .05f) - Distance(x, y - .05f);
                    var length = MathF.Sqrt(gx * gx + gy * gy);
                    var nx = length > .00001f ? gx / length : 0;
                    var ny = length > .00001f ? gy / length : 0;
                    var u = (x - Front.Left) / Front.Width;
                    var seam = lip + .65f * MathF.Pow((u - .5f) * 2, 2);
                    var t = Math.Clamp((y - seam) / height, 0, 1);
                    var pocket = Smooth(seam - .4f, seam + .5f, y);
                    var light = Math.Clamp(-nx * .6f - ny * .8f, -1, 1);
                    var edge = FrostedFolderEdge.Sample(d, (y - Front.Top) / Front.Height, light);
                    var sx = x - nx * edge.Bend;
                    var sy = y - ny * edge.Bend;
                    var color = Sample(sharp, sharpStride, textureWidth, textureHeight, sx, sy);
                    if (pocket > 0)
                    {
                        // A shallow convex cross-section bends the image underneath.
                        // Its broad face stays translucent; scattering grows at depth.
                        var domeNormal = (t - .42f) * 1.22f;
                        var fx = sx + (u - .5f) * MathF.Sin(t * MathF.PI) * 5.5f;
                        var fy = sy - 5.5f - domeNormal * 7.2f;
                        var frosted = Sample(soft, softStride, textureWidth, textureHeight, fx, fy);
                        var luminance = frosted.R * .2126f + frosted.G * .7152f + frosted.B * .0722f;
                        frosted = frosted.Mix(new Pixel(luminance, luminance, luminance), .14f);
                        var absorption = .20f + .10f * t;
                        frosted = frosted.Mix(new Pixel(164, 204, 232), absorption);
                        var faceLight = .035f + .04f * MathF.Sin(t * MathF.PI);
                        frosted = frosted.Mix(new Pixel(224, 239, 249), faceLight);

                        // Broad environment reflections travel over the bowed surface,
                        // rather than filling the pocket with a constant white overlay.
                        var diagonal = u - (.49f + .16f * t - .025f * t * t);
                        var cloud = Gaussian(diagonal, .105f) * (.28f - .12f * t);
                        cloud += Gaussian(u - (.62f - .10f * t), .08f) * Gaussian(t - .28f, .48f) * .10f;
                        var shoulder = Gaussian(t - .18f, .26f) * .055f;
                        frosted = frosted.Mix(new Pixel(237, 241, 245), cloud + shoulder);
                        var lowerShade = Smooth(.65f, 1, t) * .008f;
                        frosted = frosted.Mix(new Pixel(81, 137, 178), lowerShade);
                        var lipLight = Gaussian(y - seam - .15f, .6f) * .09f;
                        frosted = frosted.Mix(new Pixel(214, 236, 248), lipLight);
                        color = color.Mix(frosted, pocket);
                    }

                    if (d < 3)
                    {
                        color = color.Mix(new Pixel(224, 241, 251), edge.Highlight);
                        color = color.Mix(new Pixel(116, 169, 205), edge.Shade);
                        color = color.Mix(new Pixel(213, 233, 246), edge.Scatter);
                    }
                    var alpha = Math.Clamp(d * scale + .5f, 0, 1);
                    var offset = py * outputStride + px * 4;
                    output[offset] = (byte)Math.Clamp(color.B * alpha, 0, 255);
                    output[offset + 1] = (byte)Math.Clamp(color.G * alpha, 0, 255);
                    output[offset + 2] = (byte)Math.Clamp(color.R * alpha, 0, 255);
                    output[offset + 3] = (byte)(alpha * 255);
                }
            }
            token.ThrowIfCancellationRequested();
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private readonly record struct Pixel(float R, float G, float B)
    {
        public Pixel Mix(Pixel other, float amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return new Pixel(R + (other.R - R) * amount, G + (other.G - G) * amount, B + (other.B - B) * amount);
        }
    }

    private static unsafe Pixel Sample(byte* pixels, int stride, int width, int height, float x, float y)
    {
        var px = Math.Clamp((x - Front.Left) * width / Front.Width, 0, width - 1);
        var py = Math.Clamp((y - Front.Top) * height / Front.Height, 0, height - 1);
        var x0 = (int)px; var y0 = (int)py;
        var x1 = Math.Min(x0 + 1, width - 1); var y1 = Math.Min(y0 + 1, height - 1);
        Pixel At(int cx, int cy)
        {
            var offset = cy * stride + cx * 4;
            return new Pixel(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }
        return At(x0, y0).Mix(At(x1, y0), px - x0)
            .Mix(At(x0, y1).Mix(At(x1, y1), px - x0), py - y0);
    }

    private static float Gaussian(float x, float width) => MathF.Exp(-(x * x) / (width * width));

    private static float Smooth(float start, float end, float value)
    {
        var t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float Distance(float x, float y)
    {
        var qx = MathF.Abs(x - Front.MidX) - (Front.Width / 2 - FrostedFolderDepth.CornerRadius);
        var qy = MathF.Abs(y - Front.MidY) - (Front.Height / 2 - FrostedFolderDepth.CornerRadius);
        return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0))
            + MathF.Min(MathF.Max(qx, qy), 0) - FrostedFolderDepth.CornerRadius;
    }

    private static void Stroke(SKCanvas c, SKPath path, SKColor color, float width)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = width };
        c.DrawPath(path, p);
    }

    private static void Shadow(SKCanvas c, SKPath path, float x, float y, float sigma, SKColor color)
    {
        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
        using var p = new SKPaint { IsAntialias = true, Color = color, MaskFilter = blur };
        c.Save(); c.Translate(x, y); c.DrawPath(path, p); c.Restore();
    }

    private static void DrawCover(SKCanvas c, SKBitmap bitmap, SKRect target, float verticalStart = 0)
    {
        using var image = SKImage.FromBitmap(bitmap);
        var availableHeight = image.Height * (1 - verticalStart);
        var scale = Math.Max(target.Width / image.Width, target.Height / availableHeight);
        var w = target.Width / scale; var h = target.Height / scale;
        c.DrawImage(image, SKRect.Create((image.Width - w) / 2,
            image.Height * verticalStart + (availableHeight - h) / 2, w, h), target, Sampling);
    }
}
