using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public class ChromiumTabShapeTests
{
    [AvaloniaFact]
    public void ActiveOutlineUsesChromiumRadiiAndJoinsTheContentSurface()
    {
        const double width = 168;
        const double height = 40;
        const double topRadius = 10;
        const double bottomRadius = 12;
        var outline = ChromiumTabShape.CreateActiveOutline(
            new Size(width, height), topRadius, bottomRadius);

        var figure = Assert.Single(outline.Figures!);
        Assert.True(figure.IsClosed);
        Assert.True(figure.IsFilled);
        Assert.Equal(new Point(0, height), figure.StartPoint);

        Assert.Collection(
            figure.Segments!,
            segment => AssertLine(segment, 0, height - ChromiumTabShape.ToolbarOverlap),
            segment => AssertBezierEnd(segment, bottomRadius,
                height - ChromiumTabShape.ToolbarOverlap - bottomRadius),
            segment => AssertLine(segment, bottomRadius, topRadius),
            segment => AssertBezierEnd(segment, bottomRadius + topRadius, 0),
            segment => AssertLine(segment, width - bottomRadius - topRadius, 0),
            segment => AssertBezierEnd(segment, width - bottomRadius, topRadius),
            segment => AssertLine(segment, width - bottomRadius,
                height - ChromiumTabShape.ToolbarOverlap - bottomRadius),
            segment => AssertBezierEnd(segment, width,
                height - ChromiumTabShape.ToolbarOverlap),
            segment => AssertLine(segment, width, height));
    }

    [AvaloniaFact]
    public void HoverHighlightRemainsDetachedFromTheContentSurface()
    {
        const double width = 168;
        const double height = 40;
        const double topRadius = 10;
        const double bottomRadius = 12;
        var outline = ChromiumTabShape.CreateHighlightOutline(
            new Size(width, height), topRadius, bottomRadius);

        var figure = Assert.Single(outline.Figures!);
        Assert.True(figure.IsClosed);
        Assert.True(figure.IsFilled);
        Assert.Equal(new Rect(
            bottomRadius,
            ChromiumTabShape.HighlightTopInset,
            width - 2 * bottomRadius,
            height - ChromiumTabShape.HighlightTopInset - ChromiumTabShape.HighlightBottomInset),
            outline.Bounds);
        Assert.True(outline.Bounds.Bottom < height);
    }

    [AvaloniaFact]
    public void ShapeUsesAntialiasingAndPixelAlignedLayout()
    {
        var shape = new ChromiumTabShape();

        Assert.True(shape.UseLayoutRounding);
        Assert.Equal(EdgeMode.Antialias, RenderOptions.GetEdgeMode(shape));
        Assert.Null(shape.CacheMode);
    }

    private static void AssertLine(PathSegment segment, double x, double y)
    {
        var line = Assert.IsType<LineSegment>(segment);
        Assert.Equal(new Point(x, y), line.Point);
    }

    private static void AssertBezierEnd(PathSegment segment, double x, double y)
    {
        var bezier = Assert.IsType<BezierSegment>(segment);
        Assert.Equal(new Point(x, y), bezier.Point3);
    }
}
