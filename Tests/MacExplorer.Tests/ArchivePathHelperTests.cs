using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Archive")]
public sealed class ArchivePathHelperTests
{
    [Fact]
    public void Parse_NonArchivePath_ReturnsOriginalPath()
    {
        const string path = "/Users/jiangxinji/Documents/xm/测试/vitest.config 副本 3.zip";

        var (archivePath, internalPath) = ArchivePathHelper.Parse(path);

        Assert.Equal(path, archivePath);
        Assert.Equal("", internalPath);
    }

    [Fact]
    public void Parse_ArchiveSentinel_ReturnsArchiveAndInternalPaths()
    {
        const string archive = "/Users/jiangxinji/Documents/xm/测试/vitest.config 副本 3.zip";
        var sentinel = ArchivePathHelper.Build(archive, "folder/file.txt");

        var (archivePath, internalPath) = ArchivePathHelper.Parse(sentinel);

        Assert.Equal(archive, archivePath);
        Assert.Equal("folder/file.txt", internalPath);
    }

}
