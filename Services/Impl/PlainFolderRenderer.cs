using SkiaSharp;

namespace MacExplorer.Services.Impl;

// The empty folder uses a continuous frosted front rather than a photo pocket.
internal static class PlainFolderRenderer
{
    private static readonly SKRect Face = FrostedFolderDepth.Front;

    internal static byte[] RenderPng(int pixels, bool dark = false)
    {
        pixels = Math.Clamp(pixels, 16, 1024);
        using var surface = SKSurface.Create(new SKImageInfo(pixels, pixels));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(pixels / 320f);

        using var back = FrostedFolderDepth.CreateBackplate();
        Shadow(canvas, back, 1, 2.5f, new SKColor(57, 86, 117, dark ? (byte)24 : (byte)12));
        FrostedFolderDepth.DrawBackplate(canvas, back, Face, FrostedFolderDepth.TabTop);

        using var front = new SKPath(); front.AddRoundRect(Face, FrostedFolderDepth.CornerRadius, FrostedFolderDepth.CornerRadius);
        Shadow(canvas, front, 2, 5, new SKColor(35, 48, 69, dark ? (byte)38 : (byte)17));
        Shadow(canvas, front, .2f, .8f, new SKColor(68, 103, 131, 9));
        FrostedFolderDepth.DrawOpeningShadow(canvas, back, Face);
        using var glass = Glass(pixels);
        canvas.DrawBitmap(glass, new SKRect(0, 0, 320, 320));
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static SKBitmap Glass(int pixels)
    {
        var scale = pixels / 320f;
        var data = new byte[pixels * pixels * 4];
        for (var py = 0; py < pixels; py++)
        for (var px = 0; px < pixels; px++)
        {
            var x = (px + .5f) / scale;
            var y = (py + .5f) / scale;
            var distance = -Distance(x, y);
            if (distance < -.5f / scale) continue;
            var u = (x - Face.Left) / Face.Width;
            var v = (y - Face.Top) / Face.Height;
            var gx = Distance(x + .05f, y) - Distance(x - .05f, y);
            var gy = Distance(x, y + .05f) - Distance(x, y - .05f);
            var length = MathF.Sqrt(gx * gx + gy * gy);
            var nx = length > .00001f ? gx / length : 0;
            var ny = length > .00001f ? gy / length : 0;
            var light = Math.Clamp(-nx * .6f - ny * .8f, -1, 1);

            var color = new Pixel(166, 205, 235).Mix(new Pixel(118, 164, 199), Smooth(0, 1, v) * .48f);
            color = color.Mix(new Pixel(216, 236, 249), G(u - .20f, .65f) * G(v - .15f, .72f) * .38f);
            // Broad defocused reflections reveal the bowed glass without stripes.
            var reflection = G(u - (.28f + .28f * v), .20f) * G(v - .60f, .8f) * .34f;
            reflection += G(u - .76f, .30f) * G(v - .76f, .31f) * .17f;
            reflection += G(u - .18f, .35f) * G(v - .91f, .27f) * .13f;
            color = color.Mix(new Pixel(237, 245, 250), reflection);
            var depth = G(v - .012f, .017f) * .035f + G(u - .99f, .025f) * .014f;
            color = color.Mix(new Pixel(101, 157, 195), depth);

            var edge = FrostedFolderEdge.Sample(distance, v, light);
            if (distance < 3)
            {
                color = color.Mix(new Pixel(224, 241, 251), edge.Highlight);
                color = color.Mix(new Pixel(116, 169, 205), edge.Shade);
                color = color.Mix(new Pixel(213, 233, 246), edge.Scatter);
            }
            var alpha = Math.Clamp(distance * scale + .5f, 0, 1);
            var offset = (py * pixels + px) * 4;
            data[offset] = (byte)Math.Clamp(color.B * alpha, 0, 255);
            data[offset + 1] = (byte)Math.Clamp(color.G * alpha, 0, 255);
            data[offset + 2] = (byte)Math.Clamp(color.R * alpha, 0, 255);
            data[offset + 3] = (byte)(alpha * 255);
        }
        var bitmap = new SKBitmap(new SKImageInfo(pixels, pixels, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length);
        return bitmap;
    }

    private readonly record struct Pixel(float R, float G, float B)
    {
        internal Pixel Mix(Pixel other, float amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return new(R + (other.R - R) * amount, G + (other.G - G) * amount, B + (other.B - B) * amount);
        }
    }

    private static float Distance(float x, float y)
    {
        var qx = MathF.Abs(x - Face.MidX) - (Face.Width / 2 - FrostedFolderDepth.CornerRadius);
        var qy = MathF.Abs(y - Face.MidY) - (Face.Height / 2 - FrostedFolderDepth.CornerRadius);
        return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0))
            + MathF.Min(MathF.Max(qx, qy), 0) - FrostedFolderDepth.CornerRadius;
    }

    private static float G(float x, float width) => MathF.Exp(-x * x / (width * width));
    private static float Smooth(float start, float end, float value)
    {
        var t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }
    private static void Shadow(SKCanvas c, SKPath path, float y, float sigma, SKColor color)
    {
        using var filter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
        using var paint = new SKPaint { IsAntialias = true, Color = color, MaskFilter = filter };
        c.Save(); c.Translate(0, y); c.DrawPath(path, paint); c.Restore();
    }
}
