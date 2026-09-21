using MacExplorer.Services.Search;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewSearchVisibilityTests
{
    [Theory]
    [InlineData("/scope/a/report.txt", "report.txt", false, "/scope", true)]
    [InlineData("/scope/.hidden/report.txt", "report.txt", false, "/scope", false)]
    [InlineData("/scope/a/.hidden/report.txt", "report.txt", false, "/scope", false)]
    [InlineData("/scope/a/.file", ".file", false, "/scope", true)]
    [InlineData("/scope/a/.folder", ".folder", true, "/scope", false)]
    [InlineData("/.hidden/report.txt", "report.txt", false, "/", false)]
    [InlineData("/scope/.explicit/report.txt", "report.txt", false, "/scope/.explicit", true)]
    [InlineData("/scope2/.hidden/report.txt", "report.txt", false, "/scope", true)]
    [InlineData("/Scope/.hidden/report.txt", "report.txt", false, "/scope", true)]
    [InlineData("/scope/.hidden/report.txt", "report.txt", false, "/scope/", false)]
    [InlineData("/scope/a/report.txt", "report.txt", false, "/scope/", true)]
    public void HiddenAncestorsRespectRootBoundariesAndExplicitScopes(
        string path, string name, bool directory, string root, bool expected)
    {
        var options = new SearchOptions(HideDotFiles: false, HideDotFolders: true);
        Assert.Equal(expected, options.IsVisible(path, name, directory, root));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FileAndFolderVisibilitySettingsApplyIndependently(bool hideFiles, bool hideFolders)
    {
        var options = new SearchOptions(HideDotFiles: hideFiles, HideDotFolders: hideFolders);
        Assert.True(options.IsVisible("/scope/中文/report.pdf", "report.pdf", false, "/scope"));
        Assert.True(options.IsVisible("/scope/中文", "中文", true, "/scope"));
        Assert.Equal(!hideFiles, options.IsVisible("/scope/.file", ".file", false, "/scope"));
        Assert.Equal(!hideFolders, options.IsVisible("/scope/.folder", ".folder", true, "/scope"));
        Assert.Equal(!hideFolders, options.IsVisible("/scope/.folder/report.pdf", "report.pdf", false, "/scope"));
        Assert.Equal(!hideFolders, options.IsVisible("/scope/.folder/child", "child", true, "/scope"));
        Assert.Equal(!hideFiles && !hideFolders, options.IsVisible("/scope/.folder/.file", ".file", false, "/scope"));
        Assert.Equal(!hideFiles, options.IsVisible("/scope/.explicit/.file", ".file", false, "/scope/.explicit"));
        Assert.True(options.IsVisible("/scope/.explicit/report.pdf", "report.pdf", false, "/scope/.explicit"));
    }

    [Fact]
    public void CandidateVisibilityDoesNotAllocateAPathArrayPerRow()
    {
        const string path = "/scope/project/assets/documents/quarter/report.txt";
        var options = new SearchOptions();
        for (var i = 0; i < 1000; i++) options.IsVisible(path, "report.txt", false, "/scope");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var visible = 0;
        for (var i = 0; i < 10_000; i++)
            if (options.IsVisible(path, "report.txt", false, "/scope")) visible++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10_000, visible);
        Assert.True(allocated < 4096, $"Allocated {allocated} bytes for candidate visibility.");
    }
}
