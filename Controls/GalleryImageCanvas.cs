using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MacExplorer.Controls;

/// <summary>
/// Presents a gallery preview bitmap inside a gently elevated rounded frame,
/// supporting 90-degree rotation and an optional pixel-aligned transparency checkerboard.
/// </summary>
public sealed class GalleryImageCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<GalleryImageCanvas, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<int> RotationDegreesProperty =
        AvaloniaProperty.Register<GalleryImageCanvas, int>(nameof(RotationDegrees));

    public static readonly StyledProperty<bool> ShowCheckerboardProperty =
        AvaloniaProperty.Register<GalleryImageCanvas, bool>(nameof(ShowCheckerboard));

    public static readonly StyledProperty<IBrush?> CheckerLightBrushProperty =
        AvaloniaProperty.Register<GalleryImageCanvas, IBrush?>(nameof(CheckerLightBrush));

    public static readonly StyledProperty<IBrush?> CheckerDarkBrushProperty =
        AvaloniaProperty.Register<GalleryImageCanvas, IBrush?>(nameof(CheckerDarkBrush));

    public static readonly StyledProperty<IBrush?> FrameBorderBrushProperty =
        AvaloniaProperty.Register<GalleryImageCanvas, IBrush?>(nameof(FrameBorderBrush));

    public static readonly StyledProperty<BoxShadows> StageShadowProperty =
        AvaloniaProperty.Register<GalleryImageCanvas, BoxShadows>(nameof(StageShadow));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<GalleryImageCanvas, double>(nameof(CornerRadius), 8);

    public static readonly StyledProperty<double> InnerMarginProperty =
        AvaloniaProperty.Register<GalleryImageCanvas, double>(nameof(InnerMargin), 14);

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int RotationDegrees { get => GetValue(RotationDegreesProperty); set => SetValue(RotationDegreesProperty, value); }
    public bool ShowCheckerboard { get => GetValue(ShowCheckerboardProperty); set => SetValue(ShowCheckerboardProperty, value); }
    public IBrush? CheckerLightBrush { get => GetValue(CheckerLightBrushProperty); set => SetValue(CheckerLightBrushProperty, value); }
    public IBrush? CheckerDarkBrush { get => GetValue(CheckerDarkBrushProperty); set => SetValue(CheckerDarkBrushProperty, value); }
    public IBrush? FrameBorderBrush { get => GetValue(FrameBorderBrushProperty); set => SetValue(FrameBorderBrushProperty, value); }
    public BoxShadows StageShadow { get => GetValue(StageShadowProperty); set => SetValue(StageShadowProperty, value); }
    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public double InnerMargin { get => GetValue(InnerMarginProperty); set => SetValue(InnerMarginProperty, value); }

    static GalleryImageCanvas()
    {
        AffectsRender<GalleryImageCanvas>(
            SourceProperty,
            RotationDegreesProperty,
            ShowCheckerboardProperty,
            CheckerLightBrushProperty,
            CheckerDarkBrushProperty,
            FrameBorderBrushProperty,
            StageShadowProperty,
            CornerRadiusProperty,
            InnerMarginProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Source is not { } source || Bounds.Width <= 1 || Bounds.Height <= 1) return;

        var srcW = source.Size.Width;
        var srcH = source.Size.Height;
        if (srcW <= 0 || srcH <= 0) return;

        var rot = NormalizeRotation(RotationDegrees);
        var swapped = rot is 90 or 270;
        var effW = swapped ? srcH : srcW;
        var effH = swapped ? srcW : srcH;

        var margin = Math.Max(0, InnerMargin);
        var availW = Math.Max(1, Bounds.Width - margin * 2);
        var availH = Math.Max(1, Bounds.Height - margin * 2);
        var scale = Math.Min(availW / effW, availH / effH);
        if (Math.Max(effW, effH) < 64)
            scale = Math.Min(scale, 4.0);

        var drawW = Math.Max(1, effW * scale);
        var drawH = Math.Max(1, effH * scale);
        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;
        var frameRect = new Rect(cx - drawW / 2, cy - drawH / 2, drawW, drawH);

        // Keep the hit-test area aligned with the displayed image frame.
        context.FillRectangle(Brushes.Transparent, frameRect);

        var radius = Math.Clamp(CornerRadius, 0, Math.Min(drawW, drawH) / 6);
        var rounded = new RoundedRect(frameRect, radius);
        if (StageShadow.Count > 0)
            context.DrawRectangle(null, null, rounded, StageShadow);

        using (context.PushClip(rounded))
        {
            if (ShowCheckerboard)
                DrawCheckerboard(context, frameRect);

            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                if (rot == 0)
                {
                    context.DrawImage(source, frameRect);
                }
                else
                {
                    var bitmapW = srcW * scale;
                    var bitmapH = srcH * scale;
                    using (context.PushTransform(Matrix.CreateTranslation(cx, cy)))
                    using (context.PushTransform(Matrix.CreateRotation(rot * Math.PI / 180.0)))
                    {
                        context.DrawImage(source, new Rect(-bitmapW / 2, -bitmapH / 2, bitmapW, bitmapH));
                    }
                }
            }
        }

        if (FrameBorderBrush is { } borderBrush)
        {
            var pen = new Pen(borderBrush, 1);
            context.DrawRectangle(null, pen, new RoundedRect(frameRect.Deflate(0.5), radius));
        }
    }

    private void DrawCheckerboard(DrawingContext context, Rect rect)
    {
        var light = CheckerLightBrush ?? Brushes.White;
        var dark = CheckerDarkBrush ?? Brushes.LightGray;
        context.FillRectangle(light, rect);

        const double cell = 10;
        var cols = (int)Math.Ceiling(rect.Width / cell);
        var rows = (int)Math.Ceiling(rect.Height / cell);
        for (var r = 0; r < rows; r++)
        {
            var y = rect.Y + r * cell;
            for (var c = r % 2; c < cols; c += 2)
            {
                var x = rect.X + c * cell;
                context.FillRectangle(dark, new Rect(x, y, cell, cell));
            }
        }
    }

    internal static int NormalizeRotation(int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        return normalized switch
        {
            90 or 180 or 270 => normalized,
            _ => 0
        };
    }
}
