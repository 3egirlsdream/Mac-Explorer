using MacExplorer.Indexing;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Sidebar")]
public sealed class PinnedFolderGroupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sidebar-groups-").FullName;
    private DatabaseConnectionFactory Factory => new(Path.Combine(_root, "pins.db"));

    private PinnedFolderService Store()
    {
        using var connection = Factory.GetConnection();
        SqliteSchema.Initialize(connection);
        return new PinnedFolderService(Factory);
    }

    [Fact]
    public async Task LegacyMigrationPreservesIdentityOrderAndTimeAndCanRunAgain()
    {
        var ticks = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks;
        using (var connection = Factory.GetConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE schema_version(version INTEGER PRIMARY KEY);
                INSERT INTO schema_version VALUES(13);
                CREATE TABLE pinned_folders(id INTEGER PRIMARY KEY AUTOINCREMENT, folder_path TEXT NOT NULL UNIQUE,
                    display_name TEXT NOT NULL, sort_order INTEGER NOT NULL DEFAULT 0, pinned_at INTEGER NOT NULL);
                INSERT INTO pinned_folders VALUES(42, '/second', 'Second', 5, {ticks});
                INSERT INTO pinned_folders VALUES(43, '/first', 'First', 2, {ticks});
                CREATE INDEX idx_pf_sort_order ON pinned_folders(sort_order);
                """;
            command.ExecuteNonQuery();
            SqliteSchema.Initialize(connection);
            SqliteSchema.Initialize(connection);
        }
        using var store = Store();
        var pins = await store.GetAllAsync();
        Assert.Equal(new[] { "/first", "/second" }, pins.Select(pin => pin.FolderPath));
        Assert.Equal(new[] { 43, 42 }, pins.Select(pin => pin.Id));
        Assert.Equal(new[] { 2, 5 }, pins.Select(pin => pin.SortOrder));
        Assert.All(pins, pin => { Assert.Equal(0, pin.GroupId); Assert.Equal(ticks, pin.PinnedAt.ToUniversalTime().Ticks); });
        Assert.Empty(await store.GetGroupsAsync());
        await store.PinAsync("/new", "New");
        Assert.True((await store.GetAllAsync()).Single(pin => pin.FolderPath == "/new").Id > 43);
    }

    [Fact]
    public async Task MembershipIsIndependentAndDeleteDoesNotTouchOriginalFolders()
    {
        var original = Directory.CreateDirectory(Path.Combine(_root, "original")).FullName;
        var child = Path.Combine(original, "content.txt");
        await File.WriteAllTextAsync(child, "preserve me");
        int remaining;
        using (var store = Store())
        {
            var group = await store.CreateGroupAsync("  Projects  ");
            remaining = await store.CreateGroupAsync("References");
            foreach (var id in new[] { 0, group, remaining })
            {
                await store.PinAsync(original, "Original", id);
                await store.PinAsync(original, "Original", id);
            }
            Assert.Equal(3, (await store.GetAllAsync()).Count);
            await store.UnpinAsync(original, group);
            Assert.False(await store.IsPinnedAsync(original, group));
            Assert.True(await store.IsPinnedAsync(original));
            Assert.True(await store.IsPinnedAsync(original, remaining));
            await store.PinAsync(original, "Original", group);
            await store.DeleteGroupAsync(group);
            Assert.Equal(2, (await store.GetAllAsync()).Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PinAsync(original, "Original", group));
            Assert.Equal("References", Assert.Single(await store.GetGroupsAsync()).Name);
        }
        using (var store = Store())
        {
            Assert.True(await store.IsPinnedAsync(original));
            Assert.True(await store.IsPinnedAsync(original, remaining));
            Assert.Equal("preserve me", await File.ReadAllTextAsync(child));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.DeleteGroupAsync(0));
        }
    }

    [Fact]
    public async Task GroupNamesValidateAndNotificationsFollowCommittedChanges()
    {
        using var store = Store();
        var changes = 0;
        store.Changed += (_, _) => changes++;
        var first = await store.CreateGroupAsync("  Work  ");
        Assert.Equal("Work", Assert.Single(await store.GetGroupsAsync()).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateGroupAsync("work"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateGroupAsync("   "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateGroupAsync(new string('a', 81)));
        var second = await store.CreateGroupAsync("Other");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RenameGroupAsync(second, "WORK"));
        await store.RenameGroupAsync(first, "work");
        await store.RenameGroupAsync(second, "资料");
        var names = (await store.GetGroupsAsync()).Select(group => group.Name);
        Assert.Equal(new[] { "work", "资料" }, names);
        Assert.Equal(4, changes);
        await store.GetAllAsync();
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task PathUpdatesCoverEveryGroupAndReorderStaysInsideItsGroup()
    {
        using var store = Store();
        var group = await store.CreateGroupAsync("Work");
        foreach (var id in new[] { 0, group })
        {
            await store.PinAsync("/old", "Old", id);
            await store.PinAsync("/old/child", "Child", id);
            await store.PinAsync("/old-other", "Unaffected", id);
        }
        await store.UpdateFolderPathAsync("/old", "/new", "New");
        await store.ReorderAsync(["/old-other", "/new/child", "/new"], group);
        var pins = await store.GetAllAsync();
        Assert.Equal(new[] { "/new", "/new/child", "/old-other" }, pins.Where(pin => pin.GroupId == 0).Select(pin => pin.FolderPath));
        Assert.Equal(new[] { "/old-other", "/new/child", "/new" }, pins.Where(pin => pin.GroupId == group).Select(pin => pin.FolderPath));
        Assert.All(pins.Where(pin => pin.FolderPath == "/new"), pin => Assert.Equal("New", pin.DisplayName));
        await store.UpdateFolderPathAsync("/new", "/old", "Old");
        Assert.Equal(2, (await store.GetAllAsync()).Count(pin => pin.FolderPath == "/old/child"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
