using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

public sealed class UnifiedFileTagTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fkfinder-unified-tags-" + Guid.NewGuid().ToString("N"));
    private DatabaseConnectionFactory Factory => new(Path.Combine(_directory, "tags.db"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MigrationPreservesEmptyCollectionsMergesNamesAndIsNotRepeated()
    {
        using (var connection = Factory.GetConnection())
        {
            SqliteSchema.Initialize(connection);
            Execute(connection, """
                DELETE FROM schema_version WHERE version = 8;
                INSERT INTO schema_version VALUES (7);
                INSERT INTO collections(id, name, sort_order, created_at) VALUES
                    (1, 'Project', 3, 1), (2, 'project', 4, 1), (3, '空标签', 1, 1), (4, 'Red', 2, 1);
                INSERT INTO collection_items(collection_id, file_path, added_at) VALUES
                    (1, '/offline/a.txt', 1), (2, '/offline/a.txt', 2), (2, '/offline/b.txt', 3),
                    (4, '/offline/red.txt', 1), (1, '__remote:server:path', 1);
                INSERT INTO file_tags(file_path, tag, is_system, created_at) VALUES ('/offline/existing.txt', 'Project', 0, 1);
                """);
            SqliteSchema.Initialize(connection);
        }
        using var service = new FileTagService(Factory);
        var tags = await service.GetSidebarTagsAsync(Ct);
        Assert.Equal(new[] { "空标签", "红色", "Project" }, tags.Where(t => t.IsPinned).Select(t => t.Name));
        var project = Assert.Single(tags, t => t.Name.Equals("Project", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, project.ItemCount);
        Assert.Single(tags, t => t.Name == "红色");
        Assert.Contains("__remote:server:path", await service.FindFilePathsAsync(project, Ct));
        Assert.Equal(4, (await service.RetryPendingAsync(Ct)).PendingFiles);
        await service.DeleteTagAsync(project, Ct);
        using (var connection = Factory.GetConnection()) SqliteSchema.Initialize(connection);
        Assert.DoesNotContain(await service.GetSidebarTagsAsync(Ct), t => t.Name == "Project");
    }

    [Fact]
    public async Task PendingAdditionAndRemovalSurviveRestartAndStaleMetadata()
    {
        var store = new MemoryStore { FailWrites = true };
        store.Files["/a"] = [new("保留", 5), new("移除", 0)];
        var add = new FileTag("新增", "#8E8E93", FileTagKind.Custom);
        using (var service = new FileTagService(Factory, new StoreQuery(store), store: store))
        {
            Assert.Equal(1, (await service.SetTagAsync(["/a"], add, true, Ct)).PendingFiles);
            await service.SetTagAsync(["/a"], new("移除", "#8E8E93", FileTagKind.Custom), false, Ct);
            await service.ReplaceFileTagsAsync("/a", ["保留", "移除"], Ct);
            Assert.Equal(new[] { "保留", "新增" }, (await service.GetFileTagsAsync("/a", Ct)).Select(t => t.Name).Order());
            Assert.Empty(await service.FindFilePathsAsync(new("移除", "#8E8E93", FileTagKind.Custom), Ct));
        }
        store.FailWrites = false;
        using var restarted = new FileTagService(Factory, store: store);
        Assert.Equal(new TagSyncResult(1, 0), await restarted.RetryPendingAsync(Ct));
        Assert.Contains(new NativeFileTag("保留", 5), store.Files["/a"]);
        Assert.Contains(new NativeFileTag("新增", 0), store.Files["/a"]);
        Assert.DoesNotContain(store.Files["/a"], t => t.Name == "移除");
    }

    [Fact]
    public async Task ConcurrentChangesAndCaseOnlyRenamePreserveOtherNativeTags()
    {
        var store = new MemoryStore();
        store.Files["/a"] = [new("Other", 7)];
        using var service = new FileTagService(Factory, new StoreQuery(store), store: store);
        var project = await service.CreateTagAsync("Project", Ct);
        var second = await service.CreateTagAsync("第二个", Ct);
        await Task.WhenAll(service.SetTagAsync(["/a"], project, true, Ct), service.SetTagAsync(["/a"], second, true, Ct));
        Assert.Equal(3, store.Files["/a"].Count);
        await service.RenameTagAsync(project, "PROJECT", Ct);
        Assert.Contains(new NativeFileTag("PROJECT"), store.Files["/a"]);
        Assert.Contains(new NativeFileTag("Other", 7), store.Files["/a"]);
        Assert.DoesNotContain(store.Files["/a"], t => t.Name == "Project");
    }

    [Fact]
    public async Task RenameColorAndDeleteIncludeUnindexedFinderMatches()
    {
        var store = new MemoryStore();
        store.Files["/native-only"] = [new("项目"), new("保留", 2)];
        using var service = new FileTagService(Factory, new StoreQuery(store), store: store);
        var tag = await service.CreateTagAsync("项目", Ct);
        await service.RenameTagAsync(tag, "完成", Ct);
        tag = Assert.Single(await service.GetSidebarTagsAsync(Ct), t => t.Name == "完成");
        await service.SetTagColorAsync(tag, 4, Ct);
        Assert.Contains(new NativeFileTag("完成", 4), store.Files["/native-only"]);
        await service.SetTagPinnedAsync(tag, true, Ct);
        Assert.True(Assert.Single(await service.GetSidebarTagsAsync(Ct), t => t.Name == "完成").IsPinned);
        await service.DeleteTagAsync(tag, Ct);
        Assert.Equal(new[] { new NativeFileTag("保留", 2) }, store.Files["/native-only"]);
        Assert.DoesNotContain(await service.GetSidebarTagsAsync(Ct), t => t.Name == "完成");
    }

    [Fact]
    public async Task MoreThanFiveThousandMatchesAreManagedWithoutTruncation()
    {
        var store = new MemoryStore();
        for (var i = 0; i < 6001; i++) store.Files[$"/a/{i}"] = [new("大量")];
        using (var connection = Factory.GetConnection())
        {
            SqliteSchema.Initialize(connection);
            Execute(connection, """
                DELETE FROM schema_version WHERE version = 8;
                INSERT INTO collections(id, name, sort_order, created_at) VALUES (1, '大量', 0, 1);
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 6000)
                INSERT INTO collection_items(collection_id, file_path, added_at) SELECT 1, '/a/' || n, 1 FROM numbers;
                """);
            SqliteSchema.Initialize(connection);
        }
        using var service = new FileTagService(Factory, new StoreQuery(store), store: store);
        var tag = Assert.Single(await service.GetSidebarTagsAsync(Ct), tag => tag.Name == "大量");
        Assert.Equal(6001, tag.ItemCount);
        Assert.Equal(6001, (await service.FindFilePathsAsync(tag, Ct)).Count);
        await service.RenameTagAsync(tag, "全部", Ct);
        Assert.All(store.Files.Values, tags => Assert.Equal(new[] { new NativeFileTag("全部") }, tags));
        Assert.Equal(6001, Assert.Single(await service.GetSidebarTagsAsync(Ct), t => t.Name == "全部").ItemCount);
    }

    [Fact]
    public async Task PendingRenameAndColorSurviveStaleRefreshAndRestart()
    {
        var store = new MemoryStore { FailWrites = true };
        store.Files["/rename"] = [new("旧名称"), new("保留", 2)];
        using (var service = new FileTagService(Factory, new StoreQuery(store), store: store))
        {
            var tag = Assert.Single(await service.GetFileTagsAsync("/rename", Ct), t => t.Name == "旧名称");
            await service.SetTagColorAsync(tag, 4, Ct);
            await service.RenameTagAsync(tag, "新名称", Ct);
            await service.ReplaceFileTagsAsync("/rename", ["旧名称", "保留"], Ct);
            Assert.DoesNotContain(await service.GetSidebarTagsAsync(Ct), t => t.Name == "旧名称");
            var renamed = Assert.Single(await service.GetFileTagsAsync("/rename", Ct), t => t.Name == "新名称");
            Assert.Equal(4, renamed.ColorId);
        }
        store.FailWrites = false;
        using var restarted = new FileTagService(Factory, store: store);
        Assert.Equal(new TagSyncResult(1, 0), await restarted.RetryPendingAsync(Ct));
        Assert.Equal(new[] { new NativeFileTag("保留", 2), new NativeFileTag("新名称", 4) }, store.Files["/rename"].OrderBy(t => t.Name));
    }

    [Fact]
    public async Task PendingChangesFollowDirectoryMovesCopiesAndDeletion()
    {
        var store = new MemoryStore { FailWrites = true };
        using var service = new FileTagService(Factory, store: store);
        var tag = await service.CreateTagAsync("项目", Ct);
        await service.SetTagAsync(["/old/sub/a"], tag, true, Ct);
        await service.UpdatePathAsync("/old", "/moved", Ct);
        await service.CopyPathAsync("/moved", "/copied", Ct);
        await service.DeletePathAsync("/moved", Ct);
        store.FailWrites = false;
        Assert.Equal(new TagSyncResult(1, 0), await service.RetryPendingAsync(Ct));
        Assert.Contains(new NativeFileTag("项目"), store.Files["/copied/sub/a"]);
        Assert.Equal(new[] { "/copied/sub/a" }, await service.FindFilePathsAsync(tag, Ct));
    }

    [Fact]
    public async Task EmptyTagsPersistAndSystemNamesAreProtected()
    {
        using (var service = new FileTagService(Factory))
        {
            var tag = await service.CreateTagAsync("空标签", Ct);
            await service.SetTagColorAsync(tag, 7, Ct);
            await service.SetTagPinnedAsync(tag, true, Ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateTagAsync(" 空标签 ", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteTagAsync(FileTagCatalog.FinderColors[0], Ct));
        }
        using var restarted = new FileTagService(Factory);
        var saved = Assert.Single(await restarted.GetSidebarTagsAsync(Ct), t => t.Name == "空标签");
        Assert.Equal(0, saved.ItemCount);
        Assert.Equal(7, saved.ColorId);
        Assert.True(saved.IsPinned);
    }

    [Fact]
    public async Task MacNativeTagsRoundTripAndOtherColorsAndFileContentsRemainIntact()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "原文件.txt");
        await File.WriteAllTextAsync(path, "unchanged", Ct);
        var store = new MacFileTagStore();
        await store.WriteAsync(path, [new("原标签", 3)], Ct);
        using var service = new FileTagService(Factory, store: store);
        var tag = await service.CreateTagAsync("测试", Ct);
        await service.SetTagColorAsync(tag, 6, Ct);
        await service.SetTagAsync([path], tag, true, Ct);
        Assert.Contains(new NativeFileTag("测试", 6), await store.ReadAsync(path, Ct));
        Assert.Contains(new NativeFileTag("原标签", 3), await store.ReadAsync(path, Ct));
        Assert.Contains("测试", (await new MacMetadataService().GetMetadataAsync(path)).Tags);
        await service.SetTagAsync([path], tag, false, Ct);
        Assert.Equal(new[] { new NativeFileTag("原标签", 3) }, await store.ReadAsync(path, Ct));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(path, Ct));
        await store.WriteAsync(path, [], Ct);
        Assert.Empty(await store.ReadAsync(path, Ct));
    }

    [Fact]
    public async Task InterruptedSyncLeavesRemainingFilesPending()
    {
        using var cancel = new CancellationTokenSource();
        var store = new MemoryStore { AfterWrite = () => cancel.Cancel() };
        using var service = new FileTagService(Factory, store: store);
        var tag = await service.CreateTagAsync("中断恢复", Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetTagAsync(["/a", "/b", "/c"], tag, true, cancel.Token));
        store.AfterWrite = null;
        var result = await service.RetryPendingAsync(Ct);
        Assert.Equal(0, result.PendingFiles);
        Assert.All(new[] { "/a", "/b", "/c" }, path => Assert.Contains(new NativeFileTag("中断恢复"), store.Files[path]));
    }

    [Fact]
    public async Task MigrationNormalizesExistingColorAliasesAndDuplicateMemberships()
    {
        using (var connection = Factory.GetConnection())
        {
            SqliteSchema.Initialize(connection);
            Execute(connection, """
                DELETE FROM schema_version WHERE version = 8;
                INSERT INTO schema_version VALUES (7);
                INSERT INTO collections(id, name, sort_order, created_at) VALUES (1, '红色', 0, 1);
                INSERT INTO collection_items(collection_id, file_path, added_at) VALUES (1, '/a', 1);
                INSERT INTO file_tags(file_path, tag, is_system, created_at) VALUES ('/a', 'Red', 1, 1);
                """);
            SqliteSchema.Initialize(connection);
        }
        var store = new MemoryStore();
        using var service = new FileTagService(Factory, store: store);
        Assert.Equal(1, Assert.Single(await service.GetSidebarTagsAsync(Ct), t => t.Name == "红色").ItemCount);
        Assert.DoesNotContain(await service.GetSidebarTagsAsync(Ct), t => t.Name == "Red");
        await service.RetryPendingAsync(Ct);
        Assert.Equal(new[] { new NativeFileTag("红色", 6) }, store.Files["/a"]);
    }

    [Fact]
    public void FailedMigrationRollsBackVersionAndPreservesLegacyRows()
    {
        using var connection = Factory.GetConnection();
        SqliteSchema.Initialize(connection);
        Execute(connection, """
            DELETE FROM schema_version WHERE version = 8;
            INSERT INTO schema_version VALUES (7);
            INSERT INTO collections(id, name, sort_order, created_at) VALUES (1, '保持', 0, 1);
            INSERT INTO collection_items(collection_id, file_path, added_at) VALUES (1, '/a', 1);
            CREATE TRIGGER reject_migration BEFORE INSERT ON pending_tag_changes BEGIN SELECT RAISE(ABORT, 'interrupt'); END;
            """);
        Assert.Throws<SqliteException>(() => SqliteSchema.Initialize(connection));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_version";
        Assert.Equal(7L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM collection_items";
        Assert.Equal(1L, command.ExecuteScalar());
        Execute(connection, "DROP TRIGGER reject_migration");
        SqliteSchema.Initialize(connection);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private sealed class MemoryStore : IFileTagStore
    {
        public Dictionary<string, List<NativeFileTag>> Files { get; } = new(StringComparer.Ordinal);
        public bool FailWrites { get; set; }
        public Action? AfterWrite { get; set; }
        public Task<IReadOnlyList<NativeFileTag>> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NativeFileTag>>(Files.GetValueOrDefault(path)?.ToArray() ?? []);
        public Task WriteAsync(string path, IReadOnlyList<NativeFileTag> tags, CancellationToken cancellationToken = default)
        {
            if (FailWrites) throw new IOException("Read only");
            Files[path] = tags.ToList();
            AfterWrite?.Invoke();
            return Task.CompletedTask;
        }
    }
    private sealed class StoreQuery(MemoryStore store) : IFinderTagQueryService
    {
        public Task<IReadOnlyList<string>> FindFilePathsAsync(FileTag tag, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(store.Files.Where(file => file.Value.Any(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase))).Select(file => file.Key).ToArray());
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
