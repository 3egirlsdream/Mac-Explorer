using Avalonia;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Window")]
public sealed class WindowPlacementMathTests
{
    [Fact]
    public void ValidPlacementKeepsLogicalSizeAndPosition()
    {
        var placement = new WindowPlacement(1280, 800, new PixelPoint(100, 100), false);
        var result = WindowPlacementMath.Clamp(placement, new PixelRect(0, 0, 1920, 1080), 1, 1000, 680);

        Assert.Equal(1280, result.NormalWidth);
        Assert.Equal(800, result.NormalHeight);
        Assert.Equal(new PixelPoint(100, 100), result.Position);
    }

    [Fact]
    public void RemovedDisplayPlacementHasNoUsefulIntersection()
    {
        var placement = new WindowPlacement(1280, 800, new PixelPoint(3000, 100), false);

        Assert.False(WindowPlacementMath.HasUsefulIntersection(
            placement,
            new PixelRect(0, 0, 1920, 1080),
            1));
    }

    [Fact]
    public void RetinaScalingPreservesLogicalSizeWhileClampingPixels()
    {
        var placement = new WindowPlacement(1000, 680, new PixelPoint(1800, 900), false);
        var result = WindowPlacementMath.Clamp(placement, new PixelRect(0, 0, 2560, 1600), 2, 1000, 680);

        Assert.Equal(1000, result.NormalWidth);
        Assert.Equal(680, result.NormalHeight);
        Assert.Equal(new PixelPoint(560, 240), result.Position);
    }

    [Fact]
    public void CenterKeepsMaximizedRestoreFlag()
    {
        var result = WindowPlacementMath.Center(
            1280, 800, true, new PixelRect(0, 0, 1920, 1080), 1, 1000, 680);

        Assert.True(result.IsMaximized);
        Assert.Equal(new PixelPoint(320, 140), result.Position);
    }

    [Fact]
    public void OffsetWindowThatExceedsWorkingAreaMustBeRecentered()
    {
        var candidate = new WindowPlacement(1280, 800, new PixelPoint(664, 304), false);

        Assert.False(WindowPlacementMath.FitsWithinWorkingArea(
            candidate,
            new PixelRect(0, 0, 1920, 1080),
            1));

        var centered = WindowPlacementMath.Center(1280, 800, false, new PixelRect(0, 0, 1920, 1080), 1, 1000, 680);
        Assert.Equal(new PixelPoint(320, 140), centered.Position);
    }

}
