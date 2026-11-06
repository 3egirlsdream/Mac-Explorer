using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

/// <summary>Draws tab outlines and shared boundaries beneath the interactive headers.</summary>
public sealed class ChromiumTabStripSurface : BackgroundThemeSurface
{
    internal const double ContentBandHeight = 2;
    internal const double BottomRadius = 8;

    public static readonly StyledProperty<ListBox?> TabStripProperty =
        AvaloniaProperty.Register<ChromiumTabStripSurface, ListBox?>(nameof(TabStrip));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ChromiumTabStripSurface, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> HoverFillProperty =
        AvaloniaProperty.Register<ChromiumTabStripSurface, IBrush?>(nameof(HoverFill));
    public static readonly StyledProperty<IBrush?> DividerFillProperty =
        AvaloniaProperty.Register<ChromiumTabStripSurface, IBrush?>(nameof(DividerFill));

    public static readonly StyledProperty<Color> ShadowColorProperty =
        AvaloniaProperty.Register<ChromiumTabStripSurface, Color>(nameof(ShadowColor));

    private Rect _tabBounds;
    private bool _headersSuppressed;
    internal bool HeadersSuppressed
    {
        get => _headersSuppressed;
        set
        {
            if (_headersSuppressed == value) return;
            _headersSuppressed = value;
            InvalidateVisual();
        }
    }
    private Rect _hoverBounds;
    private Rect _viewport;
    private AdaptiveTabPanel? _panel;
    private Point _panelOrigin;
    private int _arrangeVersion;
    private ListBox? _observedStrip;
    private ListBoxItem? _hoveredTab;
    private readonly TabSilhouetteCache _silhouette = new();
    private Control? _draggedTab;
    internal Control? DraggedTab
    {
        get => _draggedTab;
        set
        {
            if (_draggedTab == value) return;
            _draggedTab = value;
            UpdateTabBounds();
            InvalidateVisual();
        }
    }
    private TopLevel? _topLevel;
    private StreamGeometry? _themeClip, _hoverOutline;
    private (Rect Tab, double Join) _themeClipKey, _hoverOutlineKey;

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

    public IBrush? HoverFill
    {
        get => GetValue(HoverFillProperty);
        set => SetValue(HoverFillProperty, value);
    }

    public IBrush? DividerFill
    {
        get => GetValue(DividerFillProperty);
        set => SetValue(DividerFillProperty, value);
    }

    public Color ShadowColor
    {
        get => GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    static ChromiumTabStripSurface() => AffectsRender<ChromiumTabStripSurface>(
        FillProperty, ShadowColorProperty, HoverFillProperty, DividerFillProperty);

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
        ObserveTabStrip(TabStrip);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel != null)
        {
            _topLevel.LayoutUpdated -= OnLayoutUpdated;
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }
        ObserveTabStrip(null);
        _silhouette.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TabStripProperty && this.IsAttachedToVisualTree())
            ObserveTabStrip(TabStrip);
    }

    private void ObserveTabStrip(ListBox? strip)
    {
        if (_observedStrip == strip) return;
        if (_observedStrip is { } previous)
        {
            previous.SelectionChanged -= OnSelectionChanged;
            previous.ContainerPrepared -= OnContainerPrepared;
            previous.ContainerClearing -= OnContainerClearing;
            foreach (var header in previous.GetRealizedContainers())
                header.PropertyChanged -= OnHeaderPropertyChanged;
        }
        _hoveredTab = null;
        _observedStrip = strip;
        if (strip != null)
        {
            strip.SelectionChanged += OnSelectionChanged;
            strip.ContainerPrepared += OnContainerPrepared;
            strip.ContainerClearing += OnContainerClearing;
            foreach (var header in strip.GetRealizedContainers())
                ObserveHeader(header);
        }
        UpdateTabBounds();
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e) => ObserveHeader(e.Container);

    private void ObserveHeader(Control header)
    {
        header.PropertyChanged += OnHeaderPropertyChanged;
        if (header is ListBoxItem { IsPointerOver: true, DataContext: ExplorerTabViewModel } hovered)
            _hoveredTab = hovered;
    }

    private void OnContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        e.Container.PropertyChanged -= OnHeaderPropertyChanged;
        if (e.Container != _hoveredTab) return;
        _hoveredTab = null;
        UpdateTabBounds();
    }

    private void OnHeaderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsPointerOverProperty || sender is not ListBoxItem { DataContext: ExplorerTabViewModel } header)
            return;
        if (header.IsPointerOver)
            _hoveredTab = header;
        else if (header == _hoveredTab)
            _hoveredTab = null;
        UpdateTabBounds();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateTabBounds();
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateTabBounds();
    private void OnScalingChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void UpdateTabBounds()
    {
        var tabs = _observedStrip;
        var selected = tabs is { SelectedIndex: >= 0, SelectedItem: ExplorerTabViewModel }
            ? tabs.ContainerFromIndex(tabs.SelectedIndex) : null;
        var bounds = selected != DraggedTab ? TranslateBounds(selected) : default;
        var hover = DraggedTab == null && _hoveredTab != selected
            ? TranslateBounds(_hoveredTab) : default;
        var panel = tabs?.ItemsPanelRoot as AdaptiveTabPanel;
        var panelOrigin = panel?.TranslatePoint(default, this) ?? default;
        var arrangeVersion = panel?.ArrangeVersion ?? 0;
        var viewport = default(Rect);
        if (tabs?.FindAncestorOfType<ScrollViewer>() is { } scroller &&
            scroller.TranslatePoint(default, this) is { } origin)
        {
            // Keep the outer feet at the ends of the strip and clip offscreen tabs when scrolling.
            var left = scroller.Offset.X <= 0 ? BottomRadius : 0;
            var right = scroller.Offset.X >= scroller.Extent.Width - scroller.Viewport.Width ? BottomRadius : 0;
            viewport = new Rect(origin.X - left, 0, scroller.Bounds.Width + left + right, 0);
        }
        if (bounds == _tabBounds && hover == _hoverBounds && viewport == _viewport &&
            panel == _panel && panelOrigin == _panelOrigin && arrangeVersion == _arrangeVersion)
            return;
        _tabBounds = bounds;
        _hoverBounds = hover;
        _viewport = viewport;
        _panel = panel;
        _panelOrigin = panelOrigin;
        _arrangeVersion = arrangeVersion;
        InvalidateVisual();
    }

    private Rect TranslateBounds(Control? header) => header?.TranslatePoint(default, this) is { } origin
        ? new Rect(origin, header.Bounds.Size) : default;

    internal IEnumerable<Rect> GetDividerBounds()
    {
        if (_panel == null || DraggedTab != null) yield break;
        var children = _panel.Children;
        var scale = _topLevel?.RenderScaling ?? 1;
        const double height = 16;
        for (var i = 1; i < children.Count; i++)
        {
            var previous = children[i - 1];
            var current = children[i];
            if (!IsPlainTab(previous) || !IsPlainTab(current)) continue;
            var bounds = current.Bounds;
            var x = Math.Round((_panelOrigin.X + bounds.Left) * scale - 0.5) / scale;
            var y = _panelOrigin.Y + bounds.Center.Y - (height + ContentBandHeight) / 2;
            yield return new Rect(x, y, 1 / scale, height);
        }
    }

    private static bool IsPlainTab(Control header) => header is ListBoxItem
        { DataContext: ExplorerTabViewModel, IsSelected: false, IsPointerOver: false };

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (Fill is null || size.Width <= 0 || size.Height <= ContentBandHeight) return;
        var scale = _topLevel?.RenderScaling ?? 1;
        var join = GetJoin(size.Height, scale);
        if (!HeadersSuppressed)
        {
            using var viewportClip = context.PushClip(new Rect(_viewport.X, 0, _viewport.Width, size.Height));
            if (DividerFill is { } divider)
                foreach (var boundary in GetDividerBounds()) context.FillRectangle(divider, boundary);

            if (HoverFill is { } hover && _hoverBounds.Width > 0 && _hoverBounds.Top < join)
            {
                var tab = AlignTabBounds(_hoverBounds, join, scale);
                var key = (tab, join);
                if (_hoverOutline == null || key != _hoverOutlineKey)
                {
                    _hoverOutline = CreateOutline(tab, join);
                    _hoverOutlineKey = key;
                }
                context.DrawGeometry(hover, null, _hoverOutline);
            }

            // The active outline is drawn above the hover feet wherever they meet.
            if (_tabBounds.Width > 0 && _tabBounds.Top < join)
            {
                var tab = AlignTabBounds(_tabBounds, size.Height, scale);
                _silhouette.Draw(context, tab, scale, IsThemeActive ? Brushes.Transparent : Fill, ShadowColor);
                if (IsThemeActive)
                {
                    var key = (tab, join);
                    if (_themeClip == null || key != _themeClipKey)
                    {
                        _themeClip = CreateOutline(tab, join);
                        _themeClipKey = key;
                    }
                    using (context.PushGeometryClip(_themeClip)) RenderTheme(context);
                }
            }
        }
        var band = new Rect(0, join, size.Width, size.Height - join);
        if (IsThemeActive)
        {
            using (context.PushClip(band)) RenderTheme(context);
        }
        else context.FillRectangle(Fill, band);
    }

    internal static double GetJoin(double height, double scale) =>
        Math.Round((height - ContentBandHeight) * scale) / scale;

    private static Rect AlignTabBounds(Rect tab, double bottom, double scale)
    {
        var left = Math.Round(tab.Left * scale) / scale;
        var right = Math.Round(tab.Right * scale) / scale;
        return new Rect(left, tab.Top, right - left, bottom - tab.Top);
    }

    internal static StreamGeometry CreateOutline(Rect tab, double join)
    {
        var bottom = Math.Min(BottomRadius, (join - tab.Top) / 2);
        var top = Math.Min(10, Math.Min(tab.Width / 2, join - tab.Top - bottom));
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        path.BeginFigure(new Point(tab.Left - bottom, tab.Bottom), true);
        path.LineTo(new Point(tab.Left - bottom, join));
        SmoothCorner(path, new Point(tab.Left - bottom, join), new Vector(bottom, 0), new Vector(0, -bottom));
        path.LineTo(new Point(tab.Left, tab.Top + top));
        SmoothCorner(path, new Point(tab.Left, tab.Top + top), new Vector(0, -top), new Vector(top, 0));
        path.LineTo(new Point(tab.Right - top, tab.Top));
        SmoothCorner(path, new Point(tab.Right - top, tab.Top), new Vector(top, 0), new Vector(0, top));
        path.LineTo(new Point(tab.Right, join - bottom));
        SmoothCorner(path, new Point(tab.Right, join - bottom), new Vector(0, bottom), new Vector(bottom, 0));
        path.LineTo(new Point(tab.Right + bottom, tab.Bottom));
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
