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
    public async Task GetDefaultApplicationIconBase64Async_LoadsIconFromDefaultApplication()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fkfinder-default-app-{Guid.NewGuid():N}");
        var filePath = Path.Combine(root, "document.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(filePath, "test");

        try
        {
            var openWithService = new RecordingOpenWithAppService();
            var service = new MacContextMenuService(new LauncherStub(), openWithService);

            var icon = await service.GetDefaultApplicationIconBase64Async(filePath);

            Assert.Equal(RecordingOpenWithAppService.IconResult, icon);
            Assert.NotNull(openWithService.RequestedAppPath);
            Assert.EndsWith(".app", openWithService.RequestedAppPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(openWithService.RequestedAppPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingOpenWithAppService : IOpenWithAppService
    {
        internal const string IconResult = "default-app-icon";

        public string? RequestedAppPath { get; private set; }

        public Task<List<OpenWithApp>> GetAllAsync() => Task.FromResult<List<OpenWithApp>>([]);
        public Task<List<OpenWithApp>> GetTopLevelAppsAsync() => Task.FromResult<List<OpenWithApp>>([]);
        public Task<List<OpenWithApp>> GetSubmenuAppsAsync() => Task.FromResult<List<OpenWithApp>>([]);
        public Task<string?> GetAppIconBase64Async(string bundleId) => Task.FromResult<string?>(null);

        public Task<string?> GetAppIconBase64ByPathAsync(string appPath)
        {
            RequestedAppPath = appPath;
            return Task.FromResult<string?>(IconResult);
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
