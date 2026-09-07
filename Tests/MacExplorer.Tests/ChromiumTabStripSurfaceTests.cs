using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public class ChromiumTabStripSurfaceTests
{
    [Theory]
    [InlineData(16, 0, 0, -16)]
    [InlineData(0, -10, 10, 0)]
    [InlineData(10, 0, 0, 10)]
    [InlineData(0, 16, 16, 0)]
    public void CornersHaveContinuousTangentAndCurvature(double ax, double ay, double bx, double by)
    {
        var (first, second) = ChromiumTabStripSurface.CreateCornerCurves(
            new Point(104.5, 42), new Vector(ax, ay), new Vector(bx, by));

        Assert.Equal(first.End, second.Start);
        AssertVectorClose(first.End - first.Control2, second.Control1 - second.Start);
        AssertVectorClose((first.End - first.Control2) - (first.Control2 - first.Control1),
            (second.Control2 - second.Control1) - (second.Control1 - second.Start));

        // Zero curvature at the straight edges: the first/last three points
        // are collinear, with a non-zero tangent, so there is no cusp.
        Vector startTangent = first.Control1 - first.Start;
        Vector endTangent = second.End - second.Control2;
        Assert.True(startTangent.Length > 0);
        Assert.True(endTangent.Length > 0);
        Assert.Equal(0, Cross(startTangent, first.Control2 - first.Start), 8);
        Assert.Equal(0, Cross(endTangent, second.End - second.Control1), 8);
    }

    [AvaloniaFact]
    public void JoinedBackgroundSpansTheFullTitleBarOutsideTheTabScroller()
    {
        var backdrop = new ChromiumTabStripSurface();
        var window = new AppWindow
        {
            Width = 1000, Height = 680,
            TitleBarContent = new Border(),
            TitleBarBackgroundContent = backdrop
        };
        window.Show();
        try
        {
            var titleBar = window.GetVisualDescendants().OfType<WindowTitleBar>().Single();
            var contentRow = titleBar.FindControl<Border>("TitleBarContentRow")!;
            Assert.Equal(titleBar.Bounds.Size, backdrop.Bounds.Size);
            Assert.Equal(new Point(0, 0), backdrop.TranslatePoint(default, titleBar));
            Assert.True(contentRow.ClipToBounds);
            Assert.DoesNotContain(contentRow, backdrop.GetVisualAncestors());
            Assert.False(backdrop.IsHitTestVisible);
        }
        finally { window.Close(); }
    }

    private static double Cross(Vector a, Vector b) => a.X * b.Y - a.Y * b.X;

    private static void AssertVectorClose(Vector expected, Vector actual)
    {
        Assert.Equal(expected.X, actual.X, 8);
        Assert.Equal(expected.Y, actual.Y, 8);
    }
}
