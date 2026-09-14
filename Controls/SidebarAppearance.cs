using Avalonia;
using Avalonia.Media;
using LiquidGlassAvaloniaUI;
using SkiaSharp;

namespace MacExplorer.Controls;

// Shared with the isolated Demo: the approved backdrop and material have one source.
internal static class SidebarAppearance
{
    public static void Apply(LiquidGlassSurface surface, bool dark)
    {
        surface.TintColor = Colors.Transparent;
        surface.SurfaceColor = dark ? Color.Parse("#CC30302F") : Colors.Transparent;
        surface.BackdropOpacity = dark ? 0.28 : 1;
        surface.BlurRadius = dark ? 12 : 2;
        surface.RefractionHeight = 18;
        surface.RefractionAmount = dark ? 0 : 24;
        surface.Vibrancy = 1;
        surface.DepthEffect = !dark;
        surface.ChromaticAberration = false;
        surface.HighlightOpacity = dark ? 0 : 0.5;
        surface.HighlightWidth = 0.65;
        surface.HighlightBlurRadius = 0.4;
        surface.InnerShadowEnabled = false;
        surface.ShadowEnabled = !dark;
        surface.ShadowRadius = 4;
        surface.ShadowOffset = new Vector(0, 1);
        surface.ShadowColor = Color.Parse("#18000000");
    }

    public static byte[] CreateBackdrop(bool dark, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse(dark ? "#252625" : "#F7F8FA"));
        canvas.Scale(width / 520f, height / 440f);
        if (dark)
            Glow(canvas, new SKPoint(40, 75), 600, "#084B4D48");
        else
        {
            Glow(canvas, new SKPoint(40, 75), 430, "#55C8D5E4");
            Glow(canvas, new SKPoint(520, 450), 390, "#38E8D9C7");
            Glow(canvas, new SKPoint(380, -130), 310, "#20DAD4E7");
        }
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void Glow(SKCanvas canvas, SKPoint center, float radius, string color)
    {
        var tint = SKColor.Parse(color);
        using var shader = SKShader.CreateRadialGradient(center, radius,
            [tint, tint.WithAlpha((byte)(tint.Alpha * 0.45)), tint.WithAlpha(0)],
            [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { IsAntialias = true, IsDither = true, Shader = shader };
        canvas.DrawRect(new SKRect(0, 0, 520, 440), paint);
    }
}
