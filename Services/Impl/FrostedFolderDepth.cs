using SkiaSharp;

namespace MacExplorer.Services.Impl;

internal static class FrostedFolderDepth
{
    internal static readonly SKRect Front = new(12, 80, 308, 276.5f);
    internal const float CornerRadius = 16;
    internal const float TabTop = 44;

    internal static SKPath CreateBackplate()
    {
        var back = new SKPath();
        back.MoveTo(28, TabTop); back.LineTo(98, TabTop);
        back.CubicTo(108, 44, 112, 49, 118, 58);
        back.LineTo(122, 63); back.CubicTo(125, 67, 128, 67, 132, 67);
        back.LineTo(292, 67); back.CubicTo(302, 67, 308, 76, 308, 87);
        back.LineTo(308, 260.5f); back.CubicTo(308, 270, 302, 276.5f, 292, 276.5f);
        back.LineTo(28, 276.5f); back.CubicTo(18, 276.5f, 12, 270, 12, 260.5f);
        back.LineTo(12, 61); back.CubicTo(12, 51, 18, TabTop, 28, TabTop); back.Close();
        return back;
    }

    internal static void DrawBackplate(SKCanvas canvas, SKPath back, SKRect front, float tabTop)
    {
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(front.Left + 18, tabTop),
                new SKPoint(front.Right - 12, front.Bottom - 28),
                [new SKColor(164, 195, 222), new SKColor(144, 178, 208), new SKColor(120, 153, 181)],
                [0, .48f, 1], SKShaderTileMode.Clamp)
        };
        canvas.DrawPath(back, fill);
        canvas.Save();
        canvas.ClipPath(back, antialias: true);
        using var filter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, .25f);
        using var highlight = new SKPaint
        {
            IsAntialias = true, Color = new SKColor(219, 237, 250, 70), Style = SKPaintStyle.Stroke,
            StrokeWidth = .7f, MaskFilter = filter
        };
        canvas.Translate(0, .2f);
        canvas.DrawPath(back, highlight);
        canvas.Restore();
    }

    internal static void DrawOpeningShadow(SKCanvas canvas, SKPath back, SKRect front)
    {
        using var face = new SKPath();
        face.AddRoundRect(front, CornerRadius, CornerRadius);
        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.5f);
        var shadow = new SKColor(40, 78, 112, 46);
        using var paint = new SKPaint
        {
            IsAntialias = true, MaskFilter = blur,
            Shader = SKShader.CreateLinearGradient(new SKPoint(front.Left, 0), new SKPoint(front.Right, 0),
                [shadow.WithAlpha(0), shadow, shadow, shadow.WithAlpha(0)], [0, .18f, .82f, 1], SKShaderTileMode.Clamp)
        };
        canvas.Save();
        canvas.ClipPath(back, antialias: true);
        canvas.ClipRect(new SKRect(front.Left, front.Top - 6, front.Right, front.Top + .6f));
        canvas.ClipPath(face, SKClipOperation.Difference, antialias: true);
        canvas.Translate(0, -.3f);
        canvas.DrawPath(face, paint);
        canvas.Restore();
    }
}
