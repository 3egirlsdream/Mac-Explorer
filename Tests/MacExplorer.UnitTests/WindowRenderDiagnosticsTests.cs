using System.Text.Json;
using Avalonia;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.UnitTests;

[Trait("Kind", "Unit")]
[Trait("Area", "Window")]
public sealed class WindowRenderDiagnosticsTests
{
    private static JsonElement Native(double drawableWidth = 1600, double drawableHeight = 1200,
        double windowScale = 2, double viewWidth = 800, double boundsWidth = 800) =>
        JsonSerializer.SerializeToElement(new
        {
            viewSize = new[] { viewWidth, 600 }, viewBoundsSize = new[] { boundsWidth, 600 },
            windowScale, layerKind = "Metal", layerSize = new[] { boundsWidth, 600 },
            backingSize = new[] { boundsWidth * windowScale, 600 * windowScale },
            drawableSize = new[] { drawableWidth, drawableHeight }
        });

    [Fact]
    public void RetinaDrawableUsesPhysicalPixelsNotLogicalWindowSize()
    {
        Assert.Empty(WindowRenderDiagnostics.FindMismatches(new(800, 600), new(800, 600), 2, Native()));
    }

    [Fact]
    public void StaleMetalDrawableIsDetectedEvenWhenLayoutAndScaleAreCorrect()
    {
        Assert.Equal(["metal-drawable-size"], WindowRenderDiagnostics.FindMismatches(
            new(800, 600), new(800, 600), 2, Native(1280, 800)));
    }

    [Fact]
    public void NativeScalingAndManagedLayoutErrorsAreDistinguished()
    {
        Assert.Equal(["managed-layout-size"], WindowRenderDiagnostics.FindMismatches(
            new(800, 600), new(640, 480), 2, Native()));
        Assert.Equal(["backing-scale"], WindowRenderDiagnostics.FindMismatches(
            new(800, 600), new(800, 600), 1, Native()));
        Assert.Equal(["view-coordinate-scale"], WindowRenderDiagnostics.FindMismatches(
            new(800, 600), new(800, 600), 2, Native(drawableWidth: 1280, boundsWidth: 640)));
    }

    [Fact]
    public void PixelRoundingAndUnavailableNativeProbeDoNotProduceFalseMismatch()
    {
        Assert.Empty(WindowRenderDiagnostics.FindMismatches(new(800, 600), new(800, 600), 2, Native(1599, 1201)));
        Assert.Empty(WindowRenderDiagnostics.FindMismatches(new(800, 600), new(800, 600), 2, null));
    }
}
