using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileTagService")]
public sealed class FileTagServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"macexplorer-tags-{Guid.NewGuid():N}.db");

    [Fact]
    public void TagPath_RoundTripsFinderAndCustomTags()
    {
        var finderPath = TagPathHelper.Build("Red", FileTagKind.FinderColor);
        var customPath = TagPathHelper.Build("等待处理/重要", FileTagKind.Custom);

        Assert.True(TagPathHelper.TryParse(finderPath, out var finderTag));
        Assert.Equal("红色", finderTag.Name);
        Assert.Equal(FileTagKind.FinderColor, finderTag.Kind);

        Assert.True(TagPathHelper.TryParse(customPath, out var customTag));
        Assert.Equal("等待处理/重要", customTag.Name);
        Assert.Equal(FileTagKind.Custom, customTag.Kind);
    }

    [Fact]
    public async Task ReplaceFileTags_BuildsFinderAndCustomSidebarItems()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var service = CreateService();
        var changedCount = 0;
        service.TagsChanged += (_, _) => changedCount++;

        await service.ReplaceFileTagsAsync("/tmp/a.txt", ["Red", "客户资料"], cancellationToken);
        await service.ReplaceFileTagsAsync("/tmp/b.txt", ["客户资料"], cancellationToken);

        var tags = await service.GetSidebarTagsAsync(cancellationToken);
        var red = Assert.Single(tags, tag => tag.Name == "红色");
        var custom = Assert.Single(tags, tag => tag.Name == "客户资料");

        Assert.True(red.IsFinderColor);
        Assert.Equal(1, red.ItemCount);
        Assert.True(custom.IsCustom);
        Assert.Equal(2, custom.ItemCount);
        Assert.Equal(2, changedCount);
    }

    [Fact]
    public async Task FindFilePaths_UnionsDatabaseAndFinderResults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var finderQuery = new FakeFinderTagQueryService(["/tmp/native.txt", "/tmp/shared.txt"]);
        using var service = CreateService(finderQuery);
        await service.ReplaceFileTagsAsync("/tmp/local.txt", ["项目 A"], cancellationToken);
        await service.ReplaceFileTagsAsync("/tmp/shared.txt", ["项目 A"], cancellationToken);

        var paths = await service.FindFilePathsAsync(
            new FileTag("项目 A", FileTagCatalog.CustomTagColor, FileTagKind.Custom),
            cancellationToken);

        Assert.Equal(3, paths.Count);
        Assert.Contains("/tmp/local.txt", paths);
        Assert.Contains("/tmp/native.txt", paths);
        Assert.Contains("/tmp/shared.txt", paths);
    }

    [Fact]
    public async Task PathOperations_UpdateCopyAndDeleteDirectoryTags()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var service = CreateService();
        await service.ReplaceFileTagsAsync("/old/folder/a.txt", ["归档"], cancellationToken);
        await service.ReplaceFileTagsAsync("/old/folder/sub/b.txt", ["归档"], cancellationToken);

        await service.UpdatePathAsync("/old/folder", "/new/folder", cancellationToken);
        var moved = await service.FindFilePathsAsync(
            new FileTag("归档", FileTagCatalog.CustomTagColor, FileTagKind.Custom),
            cancellationToken);
        Assert.DoesNotContain("/old/folder/a.txt", moved);
        Assert.Contains("/new/folder/a.txt", moved);
        Assert.Contains("/new/folder/sub/b.txt", moved);

        await service.CopyPathAsync("/new/folder", "/copy/folder", cancellationToken);
        var copied = await service.FindFilePathsAsync(
            new FileTag("归档", FileTagCatalog.CustomTagColor, FileTagKind.Custom),
            cancellationToken);
        Assert.Contains("/copy/folder/a.txt", copied);
        Assert.Contains("/copy/folder/sub/b.txt", copied);

        await service.DeletePathAsync("/new/folder", cancellationToken);
        var remaining = await service.FindFilePathsAsync(
            new FileTag("归档", FileTagCatalog.CustomTagColor, FileTagKind.Custom),
            cancellationToken);
        Assert.DoesNotContain(remaining, path => path.StartsWith("/new/folder", StringComparison.Ordinal));
        Assert.Contains("/copy/folder/a.txt", remaining);
    }

    private FileTagService CreateService(IFinderTagQueryService? finderQuery = null) =>
        new(new DatabaseConnectionFactory(_databasePath), finderQuery);

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_databasePath + suffix); }
            catch { }
        }
    }

    private sealed class FakeFinderTagQueryService(IReadOnlyList<string> paths) : IFinderTagQueryService
    {
        public Task<IReadOnlyList<string>> FindFilePathsAsync(
            FileTag tag,
            CancellationToken cancellationToken = default) => Task.FromResult(paths);
    }

}
