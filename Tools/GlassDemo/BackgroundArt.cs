using SkiaSharp;
using MacExplorer.Controls;

namespace GlassDemo;

// One rendered source for both Avalonia and AppKit so the comparison changes only the material.
internal static class BackgroundArt
{
    public static byte[] Create(int mode, bool dark, int width, int height)
    {
        if (mode == 7 || (mode == 5 && !dark))
            return SidebarAppearance.CreateBackdrop(dark, width, height);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse(dark ? "#1E2229" : "#F7F8FA"));
        canvas.Save();
        canvas.Scale(width / 520f, height / 440f);
        switch (mode)
        {
            case 4: DrawArc(canvas, dark); break;
            case 5: DrawGlow(canvas, dark); break;
            case 6: DrawFineLines(canvas, dark); break;
        }
        canvas.Restore();
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void DrawArc(SKCanvas canvas, bool dark)
    {
        Glow(canvas, new SKPoint(65, 50), 360, dark ? "#0A889CAD" : "#12D5DEE9");
        // A single broad paper-like plane crosses the card edge without becoming a repeating pattern.
        using var plane = new SKPath();
        plane.MoveTo(-90, 415);
        plane.CubicTo(85, 205, 295, 340, 595, -100);
        plane.LineTo(600, 500);
        plane.LineTo(-90, 500);
        plane.Close();
        using var shader = SKShader.CreateLinearGradient(new SKPoint(130, 160), new SKPoint(390, 460),
            [SKColor.Parse(dark ? "#343D49" : "#DFE6EF"), SKColor.Parse(dark ? "#22272E" : "#F5F6F9")],
            null, SKShaderTileMode.Clamp);
        using var fill = new SKPaint { IsAntialias = true, Shader = shader };
        canvas.DrawPath(plane, fill);
        using var edge = new SKPath();
        edge.MoveTo(-90, 415);
        edge.CubicTo(85, 205, 295, 340, 595, -100);
        using var highlight = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
            Color = SKColor.Parse(dark ? "#247F91A7" : "#B0FFFFFF")
        };
        canvas.DrawPath(edge, highlight);
    }

    private static void DrawGlow(SKCanvas canvas, bool dark)
    {
        Glow(canvas, new SKPoint(40, 75), 430, dark ? "#264B6B88" : "#55C8D5E4");
        Glow(canvas, new SKPoint(520, 450), 390, dark ? "#167E7366" : "#38E8D9C7");
        Glow(canvas, new SKPoint(380, -130), 310, dark ? "#1079849B" : "#20DAD4E7");
    }

    private static void DrawFineLines(SKCanvas canvas, bool dark)
    {
        Glow(canvas, new SKPoint(0, 0), 600, dark ? "#10718A9F" : "#14C8D2DE");
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.65f,
            Color = SKColor.Parse(dark ? "#088C9BAE" : "#0D8292A4")
        };
        for (var y = -100; y < 540; y += 9)
        {
            using var line = new SKPath();
            line.MoveTo(-20, y);
            line.CubicTo(160, y - 22, 330, y + 30, 540, y - 12);
            canvas.DrawPath(line, paint);
        }
    }

    private static void Glow(SKCanvas canvas, SKPoint center, float radius, string color)
    {
        var tint = SKColor.Parse(color);
        using var shader = SKShader.CreateRadialGradient(center, radius,
            [tint, tint.WithAlpha((byte)(tint.Alpha * 0.45)), tint.WithAlpha(0)],
            [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { IsAntialias = true, Shader = shader };
        canvas.DrawRect(new SKRect(0, 0, 520, 440), paint);
    }
}
