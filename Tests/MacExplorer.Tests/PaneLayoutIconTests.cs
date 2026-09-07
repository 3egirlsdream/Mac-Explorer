using Avalonia;
using MacExplorer.Controls;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed class PaneLayoutIconTests
{
    [Theory]
    [InlineData(52, 34)]
    [InlineData(30, 21)]
    public void EveryPreviewHasVisibleSeparatedPanesWithinItsBounds(double width, double height)
    {
        var bounds = new Rect(0, 0, width, height);
        foreach (var layout in Enum.GetValues<PaneLayout>())
        {
            var panes = PaneLayoutIcon.GetPaneRects(layout, bounds.Size);
            Assert.Equal(MainWindowViewModel.GetPaneCount(layout), panes.Count);
            for (var index = 0; index < panes.Count; index++)
            {
                var pane = panes[index];
                Assert.True(pane.Width > 0 && pane.Height > 0);
                Assert.True(bounds.Contains(pane));
                Assert.All(panes.Skip(index + 1), other => Assert.False(pane.Intersects(other)));
            }
        }
    }

    [Fact]
    public void SinglePaneFillsThePreviewInsteadOfDrawingATinyCenterBox()
    {
        var pane = Assert.Single(PaneLayoutIcon.GetPaneRects(PaneLayout.Single, new Size(52, 34)));
        Assert.Equal(new Rect(1, 1, 50, 32), pane);
    }

    [Theory]
    [InlineData(PaneLayout.MainLeftTwoRowsRight, true)]
    [InlineData(PaneLayout.MainRightTwoRowsLeft, false)]
    [InlineData(PaneLayout.MainLeftThreeRowsRight, true)]
    [InlineData(PaneLayout.MainRightThreeRowsLeft, false)]
    public void MainPaneUsesTheWorkspaceTwoToOneWidthAndCorrectSide(PaneLayout layout, bool mainOnLeft)
    {
        var panes = PaneLayoutIcon.GetPaneRects(layout, new Size(52, 34));
        Assert.Equal(panes[1].Width * 2, panes[0].Width);
        Assert.Equal(32, panes[0].Height);
        Assert.Equal(mainOnLeft, panes[0].X < panes[1].X);
    }
}
