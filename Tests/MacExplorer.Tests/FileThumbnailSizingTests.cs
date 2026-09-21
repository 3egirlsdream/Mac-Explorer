using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

public class FileThumbnailSizingTests
{
    [Theory]
    [InlineData(56, 1, 64)]
    [InlineData(56, 2, 128)]
    [InlineData(56, 3, 192)]
    [InlineData(56, 8, 256)]
    [InlineData(-1, -1, 64)]
    public void ThumbnailRequestUsesPhysicalSize(double size, double scale, int expected)
        => Assert.Equal(expected, FileThumbnailSizing.GetPixelSize(size, scale));

    [Fact]
    public void NonFiniteThumbnailInputDoesNotOverflow()
    {
        Assert.Equal(64, FileThumbnailSizing.GetPixelSize(double.NaN, double.NaN));
        Assert.Equal(256, FileThumbnailSizing.GetPixelSize(double.MaxValue, double.MaxValue));
    }
}
