using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(ViewMode.List)]
    [InlineData(ViewMode.Grid)]
    public async Task DoubleClickLaunchesApplicationAndPackageContentsRemainExplicit(ViewMode mode)
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("FKFinder_AppGesture_");
        var launcher = new BundleTestLauncher();
        var app = new FileSystemEntry
        {
            FullPath = Path.Combine(root.FullName, "Example.APP"), Name = "Example.APP",
            Extension = ".APP", IsDirectory = true, IconKey = "folder"
        };
        Directory.CreateDirectory(app.FullPath);
        var files = new FakeFileService(root.FullName);
        files.Seed(app);
        using var vm = CreateViewModel(files, launcherService: launcher,
            sortFilter: new SortFilterViewModel { ViewMode = mode });
        vm.Entries.Add(app);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<FastFileList>("FastList")!;
            var point = list.TranslatePoint(list.RowBounds(0).Center, window)!.Value;
            for (var i = 0; i < 2; i++)
            {
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(app.FullPath, Assert.Single(launcher.Opened));
            Assert.Equal(root.FullName, vm.CurrentPath);
            Assert.Equal("应用程序", app.KindText);
            Assert.False(app.DetailsIconSource.IsDirectory);
            var contents = Assert.Single(await vm.LoadCompleteFileContextMenuAsync(app), a => a.Label == "显示包内容");
            await contents.Execute!();
            Assert.Equal(app.FullPath, vm.CurrentPath);
        }
        finally { window.Close(); root.Delete(true); }
    }

    private sealed class BundleTestLauncher : IApplicationLauncherService
    {
        public List<string> Opened { get; } = [];
        public Task OpenFileAsync(string path) { Opened.Add(path); return Task.CompletedTask; }
        public Task OpenFileWithAppAsync(string path, string bundleIdentifier) => Task.CompletedTask;
        public Task OpenInTerminalAsync(string path) => Task.CompletedTask;
        public Task OpenInEditorAsync(string path, string cliName, string bundleId) => Task.CompletedTask;
        public Task RevealInFinderAsync(string path) => Task.CompletedTask;
    }
}

public sealed class ApplicationBundleTests
{
    [Fact]
    public void ApplicationsGroupAndSortWithFilesWhileRetainingDirectoryOperations()
    {
        var app = new FileSystemEntry { FullPath = "/Applications/A.APP", Name = "A.APP", IsDirectory = true, IconKey = "folder" };
        var folder = new FileSystemEntry { FullPath = "/tmp/0Folder", Name = "0Folder", IsDirectory = true };
        var sort = new SortFilterViewModel { GroupField = GroupField.Type };
        sort.SetRawEntries([folder, app]);
        IReadOnlyList<FileSystemEntry> sorted = [];
        sort.ApplySortAndGroup(entries => sorted = entries);
        Assert.True(app.IsDirectory);
        Assert.True(app.IsApplication);
        Assert.False(app.IsFolder);
        Assert.Equal("A", app.DisplayName);
        Assert.Same(app, sorted[0]);
        Assert.Same(app, Assert.Single(Assert.Single(sort.Groups, g => g.Name == "应用程序").Entries));
        Assert.Same(folder, Assert.Single(Assert.Single(sort.Groups, g => g.Name == "文件夹").Entries));
        Assert.False(new FileSystemEntry { FullPath = "sftp://server/App.app", Name = "App.app", IsDirectory = true }.IsApplication);
        Assert.False(new FileSystemEntry { FullPath = "/tmp/App.app", Name = "App.app" }.IsApplication);
    }

    [Fact]
    public async Task NativeApplicationIconCacheRefreshesExpiredImages()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Directory.CreateTempSubdirectory("FKFinder_AppIcon_");
        var appPath = Directory.CreateDirectory(Path.Combine(root.FullName, "Example.app")).FullName;
        var service = new MacFileService();
        string? cache = null;
        try
        {
            var entry = Assert.Single(await service.GetDirectoryContentsAsync(root.FullName, TestContext.Current.CancellationToken));
            Assert.True(entry.IsApplication);
            Assert.Equal("app-bundle", entry.IconKey);
            await service.ResolveAppIconsAsync([entry], cancellationToken: TestContext.Current.CancellationToken);
            cache = Assert.IsType<string>(entry.IconUrl);
            var expected = await File.ReadAllBytesAsync(cache, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, expected[..4]);
            await File.WriteAllTextAsync(cache, "expired placeholder", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-2));
            entry.IconUrl = null;
            await service.ResolveAppIconsAsync([entry], cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(cache, entry.IconUrl);
            Assert.Equal(expected, await File.ReadAllBytesAsync(cache, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (cache != null) File.Delete(cache);
            root.Delete(true);
        }
    }
}
