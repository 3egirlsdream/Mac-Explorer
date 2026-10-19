using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileTagService")]
public sealed class TagHierarchyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fkfinder-hierarchy-" + Guid.NewGuid().ToString("N"));
    private DatabaseConnectionFactory Factory => new(Path.Combine(_root, "tags.db"));

    [Fact]
    public async Task LegacyMigrationPreservesPresentationAndIsIdempotent()
    {
        using (var db = Factory.GetConnection())
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE tag_definitions(name TEXT PRIMARY KEY COLLATE NOCASE, color_id INTEGER DEFAULT 0,
                    is_pinned INTEGER DEFAULT 0, sort_order INTEGER DEFAULT 0);
                CREATE TABLE file_tags(file_path TEXT, tag TEXT, is_system INTEGER DEFAULT 0, created_at INTEGER,
                    PRIMARY KEY(file_path, tag));
                INSERT INTO tag_definitions(name, is_pinned, sort_order) VALUES ('置顶',1,2),('空',0,0),('资料',0,0);
                INSERT INTO file_tags VALUES('/a','资料',0,1);
                """;
            command.ExecuteNonQuery();
        }
        string[] ordered;
        using (var service = new FileTagService(Factory))
        {
            var tags = await service.GetSidebarTagsAsync();
            Assert.All(tags, tag => Assert.Null(tag.ParentName));
            ordered = tags.Select(t => t.Name).ToArray();
            Assert.Equal("置顶", ordered[0]);
            Assert.True(Array.IndexOf(ordered, "资料") < Array.IndexOf(ordered, "空"));
            await service.MoveTagAsync(tags.Single(t => t.Name == "空"), "资料", 0);
        }
        using var reopened = new FileTagService(Factory);
        Assert.Equal("资料", (await reopened.GetSidebarTagsAsync()).Single(t => t.Name == "空").ParentName);
    }

    [Fact]
    public async Task CreateMoveRenameAndRestartKeepSiblingOrderWithoutChangingMemberships()
    {
        using var service = new FileTagService(Factory);
        var parent = await service.CreateTagAsync("项目");
        var first = await service.CreateChildTagAsync("第一", parent.Name);
        var second = await service.CreateChildTagAsync("第二", parent.Name);
        var grandchild = await service.CreateChildTagAsync("孙标签", first.Name);
        await service.SetTagAsync(["/offline/a"], grandchild, true);
        Assert.Equal(new[] { "第二", "第一" }, (await service.GetSidebarTagsAsync()).Where(t => t.ParentName == "项目").Select(t => t.Name));
        await service.MoveTagAsync(second, first.Name, 0);
        await service.RenameTagAsync(first, "归档");
        Assert.Equal(new[] { "第二", "孙标签" }, (await service.GetSidebarTagsAsync()).Where(t => t.ParentName == "归档").Select(t => t.Name));
        Assert.Equal(new[] { "/offline/a" }, await service.FindFilePathsAsync(grandchild));
        Assert.Empty(await service.FindFilePathsAsync(parent));
        await service.MoveTagAsync(grandchild, null, 0);
        Assert.Equal("孙标签", (await service.GetSidebarTagsAsync()).First().Name);
        await service.SetTagPinnedAsync(parent, true);
        var pinned = await service.GetSidebarTagsAsync();
        Assert.Equal(parent.Name, pinned.First().Name);
        Assert.Equal(grandchild.Name, FileTagTree.Build(pinned).First().Tag.Name);
        using var reopened = new FileTagService(Factory);
        Assert.Null((await reopened.GetSidebarTagsAsync()).Single(t => t.Name == "孙标签").ParentName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateChildTagAsync("第二", null));
    }

    [Fact]
    public async Task MoveRejectsCyclesAndMissingParentsAtomically()
    {
        using var service = new FileTagService(Factory);
        var parent = await service.CreateTagAsync("父");
        var child = await service.CreateChildTagAsync("子", parent.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveTagAsync(parent, child.Name, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveTagAsync(parent, parent.Name, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveTagAsync(child, "不存在", 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateChildTagAsync("孤儿", "不存在"));
        Assert.Null((await service.GetSidebarTagsAsync()).Single(t => t.Name == parent.Name).ParentName);
        await service.MoveTagAsync(child, null, 0);
        Assert.Null((await service.GetSidebarTagsAsync()).Single(t => t.Name == child.Name).ParentName);
    }

    [Fact]
    public async Task DeleteSubtreePreservesSystemColorsAndFilesAndEmitsEveryDeletion()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "keep.txt"); await File.WriteAllTextAsync(path, "keep");
        using var service = new FileTagService(Factory);
        var root = await service.CreateTagAsync("删除");
        var red = FileTagCatalog.FinderColors[0];
        await service.MoveTagAsync(red, root.Name, 0);
        var custom = await service.CreateChildTagAsync("颜色之下", red.Name);
        await service.SetTagAsync([path], root, true); await service.SetTagAsync([path], red, true); await service.SetTagAsync([path], custom, true);
        var deleted = new List<string>(); service.TagRenamed += (_, e) => { if (e.NewName == null) deleted.Add(e.OldName); };
        await service.DeleteTagAsync(root);
        var tags = await service.GetSidebarTagsAsync();
        Assert.DoesNotContain(tags, t => t.Name == root.Name || t.Name == custom.Name);
        Assert.Null(tags.Single(t => t.Name == red.Name).ParentName);
        Assert.Equal(new[] { path }, await service.FindFilePathsAsync(red));
        Assert.Empty(await service.FindFilePathsAsync(custom));
        Assert.Contains(custom.Name, deleted); Assert.Contains(root.Name, deleted);
        Assert.Equal("keep", await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteTagAsync(red));
        await service.RetryPendingAsync();
        using var reopened = new FileTagService(Factory);
        Assert.DoesNotContain(await reopened.GetSidebarTagsAsync(), t => t.Name == custom.Name);
    }

    [Fact]
    public void EmptyFilteringRetainsOnlyAncestorsOfFiles()
    {
        FileTag[] tags = [new("根", "#888888", FileTagKind.Custom), new("空分支", "#888888", FileTagKind.Custom, ParentName: "根"),
            new("子", "#888888", FileTagKind.Custom, ParentName: "根"), new("有文件", "#888888", FileTagKind.Custom, ItemCount: 1, ParentName: "子"),
            new("空根", "#888888", FileTagKind.Custom)];
        var root = Assert.Single(FileTagTree.Build(tags, hideEmpty: true));
        Assert.Equal("根", root.Tag.Name); Assert.Equal("子", Assert.Single(root.Children).Tag.Name);
        Assert.Equal("有文件", Assert.Single(root.Children[0].Children).Tag.Name);
        Assert.Equal(2, FileTagTree.Build(tags).Count);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
