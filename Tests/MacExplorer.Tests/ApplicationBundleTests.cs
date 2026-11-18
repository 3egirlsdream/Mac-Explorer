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

[Trait("Kind", "UI")]
[Trait("Area", "ApplicationBundle")]
public sealed class ApplicationBundleViewModelTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
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

}

public abstract partial class FileListViewModelTestBase
{
    protected sealed class BundleTestLauncher : IApplicationLauncherService
    {
        public List<string> Opened { get; } = [];
        public Task OpenFileAsync(string path) { Opened.Add(path); return Task.CompletedTask; }
        public Task OpenFileWithAppAsync(string path, string bundleIdentifier) => Task.CompletedTask;
        public Task OpenInTerminalAsync(string path) => Task.CompletedTask;
        public Task OpenInEditorAsync(string path, string cliName, string bundleId) => Task.CompletedTask;
        public Task RevealInFinderAsync(string path) => Task.CompletedTask;
    }

}

[Trait("Kind", "Unit")]
[Trait("Area", "ApplicationBundle")]
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
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS.");
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

[Trait("Area", "FileIcon")]
[Trait("Kind", "Integration")]
public sealed class DirectoryPackageProbeTests
{
    [Fact(Explicit = true)]
    public void BatchMatchesIndividualResultsIncludingApplicationLinksAndMissingItems()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS");
        var root = Directory.CreateTempSubdirectory("FKFinder_PackageBatch_");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "测试 名称.framework")).FullName;
            var project = Directory.CreateDirectory(Path.Combine(root.FullName, "Plan.xcodeproj")).FullName;
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "Sample.app")).FullName;
            var link = Directory.CreateSymbolicLink(Path.Combine(root.FullName, "应用.app"), app).FullName;
            var paths = new[] { link, project, Path.Combine(root.FullName, "missing.app"), folder, app, link };
            var results = Platforms.MacOS.MacSandboxNative.DirectoryPackages(paths);
            for (var i = 0; i < paths.Length; i++)
            {
                var expected = Platforms.MacOS.MacSandboxNative.DirectoryPackage(paths[i], out var target);
                Assert.Equal(expected, results[i].IsPackage);
                Assert.Equal(target, results[i].ApplicationPath);
                if (target == null) Assert.Equal(0, results[i].ApplicationSize);
            }
        }
        finally { root.Delete(true); }
    }

    [Theory(Explicit = true)]
    [InlineData(2048, 0, 0)]
    [InlineData(1536, 448, 64)]
    [InlineData(0, 0, 2048)]
    public async Task OnlyCandidateDirectoriesReachTheNativePackageProbe(int files, int folders, int candidates)
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS");
        var root = Directory.CreateTempSubdirectory("FKFinder_CandidateCounts_");
        try
        {
            for (var i = 0; i < files; i++) File.WriteAllBytes(Path.Combine(root.FullName, $"file-{i}.framework"), []);
            for (var i = 0; i < folders; i++) Directory.CreateDirectory(Path.Combine(root.FullName, $"directory-{i}"));
            for (var i = 0; i < candidates; i++)
            {
                var extension = i % 2 == 0 ? ".framework" : ".xcodeproj";
                Directory.CreateDirectory(Path.Combine(root.FullName, $"project-{i}{extension}"));
            }
            var probes = new List<string>();
            var batchSizes = new List<int>();
            var service = new MacFileService(null, paths =>
            {
                probes.AddRange(paths);
                batchSizes.Add(paths.Length);
                return Platforms.MacOS.MacSandboxNative.DirectoryPackages(paths);
            });
            var entries = new List<FileSystemEntry>();
            await foreach (var batch in service.EnumerateDirectoryBatchesAsync(root.FullName, 256, TestContext.Current.CancellationToken))
                entries.AddRange(batch);
            Assert.Equal(2048, entries.Count);
            Assert.Equal(candidates, probes.Count);
            Assert.Equal(candidates, probes.Distinct().Count());
            Assert.All(batchSizes, size => Assert.InRange(size, 1, 256));
            if (files == 2048) Assert.Empty(batchSizes);
            if (candidates == 2048) Assert.Equal(Enumerable.Repeat(256, 8), batchSizes);
            Assert.All(entries.Where(entry => entry.IsDirectory && entry.Extension == ".framework"), entry =>
            {
                Assert.True(entry.IsFolder);
                Assert.Equal(entry.Name, entry.DisplayName);
                Assert.Equal("folder", entry.DetailsIconSource.IconKey);
            });
            Assert.All(entries.Where(entry => !entry.IsDirectory), entry => Assert.Null(entry.Package));
            var streamed = entries.OrderBy(entry => entry.FullPath).ToArray();
            probes.Clear();
            batchSizes.Clear();
            var contents = await service.GetDirectoryContentsAsync(root.FullName, TestContext.Current.CancellationToken);
            Assert.Equal(candidates, probes.Distinct().Count());
            Assert.Equal(candidates, probes.Count);
            Assert.Equal(streamed.Select(entry => (entry.FullPath, entry.Package)),
                contents.OrderBy(entry => entry.FullPath).Select(entry => (entry.FullPath, entry.Package)));
            if (candidates == 2048) Assert.Equal(Enumerable.Repeat(256, 8), batchSizes);
            TestContext.Current.TestOutputHelper!.WriteLine($"Candidate paths: {probes.Count}, native batches: {batchSizes.Count}");
        }
        finally { root.Delete(true); }
    }
}
