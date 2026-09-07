using MacExplorer.Models;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

public class FileListScrollAnchorAndSizingTests
{
    [Fact]
    public void InsertingBeforeAnchorKeepsSameFileAtSameViewportPosition()
    {
        var entries = Enumerable.Range(0, 100).Select(i => Entry($"/{i}")).ToList();
        var anchor = new FileListScrollAnchor("/10", -5, 305);
        entries.Insert(0, Entry("/new"));
        Assert.Equal(335d, anchor.Resolve(entries, 300));
    }

    [Fact]
    public void RemovedAnchorUsesClampedAbsoluteFallback()
    {
        var anchor = new FileListScrollAnchor("/missing", -5, 700);
        var entries = Enumerable.Range(0, 20).Select(i => Entry($"/{i}")).ToArray();
        Assert.Equal(300d, anchor.Resolve(entries, 300));
        Assert.Equal(0d, anchor.Resolve([], 300));
    }

    [Fact]
    public void ContentInsetIsIncludedInRestoration()
    {
        var anchor = new FileListScrollAnchor("/2", -4, 70, 6);
        var entries = Enumerable.Range(0, 30).Select(i => Entry($"/{i}")).ToArray();
        Assert.Equal(70d, anchor.Resolve(entries, 100));
    }

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

    private static FileSystemEntry Entry(string path) => new() { FullPath = path, Name = path };
}
