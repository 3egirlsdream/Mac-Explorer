using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MacExplorer.Controls;

// Kept beside the glass surface so the backdrop provider can capture it.
public sealed class SidebarBackdrop : Control
{
    public bool UseWindowCoordinates { get; set; }
    private Bitmap? _bitmap;
    private (bool Dark, double Scale) _key;

    public SidebarBackdrop() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var key = (Dark: ActualThemeVariant == ThemeVariant.Dark, Scale: scale);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        if (_bitmap == null || key != _key)
        {
            // Keep one normalized texture throughout expansion; drawing stretches it with the pane.
            using var stream = new MemoryStream(SidebarAppearance.CreateBackdrop(key.Dark,
                (int)Math.Ceiling(520 * scale), (int)Math.Ceiling(440 * scale)));
            var bitmap = new Bitmap(stream);
            _bitmap?.Dispose();
            _bitmap = bitmap;
            _key = key;
        }
        if (UseWindowCoordinates && TopLevel.GetTopLevel(this) is { } window && this.TranslatePoint(default, window) is { } offset)
        {
            using (context.PushClip(new Rect(Bounds.Size)))
                context.DrawImage(_bitmap, new Rect(-offset.X, -offset.Y, window.Bounds.Width, window.Bounds.Height));
        }
        else context.DrawImage(_bitmap, new Rect(Bounds.Size));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
