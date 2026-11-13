using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileList")]
public sealed class FileListStatusFormatterTests
{
    [Fact]
    public void NoSelectionShowsDirectoryItemCount()
    {
        var entries = CreateFiles(28);

        var text = FileListStatusFormatter.FormatSelectionSummary(entries, []);

        Assert.Equal("28 项", text);
    }

    [Fact]
    public void FileSelectionShowsCountAndTotalSize()
    {
        var selected = new[]
        {
            File("one.bin", 10 * 1024 * 1024),
            File("two.bin", 5 * 1024 * 1024)
        };

        var text = FileListStatusFormatter.FormatSelectionSummary(selected, selected);

        Assert.Equal("已选 2 项 · 15 MB", text);
    }

    [Fact]
    public void SelectionContainingFolderOmitsRecursiveSize()
    {
        var selected = new[]
        {
            File("one.bin", 1024),
            new FileSystemEntry { Name = "Folder", FullPath = "/tmp/Folder", IsDirectory = true }
        };

        var text = FileListStatusFormatter.FormatSelectionSummary(selected, selected);

        Assert.Equal("已选 2 项", text);
    }

    [Fact]
    public void LocalStatusUsesLongestMatchingVolumeRoot()
    {
        var gb = 1000L * 1000 * 1000;
        var drives = new[]
        {
            new DriveSpaceSnapshot("/", 100 * gb, 500 * gb),
            new DriveSpaceSnapshot("/Volumes/Work", 412 * gb, 994 * gb)
        };

        var status = FileListStatusFormatter.GetLocalLocationStatus("/Volumes/Work/Projects/FKFinder", drives);

        Assert.NotNull(status);
        Assert.Equal("可用 412 GB", status.Value.Text);
        Assert.Equal("412 GB 可用，共 994 GB", status.Value.Tooltip);
    }

    [Fact]
    public void LocalStatusUsesSystemAvailableCapacityAndShowsFreeSpaceInDecimalUnits()
    {
        var drives = new[]
        {
            new DriveSpaceSnapshot("/", 133_561_421_824, 994_610_155_520, 142_969_185_185)
        };

        var status = FileListStatusFormatter.GetLocalLocationStatus("/Users/test/Documents", drives);

        Assert.NotNull(status);
        Assert.Equal("可用 142.97 GB", status.Value.Text);
        Assert.Equal("142.97 GB 可用，共 994.61 GB\n空闲 133.56 GB", status.Value.Tooltip);
    }

    [Fact]
    public void LocalStatusFallsBackToFreeSpaceWhenSystemEstimateIsUnavailable()
    {
        var status = FileListStatusFormatter.GetLocalLocationStatus("/Volumes/Work", [
            new DriveSpaceSnapshot("/Volumes/Work", 133_561_421_824, 994_610_155_520)
        ]);

        Assert.NotNull(status);
        Assert.Equal("可用 133.56 GB", status.Value.Text);
        Assert.Equal("133.56 GB 可用，共 994.61 GB", status.Value.Tooltip);
    }

    [Fact]
    public void FullVolumeDisplaysZeroAvailableSpace()
    {
        var status = FileListStatusFormatter.GetLocalLocationStatus("/", [
            new DriveSpaceSnapshot("/", 0, 1_000_000_000, 0)
        ]);

        Assert.NotNull(status);
        Assert.Equal("可用 0 B", status.Value.Text);
        Assert.Equal("0 B 可用，共 1 GB\n空闲 0 B", status.Value.Tooltip);
    }

    [Fact]
    public void SimilarVolumePrefixDoesNotCountAsAPathMatch()
    {
        var drives = new[] { new DriveSpaceSnapshot("/Volumes/Data", 100, 200) };

        var status = FileListStatusFormatter.GetLocalLocationStatus("/Volumes/Database/files", drives);

        Assert.Null(status);
    }

    [Theory]
    [InlineData(true, "开发服务器 · 已连接")]
    [InlineData(false, "开发服务器 · 连接已断开")]
    public void RemoteStatusReflectsConnectionState(bool connected, string expected)
    {
        var status = FileListStatusFormatter.GetRemoteLocationStatus("开发服务器", connected);

        Assert.Equal(expected, status.Text);
        Assert.Equal(expected, status.Tooltip);
    }

    private static IReadOnlyList<FileSystemEntry> CreateFiles(int count) => Enumerable.Range(0, count)
        .Select(index => File($"{index}.txt", index))
        .ToArray();

    private static FileSystemEntry File(string name, long size) => new()
    {
        Name = name,
        FullPath = Path.Combine("/tmp", name),
        Size = size
    };

}
