using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public sealed class HomeFolderPanelTests
{
    [AvaloniaFact]
    public void ResizingReclaimsGapsWithoutOverlappingCards()
    {
        var first = new Border { Width = 200, Height = 300 };
        var second = new Border { Width = 200, Height = 100 };
        var third = new Border { Width = 200, Height = 100 };
        var panel = new HomeFolderPanel { Children = { first, second, third } };
        panel.InvalidateMeasure();
        panel.Measure(new Size(400, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 400, 500));
        Assert.Equal(new Point(200, 100), third.Bounds.Position);
        first.Height = 100;
        panel.InvalidateMeasure();
        panel.Measure(new Size(400, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 400, 500));
        Assert.Equal(new Point(0, 100), third.Bounds.Position);
        Assert.False(first.Bounds.Intersects(third.Bounds));
        Assert.False(second.Bounds.Intersects(third.Bounds));
    }
}
