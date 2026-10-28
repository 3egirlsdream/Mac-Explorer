using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MacExplorer.Controls;

/// <summary>Tab-local surface that moves above neighboring headers during sorting.</summary>
public sealed class TabDragSurface : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<TabDragSurface, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<Color> ShadowColorProperty =
        AvaloniaProperty.Register<TabDragSurface, Color>(nameof(ShadowColor));
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Color ShadowColor { get => GetValue(ShadowColorProperty); set => SetValue(ShadowColorProperty, value); }
    private readonly TabSilhouetteCache _cache = new();
    static TabDragSurface() => AffectsRender<TabDragSurface>(FillProperty, ShadowColorProperty);
    public TabDragSurface() { IsHitTestVisible = false; }
    public override void Render(DrawingContext context)
    {
        if (Fill != null) _cache.Draw(context, new Rect(Bounds.Size), TopLevel.GetTopLevel(this)?.RenderScaling ?? 1, Fill, ShadowColor);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _cache.Dispose(); base.OnDetachedFromVisualTree(e); }
}

/// <summary>Caches only the tab silhouette; changing its position does not allocate bitmaps.</summary>
internal sealed class TabSilhouetteCache : IDisposable
{
    private const double Padding = 7;
    private Bitmap? _bitmap;
    private Size _size;
    private double _scale;
    private IBrush? _fill;
    private Color _shadow;
    internal int RenderCount { get; private set; }

    internal void Draw(DrawingContext context, Rect bounds, double scale, IBrush fill, Color shadow)
    {
        if (bounds.Width <= 0 || bounds.Height <= ChromiumTabStripSurface.ContentBandHeight) return;
        fill = fill.ToImmutable();
        if (_bitmap == null || _size != bounds.Size || _scale != scale || !Equals(_fill, fill) || _shadow != shadow)
        {
            Dispose();
            _size = bounds.Size; _scale = scale; _fill = fill; _shadow = shadow;
            var size = new Size(bounds.Width + Padding * 2, bounds.Height + Padding * 2);
            var pixels = PixelSize.FromSize(size, scale);
            using var sampled = new RenderTargetBitmap(new PixelSize(pixels.Width * 4, pixels.Height * 4), new Vector(96 * scale * 4, 96 * scale * 4));
            using (var drawing = sampled.CreateDrawingContext())
            using (drawing.PushTransform(Matrix.CreateTranslation(Padding, Padding)))
            {
                var join = Math.Round((bounds.Height - ChromiumTabStripSurface.ContentBandHeight) * scale) / scale;
                var outline = ChromiumTabStripSurface.CreateOutline(bounds.Size, new Rect(bounds.Size), join);
                using var options = drawing.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Antialias });
                if (shadow.A > 0)
                {
                    using var effect = drawing.PushEffect(new BlurEffect { Radius = 2 }, new Rect(bounds.Size));
                    drawing.DrawGeometry(new SolidColorBrush(shadow), null, outline);
                }
                drawing.DrawGeometry(fill, null, outline);
            }
            using var half = Reduce(sampled, new PixelSize(pixels.Width * 2, pixels.Height * 2), scale * 2);
            _bitmap = Reduce(half, pixels, scale);
            RenderCount++;
        }
        context.DrawImage(_bitmap, new Rect(_bitmap.PixelSize.ToSize(1)),
            new Rect(bounds.X - Padding, bounds.Y - Padding, _bitmap.Size.Width, _bitmap.Size.Height));
    }

    private static RenderTargetBitmap Reduce(Bitmap source, PixelSize pixels, double scale)
    {
        var result = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        using var drawing = result.CreateDrawingContext();
        using var options = drawing.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality });
        drawing.DrawImage(source, new Rect(source.PixelSize.ToSize(1)), new Rect(result.Size));
        return result;
    }
    public void Dispose() { _bitmap?.Dispose(); _bitmap = null; }
}
