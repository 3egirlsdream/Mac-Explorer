using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

public sealed class HomeWorkspaceTests
{
    [Fact]
    public void LayoutKeepsThePreferredSizeWhenTheViewportShrinks()
    {
        var preferred = new HomeFolderLayout(4, 3);
        Assert.Equal(new HomeFolderLayout(2, 3), preferred.Fit(220));
        Assert.Equal(new HomeFolderLayout(1, 3), preferred.Fit(120));
        Assert.Equal(108, preferred.Fit(120).Width);
        Assert.Equal(3, preferred.Fit(120).Capacity);
        Assert.Equal(preferred, preferred.Fit(1000));
        Assert.Equal(new HomeFolderLayout(2, 5), new HomeFolderLayout(-20, 200).Normalize());
        Assert.Equal(new HomeFolderLayout(4, 3), HomeFolderLayout.FromSize(preferred.Width, preferred.Height));
        Assert.Equal(40, new HomeFolderLayout(8, 5).Capacity);
    }

    [Fact]
    public void FrequencyDecaysInsteadOfPermanentlyPinningOldWork()
    {
        var now = DateTime.UtcNow;
        var current = new HomeUsageEntry("/current", false, 3, now);
        var old = new HomeUsageEntry("/old", false, 500, now.AddDays(-180));
        Assert.True(current.Score(now) > old.Score(now));
        Assert.Equal(current.Score(now), current.Score(now.AddDays(-1)));
    }

    [Fact]
    public async Task RecordsFilesAndFoldersButNotMissingOrVirtualPaths()
    {
        using var fixture = new Fixture();
        var file = fixture.File("report.md");
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "project")).FullName;
        await fixture.Workspace.RecordUseAsync(file);
        var firstAdded = Assert.Single(await fixture.Workspace.GetUsageAsync(false)).AddedUtc;
        await fixture.Workspace.RecordUseAsync(file);
        await fixture.Workspace.RecordUseAsync(folder, directoriesOnly: true);
        await fixture.Workspace.RecordUseAsync(file, directoriesOnly: true);
        await fixture.Workspace.RecordUseAsync("tag://work");
        await fixture.Workspace.RecordUseAsync(Path.Combine(fixture.Root, "missing"));
        var entries = await fixture.Workspace.GetUsageAsync(false);
        Assert.Equal(2, entries.Count);
        Assert.Equal(firstAdded, Assert.Single(entries, e => e.Path == file).AddedUtc);
        Assert.True(firstAdded > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(2, Assert.Single(entries, e => e.Path == file).UseCount);
        Assert.True(Assert.Single(entries, e => e.Path == folder).IsDirectory);
        System.IO.File.Delete(file);
        Assert.Equal(folder, Assert.Single(await fixture.Workspace.GetUsageAsync(false)).Path);
    }

    [Fact]
    public async Task HistoryIsBoundedAndClearingItDoesNotDeleteFiles()
    {
        using var fixture = new Fixture();
        var path = fixture.File("keep.txt");
        await fixture.Workspace.RecordUseAsync(path); // Initialize the history schema.
        using (var connection = fixture.Database.GetConnection())
        {
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO home_usage (path, is_directory, use_count, last_used) VALUES (@path, 0, 1, @time)";
            insert.Parameters.Add("@path", SqliteType.Text);
            insert.Parameters.Add("@time", SqliteType.Integer);
            for (var i = 0; i < 600; i++)
            {
                insert.Parameters["@path"].Value = Path.Combine(fixture.Root, "old-" + i);
                insert.Parameters["@time"].Value = i + 1;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        await fixture.Workspace.RecordUseAsync(path);
        using (var connection = fixture.Database.GetConnection())
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM home_usage";
            Assert.Equal(500L, (long)count.ExecuteScalar()!);
        }
        await fixture.Workspace.ClearHistoryAsync();
        Assert.Empty(await fixture.Workspace.GetUsageAsync(true));
        Assert.True(System.IO.File.Exists(path));
    }

    [Fact]
    public async Task ImportsOldFolderHistoryOnceAndDoesNotUndoClearAfterRestart()
    {
        using var fixture = new Fixture();
        using (var connection = fixture.Database.GetConnection())
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO frequent_folders VALUES (@path, 'old', 7, @time)";
            insert.Parameters.AddWithValue("@path", fixture.Root);
            insert.Parameters.AddWithValue("@time", DateTime.UtcNow.Ticks);
            insert.ExecuteNonQuery();
        }
        Assert.Equal(7, Assert.Single(await fixture.Workspace.GetUsageAsync(true)).UseCount);
        await fixture.Workspace.ClearHistoryAsync();
        using var restarted = new HomeWorkspaceService(fixture.Database, fixture.Settings, fixture.Tags);
        Assert.Empty(await restarted.GetUsageAsync(false));
    }

    [Fact]
    public async Task TagRenameMigratesTheLayoutAndTagDeletionRemovesIt()
    {
        using var fixture = new Fixture();
        var tag = await fixture.Tags.CreateTagAsync("Work");
        fixture.Workspace.SaveLayout(tag, new HomeFolderLayout(6, 4));
        await fixture.Tags.RenameTagAsync(tag, "Projects");
        var renamed = tag with { Name = "Projects" };
        Assert.Equal(new HomeFolderLayout(), fixture.Workspace.GetLayout(tag));
        Assert.Equal(new HomeFolderLayout(6, 4), fixture.Workspace.GetLayout(renamed));
        await fixture.Tags.DeleteTagAsync(renamed);
        Assert.Equal(new HomeFolderLayout(), fixture.Workspace.GetLayout(renamed));
    }

    [Fact]
    public void CommandsArePerFilePersistedAndValidatedBeforeReplacingThePreviousConfiguration()
    {
        using var fixture = new Fixture();
        var script = fixture.File("a ' special $(echo nope).sh");
        var command = HomeScriptCommand.Create(script) with { Name = "Build", Shell = "/bin/sh" };
        fixture.Workspace.SaveCommands(script, [command]);
        using (var restarted = new HomeWorkspaceService(fixture.Database, fixture.Settings, fixture.Tags))
            Assert.Equal(command, Assert.Single(restarted.GetCommands(script)));
        var duplicate = command with { Id = Guid.NewGuid().ToString("N"), Name = " build " };
        Assert.Throws<ArgumentException>(() => fixture.Workspace.SaveCommands(script, [command, duplicate]));
        Assert.Equal(command, Assert.Single(fixture.Workspace.GetCommands(script)));
        Assert.Throws<ArgumentException>(() => fixture.Workspace.SaveCommands(script,
            [command with { Command = "  " }]));
        Assert.Equal(command, Assert.Single(fixture.Workspace.GetCommands(script)));
        fixture.Workspace.SaveCommands(script, []);
        Assert.Empty(fixture.Workspace.GetCommands(script));
        Assert.True(System.IO.File.Exists(script));
    }

    [Fact]
    public void MalformedPreferencesDoNotPreventHomepageLoading()
    {
        using var fixture = new Fixture();
        fixture.Settings.Set(HomeWorkspaceService.LayoutsKey, "{not valid JSON");
        fixture.Settings.Set(HomeWorkspaceService.CommandsKey, "{not valid JSON");
        Assert.Equal(new HomeFolderLayout(), fixture.Workspace.GetLayout(new FileTag("Work", "#808080", FileTagKind.Custom)));
        Assert.Empty(fixture.Workspace.GetCommands(fixture.File("test.sh")));
    }

    [Theory]
    [InlineData("test.py", "python3 \"$SCRIPT\"")]
    [InlineData("test.js", "node \"$SCRIPT\"")]
    [InlineData("test.sh", "/bin/sh \"$SCRIPT\"")]
    [InlineData("test.scpt", "osascript \"$SCRIPT\"")]
    public void DefaultCommandQuotesTheScriptVariable(string path, string expected)
    {
        Assert.True(HomeScriptCommand.IsScript(path));
        Assert.Equal(expected, HomeScriptCommand.Create(path).Command);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("MacExplorer_Home_").FullName;
        public DatabaseConnectionFactory Database { get; }
        public SettingsService Settings { get; }
        public FileTagService Tags { get; }
        public HomeWorkspaceService Workspace { get; }
        public Fixture()
        {
            Database = new DatabaseConnectionFactory(Path.Combine(Root, "test.db"));
            using (var connection = Database.GetConnection())
            {
                using var schema = connection.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE frequent_folders (path TEXT PRIMARY KEY, name TEXT, visit_count INTEGER, last_visited INTEGER);
                    """;
                schema.ExecuteNonQuery();
            }
            Settings = new SettingsService(Database);
            Tags = new FileTagService(Database);
            Workspace = new HomeWorkspaceService(Database, Settings, Tags);
        }
        public string File(string name)
        {
            var path = Path.Combine(Root, name);
            System.IO.File.WriteAllText(path, "test");
            return path;
        }
        public void Dispose()
        {
            Workspace.Dispose(); Tags.Dispose(); Settings.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
