using Avalonia;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "UI")]
public sealed class ViewMotionTests
{
    [Fact]
    public void EaseOutCubicClampsAndMonotonicallyDecelerates()
    {
        Assert.Equal(0, ViewMotion.EaseOutCubic(-0.5), 6);
        Assert.Equal(0, ViewMotion.EaseOutCubic(0), 6);
        Assert.Equal(1, ViewMotion.EaseOutCubic(1), 6);
        Assert.Equal(1, ViewMotion.EaseOutCubic(1.5), 6);

        var quarter = ViewMotion.EaseOutCubic(0.25);
        var half = ViewMotion.EaseOutCubic(0.5);
        var threeQuarters = ViewMotion.EaseOutCubic(0.75);
        Assert.True(quarter > 0.25 && half > 0.5 && threeQuarters > 0.75);
        Assert.True(quarter < half && half < threeQuarters);
    }

    [Fact]
    public void LerpRectInterpolatesOriginAndSize()
    {
        var from = new Rect(10, 20, 18, 18);
        var to = new Rect(110, 120, 78, 78);

        Assert.Equal(from, ViewMotion.LerpRect(from, to, 0));
        Assert.Equal(to, ViewMotion.LerpRect(from, to, 1));
        Assert.Equal(new Rect(60, 70, 48, 48), ViewMotion.LerpRect(from, to, 0.5));
    }
}
