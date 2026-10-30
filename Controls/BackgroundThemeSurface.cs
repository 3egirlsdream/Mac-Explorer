using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

/// <summary>Draws a shared theme texture in window coordinates, independently of scrolling content.</summary>
public class BackgroundThemeSurface : Control
{
    public static readonly StyledProperty<bool> FrostedProperty =
        AvaloniaProperty.Register<BackgroundThemeSurface, bool>(nameof(Frosted));
    public bool Frosted { get => GetValue(FrostedProperty); set => SetValue(FrostedProperty, value); }
    private BackgroundThemeService? _theme;
    private TopLevel? _window;
    protected bool IsThemeActive => _theme?.IsActive == true;

    static BackgroundThemeSurface() => AffectsRender<BackgroundThemeSurface>(FrostedProperty);
    public BackgroundThemeSurface() => IsHitTestVisible = false;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this);
        _theme = WindowBackgroundTheme.GetService(this);
        if (_theme != null) _theme.Changed += InvalidateVisual;
        if (_window != null) _window.SizeChanged += OnWindowSizeChanged;
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_theme != null) _theme.Changed -= InvalidateVisual;
        if (_window != null) _window.SizeChanged -= OnWindowSizeChanged;
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        _theme = null; _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) => InvalidateVisual();
    private void OnThemeVariantChanged(object? sender, EventArgs e) => InvalidateVisual();
    public override void Render(DrawingContext context) => RenderTheme(context);

    protected bool RenderTheme(DrawingContext context)
    {
        if (_theme is not { IsActive: true } || _window == null) return false;
        var image = Frosted ? _theme.FrostedImage : _theme.Image;
        if (image == null || this.TranslatePoint(default, _window) is not { } offset) return false;
        var windowSize = _window.ClientSize;
        if (windowSize.Width <= 0 || windowSize.Height <= 0) return false;
        var tile = CalculateImageRect(image.Size, windowSize, _theme.Options.Layout);
        var brush = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,
            TileMode = _theme.Options.Layout == BackgroundImageLayout.HeightTile ? TileMode.Tile : TileMode.None,
            DestinationRect = new RelativeRect(tile, RelativeUnit.Absolute)
        };
        var surface = this.TryFindResource("ColorBgContent", ActualThemeVariant, out var value) && value is IBrush color
            ? color : Brushes.White;
        using (context.PushClip(new Rect(Bounds.Size)))
        using (context.PushTransform(Matrix.CreateTranslation(-offset.X, -offset.Y)))
        {
            var rect = new Rect(windowSize);
            context.DrawRectangle(surface, null, rect);
            using (context.PushOpacity(_theme.Options.Strength / 100d * (Frosted ? 0.65 : 1)))
                context.DrawRectangle(brush, null, rect);
        }
        return true;
    }

    internal static Rect CalculateImageRect(Size image, Size window, BackgroundImageLayout layout)
    {
        if (image.Width <= 0 || image.Height <= 0 || window.Width <= 0 || window.Height <= 0) return default;
        var scale = window.Height / image.Height;
        if (layout == BackgroundImageLayout.Cover)
        {
            scale = Math.Max(scale, window.Width / image.Width);
            var size = image * scale;
            return new Rect((window.Width - size.Width) / 2, (window.Height - size.Height) / 2, size.Width, size.Height);
        }
        return new Rect(0, 0, image.Width * scale, window.Height);
    }
}
