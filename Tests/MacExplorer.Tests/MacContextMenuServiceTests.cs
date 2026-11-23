using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "MacContextMenuService")]
public sealed class MacContextMenuServiceTests
{
    [Fact]
    public async Task GetDefaultApplicationIconsAsync_LoadsIconFromDefaultApplication()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fkfinder-default-app-{Guid.NewGuid():N}");
        var filePath = Path.Combine(root, "document.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(filePath, "test");

        try
        {
            var openWithService = new RecordingOpenWithAppService();
            var service = new MacContextMenuService(new LauncherStub(), openWithService);

            var group = await service.GetDefaultApplicationIconsAsync([filePath]);

            var appPath = Assert.Single(openWithService.RequestedAppPaths);
            Assert.Equal(appPath, Assert.Single(group.Icons));
            Assert.False(group.HasMore);
            Assert.EndsWith(".app", appPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(appPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public async Task GetDefaultApplicationIconsAsync_LimitsDistinctAppsInSelectionOrder(int appCount, bool hasMore)
    {
        string?[] apps = [null, "/Preview.app", "/Preview.app", "/Pages.app", "/Safari.app", "/TextEdit.app"];
        var openWithService = new RecordingOpenWithAppService();
        var service = new MacContextMenuService(new LauncherStub(), openWithService, _ => [], path => apps[int.Parse(path)]);
        var files = Enumerable.Range(0, appCount + 2).Select(index => index.ToString()).ToArray();

        var group = await service.GetDefaultApplicationIconsAsync(files);

        Assert.Equal(["/Preview.app", "/Pages.app", "/Safari.app"], group.Icons);
        Assert.Equal(group.Icons, openWithService.RequestedAppPaths);
        Assert.Equal(hasMore, group.HasMore);
    }

    private sealed class RecordingOpenWithAppService : IOpenWithAppService
    {
        public List<string> RequestedAppPaths { get; } = [];

        public Task<List<OpenWithApp>> GetAllAsync() => Task.FromResult<List<OpenWithApp>>([]);
        public Task<List<OpenWithApp>> GetTopLevelAppsAsync() => Task.FromResult<List<OpenWithApp>>([]);
        public Task<List<OpenWithApp>> GetSubmenuAppsAsync() => Task.FromResult<List<OpenWithApp>>([]);
        public Task<string?> GetAppIconBase64Async(string bundleId) => Task.FromResult<string?>(null);

        public Task<string?> GetAppIconBase64ByPathAsync(string appPath)
        {
            RequestedAppPaths.Add(appPath);
            return Task.FromResult<string?>(appPath);
        }

        public Task AddAsync(string bundleId, string label, bool isTopLevel, string? iconBase64 = null) => Task.CompletedTask;
        public Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder) => Task.CompletedTask;
        public Task RemoveAsync(int id) => Task.CompletedTask;
        public Task<int> RemoveUnavailableAppsAsync() => Task.FromResult(0);
        public Task<List<AppListItem>> GetInstalledAppsAsync() => Task.FromResult<List<AppListItem>>([]);
    }

    private sealed class LauncherStub : IApplicationLauncherService
    {
        public Task OpenFileAsync(string filePath) => Task.CompletedTask;
        public Task OpenFileWithAppAsync(string filePath, string bundleIdentifier) => Task.CompletedTask;
        public Task OpenInTerminalAsync(string directoryPath) => Task.CompletedTask;
        public Task OpenInEditorAsync(string path, string cliName, string bundleId) => Task.CompletedTask;
        public Task RevealInFinderAsync(string path) => Task.CompletedTask;
    }

}
