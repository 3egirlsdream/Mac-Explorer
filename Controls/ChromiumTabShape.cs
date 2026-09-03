using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MacExplorer.Controls;

public enum ChromiumTabShapeMode
{
    Highlight,
    Active
}

/// <summary>
/// Draws the two horizontal tab surfaces used by Chromium: a detached hover
/// highlight and an active tab whose flared lower corners join the content.
/// </summary>
public sealed class ChromiumTabShape : Control
{
    // Cubic approximation of a quarter circle. Using one native cubic per
    // corner avoids the segmented ArcSegment tessellation visible at small
    // tab radii.
    private const double CircleControlPoint = 0.5522847498307936;

    internal const double ToolbarOverlap = 1;
    internal const double HighlightTopInset = 4;
    internal const double HighlightBottomInset = 5;

    private PathGeometry? _outline;
    private Size _outlineSize;
    private ChromiumTabShapeMode _outlineMode = (ChromiumTabShapeMode)(-1);
    private double _outlineTopRadius = double.NaN;
    private double _outlineBottomRadius = double.NaN;

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ChromiumTabShape, IBrush?>(nameof(Fill), Brushes.Transparent);

    public static readonly StyledProperty<ChromiumTabShapeMode> ModeProperty =
        AvaloniaProperty.Register<ChromiumTabShape, ChromiumTabShapeMode>(
            nameof(Mode), ChromiumTabShapeMode.Highlight);

    // Chromium's current horizontal tab radii are 10 DIP at the top and
    // 12 DIP for the lower extension corners.
    public static readonly StyledProperty<double> TopCornerRadiusProperty =
        AvaloniaProperty.Register<ChromiumTabShape, double>(nameof(TopCornerRadius), 10);

    public static readonly StyledProperty<double> BottomCornerRadiusProperty =
        AvaloniaProperty.Register<ChromiumTabShape, double>(nameof(BottomCornerRadius), 12);

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public ChromiumTabShapeMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double TopCornerRadius
    {
        get => GetValue(TopCornerRadiusProperty);
        set => SetValue(TopCornerRadiusProperty, value);
    }

    public double BottomCornerRadius
    {
        get => GetValue(BottomCornerRadiusProperty);
        set => SetValue(BottomCornerRadiusProperty, value);
    }

    public ChromiumTabShape()
    {
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Antialias);
    }

    static ChromiumTabShape()
    {
        AffectsRender<ChromiumTabShape>(
            FillProperty,
            ModeProperty,
            TopCornerRadiusProperty,
            BottomCornerRadiusProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Fill is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var size = Bounds.Size;
        var bottomRadius = ClampBottomRadius(size, BottomCornerRadius);
        var topRadius = ClampTopRadius(size, TopCornerRadius, bottomRadius);

        if (_outline is null || _outlineSize != size || _outlineMode != Mode ||
            _outlineTopRadius != topRadius || _outlineBottomRadius != bottomRadius)
        {
            _outline = Mode == ChromiumTabShapeMode.Active
                ? CreateActiveOutline(size, topRadius, bottomRadius)
                : CreateHighlightOutline(size, topRadius, bottomRadius);
            _outlineSize = size;
            _outlineMode = Mode;
            _outlineTopRadius = topRadius;
            _outlineBottomRadius = bottomRadius;
        }

        using (context.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Antialias }))
            context.DrawGeometry(Fill, null, _outline);
    }

    internal static PathGeometry CreateActiveOutline(
        Size size,
        double topRadius,
        double bottomRadius)
    {
        bottomRadius = ClampBottomRadius(size, bottomRadius);
        topRadius = ClampTopRadius(size, topRadius, bottomRadius);

        var tabBottom = Math.Max(0, size.Height - ToolbarOverlap);
        var tabLeft = bottomRadius;
        var tabRight = size.Width - bottomRadius;

        var figure = new PathFigure
        {
            // Begin inside the one-DIP toolbar overlap and walk clockwise.
            StartPoint = new Point(0, size.Height),
            IsClosed = true,
            IsFilled = true,
            Segments = new PathSegments
            {
                new LineSegment { Point = new Point(0, tabBottom), IsStroked = false },
                CreateBezier(
                    bottomRadius * CircleControlPoint,
                    tabBottom,
                    tabLeft,
                    tabBottom - bottomRadius + bottomRadius * CircleControlPoint,
                    tabLeft,
                    tabBottom - bottomRadius),
                new LineSegment { Point = new Point(tabLeft, topRadius), IsStroked = false },
                CreateBezier(
                    tabLeft,
                    topRadius - topRadius * CircleControlPoint,
                    tabLeft + topRadius - topRadius * CircleControlPoint,
                    0,
                    tabLeft + topRadius,
                    0),
                new LineSegment { Point = new Point(tabRight - topRadius, 0), IsStroked = false },
                CreateBezier(
                    tabRight - topRadius + topRadius * CircleControlPoint,
                    0,
                    tabRight,
                    topRadius - topRadius * CircleControlPoint,
                    tabRight,
                    topRadius),
                new LineSegment
                {
                    Point = new Point(tabRight, tabBottom - bottomRadius),
                    IsStroked = false
                },
                CreateBezier(
                    tabRight,
                    tabBottom - bottomRadius + bottomRadius * CircleControlPoint,
                    size.Width - bottomRadius * CircleControlPoint,
                    tabBottom,
                    size.Width,
                    tabBottom),
                new LineSegment { Point = new Point(size.Width, size.Height), IsStroked = false }
            }
        };

        return new PathGeometry { Figures = new PathFigures { figure } };
    }

    internal static PathGeometry CreateHighlightOutline(
        Size size,
        double topRadius,
        double bottomRadius)
    {
        bottomRadius = ClampBottomRadius(size, bottomRadius);
        var left = bottomRadius;
        var right = Math.Max(left, size.Width - bottomRadius);
        var top = Math.Min(HighlightTopInset, size.Height);
        var bottom = Math.Max(top, size.Height - HighlightBottomInset);
        var radius = Math.Clamp(
            topRadius,
            0,
            Math.Min((right - left) / 2, (bottom - top) / 2));

        var figure = new PathFigure
        {
            StartPoint = new Point(left + radius, top),
            IsClosed = true,
            IsFilled = true,
            Segments = new PathSegments
            {
                new LineSegment { Point = new Point(right - radius, top), IsStroked = false },
                CreateBezier(
                    right - radius + radius * CircleControlPoint,
                    top,
                    right,
                    top + radius - radius * CircleControlPoint,
                    right,
                    top + radius),
                new LineSegment { Point = new Point(right, bottom - radius), IsStroked = false },
                CreateBezier(
                    right,
                    bottom - radius + radius * CircleControlPoint,
                    right - radius + radius * CircleControlPoint,
                    bottom,
                    right - radius,
                    bottom),
                new LineSegment { Point = new Point(left + radius, bottom), IsStroked = false },
                CreateBezier(
                    left + radius - radius * CircleControlPoint,
                    bottom,
                    left,
                    bottom - radius + radius * CircleControlPoint,
                    left,
                    bottom - radius),
                new LineSegment { Point = new Point(left, top + radius), IsStroked = false },
                CreateBezier(
                    left,
                    top + radius - radius * CircleControlPoint,
                    left + radius - radius * CircleControlPoint,
                    top,
                    left + radius,
                    top)
            }
        };

        return new PathGeometry { Figures = new PathFigures { figure } };
    }

    private static double ClampBottomRadius(Size size, double radius)
    {
        var tabBottom = Math.Max(0, size.Height - ToolbarOverlap);
        return Math.Clamp(radius, 0, Math.Min(size.Width / 2, tabBottom));
    }

    private static BezierSegment CreateBezier(
        double control1X,
        double control1Y,
        double control2X,
        double control2Y,
        double endX,
        double endY)
        => new()
        {
            Point1 = new Point(control1X, control1Y),
            Point2 = new Point(control2X, control2Y),
            Point3 = new Point(endX, endY),
            IsStroked = false
        };

    private static double ClampTopRadius(Size size, double radius, double bottomRadius)
    {
        var tabBottom = Math.Max(0, size.Height - ToolbarOverlap);
        return Math.Clamp(
            radius,
            0,
            Math.Min(
                Math.Max(0, tabBottom - bottomRadius),
                Math.Max(0, (size.Width - 2 * bottomRadius) / 2)));
    }
}
