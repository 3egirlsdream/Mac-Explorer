using MacExplorer.Services.Impl;
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

    [Fact]
    public void VisibilityMatchesThePreviousPolicyAcrossGeneratedPaths()
    {
        var random = new Random(17092026);
        string[] segments = ["a", ".hidden", "中文", "report.pdf", "x_y", "100%", "..."];
        string[] roots = ["/", "/scope", "/scope/", "/scope/.explicit", "/other", ""];
        for (var i = 0; i < 5000; i++)
        {
            var path = "/scope/" + string.Join("/", Enumerable.Range(0, random.Next(1, 8))
                .Select(_ => segments[random.Next(segments.Length)]));
            var name = Path.GetFileName(path);
            var directory = random.Next(2) == 0;
            var root = roots[random.Next(roots.Length)];
            var hideFiles = random.Next(2) == 0;
            var hideFolders = random.Next(2) == 0;
            var options = new SearchOptions(HideDotFiles: hideFiles, HideDotFolders: hideFolders);
            Assert.Equal(PreviousPolicy(path, name, directory, root, hideFiles, hideFolders),
                options.IsVisible(path, name, directory, root));
        }
    }

    private static bool PreviousPolicy(string path, string name, bool directory, string root, bool hideFiles, bool hideFolders)
    {
        if (name.StartsWith('.') && (directory ? hideFolders : hideFiles)) return false;
        if (!hideFolders) return true;
        var prefix = SearchPath.Prefix(root);
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        var segments = path[prefix.Length..].Split(Path.DirectorySeparatorChar);
        var count = directory ? segments.Length : segments.Length - 1;
        for (var i = 0; i < count; i++)
            if (segments[i].StartsWith('.')) return false;
        return true;
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
