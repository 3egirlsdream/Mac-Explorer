using Avalonia.Headless.XUnit;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

public sealed class OpenWithSchemaMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fkfinder-openwith-v9-" + Guid.NewGuid().ToString("N"));
    private DatabaseConnectionFactory Factory => new(Path.Combine(_directory, "index.db"));

    [Fact]
    public void FreshInstallEnablesBuiltInsAndMovesSeededEditorsToSubmenu()
    {
        using var connection = Factory.GetConnection();
        SqliteSchema.Initialize(connection);
        Assert.Equal(ExpectedDefaults, ReadApps(connection));
    }

    [Fact]
    public void ExistingInstallationMigratesOnceWithoutReorderingLaterRuns()
    {
        using (var connection = Factory.GetConnection())
        {
            SqliteSchema.Initialize(connection);
            Execute(connection, """
                DELETE FROM schema_version WHERE version >= 9;
                INSERT INTO schema_version VALUES (8);
                DELETE FROM open_with_apps;
                INSERT INTO open_with_apps(bundle_id, label, is_top_level, sort_order) VALUES
                    ('com.microsoft.VSCode', 'VS Code', 1, 0),
                    ('com.todesktop.230313mzl4w4u92', 'Cursor', 1, 1),
                    ('dev.kiro.desktop', 'Kiro', 0, 2),
                    ('com.qoder.ide', 'Qoder', 1, 3);
                """);
            SqliteSchema.Initialize(connection);
            Assert.Equal(ExpectedDefaults, ReadApps(connection));
        }

        using var reopened = Factory.GetConnection();
        SqliteSchema.Initialize(reopened);
        SqliteSchema.Initialize(reopened);
        Assert.Equal(ExpectedDefaults, ReadApps(reopened));
    }

    private static readonly (string BundleId, int IsTopLevel, int SortOrder)[] ExpectedDefaults =
    [
        (BuiltInOpenWithActions.RevealInFinderBundleId, 1, 0),
        (BuiltInOpenWithActions.OpenInTerminalBundleId, 1, 1),
        ("com.microsoft.VSCode", 0, 2),
        ("com.todesktop.230313mzl4w4u92", 0, 3),
        ("dev.kiro.desktop", 0, 4),
        ("com.qoder.ide", 0, 5)
    ];

    private static (string BundleId, int IsTopLevel, int SortOrder)[] ReadApps(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bundle_id, is_top_level, sort_order FROM open_with_apps ORDER BY sort_order";
        var rows = new List<(string, int, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        return rows.ToArray();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuiltInActionsFollowSettingsAndStayMutuallyExclusive(bool enabled)
    {
        var files = new FakeFileService("/tmp/FKFinderBuiltIns");
        var entry = new FileSystemEntry { FullPath = "/tmp/FKFinderBuiltIns/a.txt", Name = "a.txt" };
        files.Seed(entry);
        var launcher = new PlacementLauncher();
        using var vm = CreateViewModel(
            files,
            launcherService: launcher,
            contextMenuService: new PlacementContextMenuService(),
            openWithAppService: PlacementOpenWithAppService.WithBuiltIns(enabled));
        await vm.RefreshAsync();

        var actions = await vm.LoadCompleteFileContextMenuAsync(entry);
        var openWith = Assert.Single(actions, action => action.Label == "打开方式");

        foreach (var label in new[] { "在 Finder 中显示", "在终端中打开" })
        {
            if (enabled)
            {
                Assert.Contains(actions, action => action.Label == label);
                Assert.DoesNotContain(openWith.SubItems!, action => action.Label == label);
            }
            else
            {
                Assert.DoesNotContain(actions, action => action.Label == label);
                Assert.Contains(openWith.SubItems!, action => action.Label == label);
            }
        }

        var submenuApps = openWith.SubItems!.Select(action => action.Label).ToArray();
        Assert.Equal(enabled
            ? new[] { "VS Code" }
            : new[] { "VS Code", "在 Finder 中显示", "在终端中打开" }, submenuApps);

        if (!enabled)
        {
            await Assert.Single(openWith.SubItems!, action => action.Label == "在 Finder 中显示").Execute!();
            await Assert.Single(openWith.SubItems!, action => action.Label == "在终端中打开").Execute!();
            Assert.Equal(entry.FullPath, launcher.RevealedPath);
            Assert.Equal("/tmp/FKFinderBuiltIns", launcher.TerminalPath);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackgroundMenuFollowsTerminalSetting(bool terminalEnabled)
    {
        var files = new FakeFileService("/tmp/FKFinderBuiltIns");
        var launcher = new PlacementLauncher();
        using var vm = CreateViewModel(
            files,
            launcherService: launcher,
            openWithAppService: PlacementOpenWithAppService.WithBuiltIns(terminalEnabled));
        await vm.ShowBackgroundContextMenuAsync(0, 0);

        var actions = vm.ContextMenuActions.ToArray();
        Assert.Equal(terminalEnabled, actions.Any(action => action.Label == "在终端中打开"));
        var submenu = Assert.Single(actions, action => action.Label == "打开方式");
        Assert.Equal(
            terminalEnabled ? new[] { "Kiro" } : new[] { "Kiro", "在终端中打开" },
            submenu.SubItems!.Select(action => action.Label));
        if (!terminalEnabled)
        {
            await Assert.Single(submenu.SubItems!, action => action.Label == "在终端中打开").Execute!();
            Assert.Equal("/tmp/FKFinderBuiltIns", launcher.TerminalPath);
        }
    }

    [Fact]
    public async Task ContextMenuServiceKeepsTopLevelAppsOutOfSubmenuAndHidesBuiltIns()
    {
        var apps = PlacementOpenWithAppService.WithBuiltIns(true);
        apps.TopLevel.Insert(0, new OpenWithApp { Id = 9, BundleId = "com.microsoft.VSCode", Label = "VS Code" });
        var service = new MacContextMenuService(new PlacementLauncher(), apps);
        var vsCodeInstalled = service.IsAppInstalled("com.microsoft.VSCode");
        var kiroInstalled = service.IsAppInstalled("dev.kiro.desktop");
        var filePath = Path.Combine(Path.GetTempPath(), "fkfinder-placement.txt");

        var submenu = await service.GetOpenWithActionsAsync(filePath);
        Assert.Equal(kiroInstalled, submenu.Any(action => action.Label == "Kiro"));
        Assert.DoesNotContain(submenu, action => action.Label == BuiltInOpenWithActions.RevealInFinderLabel);
        Assert.DoesNotContain(submenu, action => action.Label == BuiltInOpenWithActions.OpenInTerminalLabel);
        if (vsCodeInstalled)
            Assert.DoesNotContain(submenu, action => action.Label == "VS Code");

        var topLevel = await service.GetTopLevelOpenWithActionsAsync(filePath);
        Assert.Equal(
            vsCodeInstalled ? new[] { "在 VS Code 中打开" } : [],
            topLevel.Select(action => action.Label));
    }

    private sealed class PlacementOpenWithAppService : IOpenWithAppService
    {
        public List<OpenWithApp> TopLevel { get; init; } = [];
        public List<OpenWithApp> SubmenuApps { get; init; } = [];

        public static PlacementOpenWithAppService WithBuiltIns(bool enabled) => new()
        {
            TopLevel = enabled
                ?
                [
                    new OpenWithApp { Id = 1, BundleId = BuiltInOpenWithActions.RevealInFinderBundleId, Label = BuiltInOpenWithActions.RevealInFinderLabel },
                    new OpenWithApp { Id = 2, BundleId = BuiltInOpenWithActions.OpenInTerminalBundleId, Label = BuiltInOpenWithActions.OpenInTerminalLabel }
                ]
                : [],
            SubmenuApps =
            [
                new OpenWithApp { Id = 3, BundleId = "dev.kiro.desktop", Label = "Kiro", IsTopLevel = false },
                new OpenWithApp { Id = 1, BundleId = BuiltInOpenWithActions.RevealInFinderBundleId, Label = BuiltInOpenWithActions.RevealInFinderLabel, IsTopLevel = false },
                new OpenWithApp { Id = 2, BundleId = BuiltInOpenWithActions.OpenInTerminalBundleId, Label = BuiltInOpenWithActions.OpenInTerminalLabel, IsTopLevel = false }
            ]
        };

        public Task<List<OpenWithApp>> GetAllAsync()
            => Task.FromResult(TopLevel.Concat(SubmenuApps).ToList());

        public Task<List<OpenWithApp>> GetTopLevelAppsAsync() => Task.FromResult(TopLevel.ToList());
        public Task<List<OpenWithApp>> GetSubmenuAppsAsync() => Task.FromResult(SubmenuApps.ToList());
        public Task<string?> GetAppIconBase64Async(string bundleId) => Task.FromResult<string?>(null);
        public Task<string?> GetAppIconBase64ByPathAsync(string appPath) => Task.FromResult<string?>(null);
        public Task AddAsync(string bundleId, string label, bool isTopLevel, string? iconBase64 = null) => Task.CompletedTask;
        public Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder) => Task.CompletedTask;
        public Task RemoveAsync(int id) => Task.CompletedTask;
        public Task<int> RemoveUnavailableAppsAsync() => Task.FromResult(0);
        public Task<List<AppListItem>> GetInstalledAppsAsync() => Task.FromResult<List<AppListItem>>([]);
    }

    private sealed class PlacementLauncher : IApplicationLauncherService
    {
        public string? RevealedPath { get; private set; }
        public string? TerminalPath { get; private set; }

        public Task OpenFileAsync(string filePath) => Task.CompletedTask;
        public Task OpenFileWithAppAsync(string filePath, string bundleIdentifier) => Task.CompletedTask;
        public Task OpenInEditorAsync(string path, string cliName, string bundleId) => Task.CompletedTask;

        public Task OpenInTerminalAsync(string directoryPath)
        {
            TerminalPath = directoryPath;
            return Task.CompletedTask;
        }

        public Task RevealInFinderAsync(string path)
        {
            RevealedPath = path;
            return Task.CompletedTask;
        }
    }

    private sealed class PlacementContextMenuService : IContextMenuService
    {
        public bool IsAppInstalled(string bundleIdentifier) => true;

        public Task<IReadOnlyList<ContextMenuAction>> GetOpenWithActionsAsync(string filePath)
            => Task.FromResult<IReadOnlyList<ContextMenuAction>>(
            [
                new ContextMenuAction
                {
                    Label = "VS Code",
                    IconSvg = Icons.Open,
                    Execute = () => Task.CompletedTask
                }
            ]);

        public Task<IReadOnlyList<ContextMenuAction>> GetTopLevelOpenWithActionsAsync(string path)
            => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);
        public Task<IReadOnlyList<RegisteredApp>> GetApplicationsForFileAsync(string filePath) => Task.FromResult<IReadOnlyList<RegisteredApp>>([]);
        public Task<string?> GetDefaultApplicationIconBase64Async(string filePath) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<ContextMenuAction>> GetFileContextMenuActionsAsync(FileSystemEntry entry) => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);
        public Task<IReadOnlyList<ContextMenuAction>> GetBackgroundContextMenuActionsAsync(string currentDirectory) => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);
        public Task<IReadOnlyList<ContextMenuAction>> GetTrashFileContextMenuActionsAsync(FileSystemEntry entry) => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);
        public Task<IReadOnlyList<ContextMenuAction>> GetTrashBackgroundContextMenuActionsAsync() => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);
    }
}
