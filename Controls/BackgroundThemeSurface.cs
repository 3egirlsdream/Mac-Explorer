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
    private WindowBackgroundTheme? _renderer;
    private TopLevel? _window;
    protected bool IsThemeActive => _renderer?.IsActive == true;

    static BackgroundThemeSurface() => AffectsRender<BackgroundThemeSurface>(FrostedProperty);
    public BackgroundThemeSurface() => IsHitTestVisible = false;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this);
        _renderer = WindowBackgroundTheme.GetController(this);
        if (_renderer != null) _renderer.FrameChanged += InvalidateVisual;
        if (_window != null) _window.SizeChanged += OnWindowSizeChanged;
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_renderer != null) _renderer.FrameChanged -= InvalidateVisual;
        if (_window != null) _window.SizeChanged -= OnWindowSizeChanged;
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        _renderer = null; _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) => InvalidateVisual();
    private void OnThemeVariantChanged(object? sender, EventArgs e) => InvalidateVisual();
    public override void Render(DrawingContext context) => RenderTheme(context);

    protected bool RenderTheme(DrawingContext context)
    {
        if (_renderer is not { IsActive: true, Frame: { } frame } || _window == null) return false;
        var image = Frosted ? frame.Frosted : frame.Image;
        if (image == null || this.TranslatePoint(default, _window) is not { } offset) return false;
        var windowSize = _window.ClientSize;
        if (windowSize.Width <= 0 || windowSize.Height <= 0) return false;
        using (context.PushClip(new Rect(Bounds.Size)))
        using (context.PushTransform(Matrix.CreateTranslation(-offset.X, -offset.Y)))
        {
            context.DrawImage(image, new Rect(windowSize));
        }
        return true;
    }

    internal static Rect CalculateImageRect(Size image, Size window, BackgroundImageLayout layout)
        => BackgroundThemeComposition.CalculateImageRect(image, window, layout);
}
