using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace MacExplorer.Controls;

/// <summary>One surface for the active tab and the full-width content join.</summary>
public sealed class ChromiumTabStripSurface : Control
{
    internal const double ContentBandHeight = 2;
    internal const double BottomRadius = 16;

    public static readonly StyledProperty<ListBox?> TabStripProperty =
        AvaloniaProperty.Register<ChromiumTabStripSurface, ListBox?>(nameof(TabStrip));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ChromiumTabStripSurface, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<Color> ShadowColorProperty =
        AvaloniaProperty.Register<ChromiumTabStripSurface, Color>(nameof(ShadowColor));

    private Rect _tabBounds;
    private Rect _viewport;
    private readonly TabSilhouetteCache _silhouette = new();
    private Control? _draggedTab;
    internal Control? DraggedTab
    {
        get => _draggedTab;
        set { _draggedTab = value; UpdateTabBounds(); }
    }
    private TopLevel? _topLevel;

    public ListBox? TabStrip
    {
        get => GetValue(TabStripProperty);
        set => SetValue(TabStripProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Color ShadowColor
    {
        get => GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    static ChromiumTabStripSurface() => AffectsRender<ChromiumTabStripSurface>(FillProperty, ShadowColorProperty);

    public ChromiumTabStripSurface()
    {
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null)
        {
            _topLevel.LayoutUpdated += OnLayoutUpdated;
            _topLevel.ScalingChanged += OnScalingChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel != null)
        {
            _topLevel.LayoutUpdated -= OnLayoutUpdated;
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }
        ClearBitmap();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TabStripProperty)
        {
            if (change.OldValue is ListBox previous)
                previous.SelectionChanged -= OnSelectionChanged;
            if (change.NewValue is ListBox current)
                current.SelectionChanged += OnSelectionChanged;
            UpdateTabBounds();
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateTabBounds();
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateTabBounds();
    private void OnScalingChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void UpdateTabBounds()
    {
        var bounds = default(Rect);
        var viewport = default(Rect);
        if (TabStrip is { SelectedIndex: >= 0 } tabs &&
            tabs.ContainerFromIndex(tabs.SelectedIndex) is { } selected &&
            !ReferenceEquals(selected, DraggedTab) &&
            selected.TranslatePoint(default, this) is { } origin)
        {
            bounds = new Rect(origin, selected.Bounds.Size);
            // The background lives outside the scroller, so clip only the tab
            // portion to its viewport. The content band remains full width.
            var scroller = tabs.FindAncestorOfType<ScrollViewer>();
            if (scroller?.TranslatePoint(default, this) is { } viewportOrigin)
                viewport = new Rect(viewportOrigin, scroller.Bounds.Size);
        }
        if (bounds == _tabBounds && viewport == _viewport)
            return;
        _tabBounds = bounds;
        _viewport = viewport;
        InvalidateVisual();
    }

    private void ClearBitmap() => _silhouette.Dispose();

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (Fill is null || size.Width <= 0 || size.Height <= ContentBandHeight) return;
        var scale = _topLevel?.RenderScaling ?? 1;
        var join = Math.Round((size.Height - ContentBandHeight) * scale) / scale;
        if (_tabBounds.Width > 0)
        {
            using var clip = context.PushClip(new Rect(_viewport.X, 0, _viewport.Width, size.Height));
            _silhouette.Draw(context, new Rect(_tabBounds.X, _tabBounds.Y, _tabBounds.Width,
                size.Height - _tabBounds.Y), scale, Fill, ShadowColor);
        }
        context.FillRectangle(Fill, new Rect(0, join, size.Width, size.Height - join));
    }

    internal static StreamGeometry CreateOutline(Size size, Rect tab, double join)
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        path.BeginFigure(new Point(0, size.Height), true);
        path.LineTo(new Point(0, join));
        if (tab.Width > 0 && tab.Top < join)
        {
            var bottom = Math.Min(BottomRadius, Math.Min(tab.Width / 4, (join - tab.Top) / 2));
            var top = Math.Min(10, Math.Min(tab.Width / 4, join - tab.Top - bottom));
            var left = tab.Left + bottom;
            var right = tab.Right - bottom;
            path.LineTo(new Point(tab.Left, join));
            SmoothCorner(path, new Point(tab.Left, join), new Vector(bottom, 0), new Vector(0, -bottom));
            path.LineTo(new Point(left, tab.Top + top));
            SmoothCorner(path, new Point(left, tab.Top + top), new Vector(0, -top), new Vector(top, 0));
            path.LineTo(new Point(right - top, tab.Top));
            SmoothCorner(path, new Point(right - top, tab.Top), new Vector(top, 0), new Vector(0, top));
            path.LineTo(new Point(right, join - bottom));
            SmoothCorner(path, new Point(right, join - bottom), new Vector(0, bottom), new Vector(bottom, 0));
        }
        path.LineTo(new Point(size.Width, join));
        path.LineTo(new Point(size.Width, size.Height));
        path.EndFigure(true);
        return geometry;
    }

    internal static void SmoothCorner(StreamGeometryContext path, Point start, Vector along, Vector across)
    {
        var (first, second) = CreateCornerCurves(start, along, across);
        path.CubicBezierTo(first.Control1, first.Control2, first.End);
        path.CubicBezierTo(second.Control1, second.Control2, second.End);
    }

    internal readonly record struct CubicCurve(Point Start, Point Control1, Point Control2, Point End);

    internal static (CubicCurve First, CubicCurve Second) CreateCornerCurves(Point start, Vector along, Vector across)
    {
        // Two C2-continuous cubics, with zero curvature where they meet the
        // straight edges. Ordinary quarter circles have a curvature jump there.
        var middle = start + along * 0.775 + across * 0.225;
        return (new(start, start + along * 0.1, start + along * 0.55, middle),
            new(middle, start + along + across * 0.45, start + along + across * 0.9, start + along + across));
    }
}
