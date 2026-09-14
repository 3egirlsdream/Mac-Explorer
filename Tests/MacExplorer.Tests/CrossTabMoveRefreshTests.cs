using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task DisposedTabStopsReceivingDirectoryChanges()
    {
        var files = new FakeFileService("/tmp/cross-tab-dispose");
        var notifier = new DirectoryChangeNotifier();
        using var closed = CreateViewModel(files, notifier);
        await closed.RefreshAsync();
        using var open = CreateViewModel(files, notifier);
        await open.RefreshAsync();
        closed.Dispose();
        files.Seed(new FileSystemEntry { FullPath = "/tmp/cross-tab-dispose/new.txt", Name = "new.txt" });

        // A notification already dispatched before closing must also be harmless.
        await closed.RefreshFromNotification();
        notifier.NotifyChanged([files.HomeDirectory]);
        await WaitForIssue11Async(() => open.Entries.Count == 1 && !open.IsDirectoryLoading);
        Assert.Empty(closed.Entries);
        Assert.Equal("new.txt", Assert.Single(open.Entries).Name);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CrossTabMoveRefreshesInactiveSourceAndDestination(bool fast, bool grid)
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("cross-tab-refresh-").FullName;
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var targetDirectory = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        var sourcePath = Path.Combine(sourceDirectory, "moved.txt");
        await File.WriteAllTextAsync(sourcePath, "cross-tab move");
        var files = new MacFileService();
        var notifier = new DirectoryChangeNotifier();
        using var source = CreateViewModel(files, notifier, navigation: new NavigationViewModel(files)
            { CurrentPath = sourceDirectory, IsHomePage = false });
        await source.RefreshAsync();
        // Let the native volume lookup finish before constructing another real-file tab.
        await WaitForIssue11Async(() => !string.IsNullOrEmpty(source.LocationStatusText));
        using var target = CreateViewModel(files, notifier, navigation: new NavigationViewModel(files)
            { CurrentPath = targetDirectory, IsHomePage = false });
        await target.RefreshAsync();
        source.SetSelection([Assert.Single(source.Entries)]);
        target.UseFastFileList = fast;
        target.SetViewMode(grid ? ViewMode.Grid : ViewMode.List);
        var tabs = new MainWindowViewModel(source);
        var sourceTab = tabs.SelectedTab;
        tabs.AddTab(target, select: true);
        Assert.DoesNotContain(sourceTab, tabs.VisiblePanes);

        using var services = new ServiceCollection()
            .AddSingleton<IFileService>(files).BuildServiceProvider();
        var previousServices = App.Services;
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        var view = new FileListView { DataContext = target };
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            RenderIssue11(window);
            var surface = view.FindControl<Grid>("InteractionSurface")!;
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(
                await window.StorageProvider.TryGetFileFromPathAsync(new Uri(sourcePath)))));
            var drop = new DragEventArgs(DragDrop.DropEvent, transfer, surface,
                new Point(40, surface.Bounds.Height - 40), KeyModifiers.None);
            surface.RaiseEvent(drop);

            await WaitForIssue11Async(() => !File.Exists(sourcePath)
                && target.Entries.Count == 1 && !target.IsDirectoryLoading);
            Assert.Equal("cross-tab move", await File.ReadAllTextAsync(Path.Combine(targetDirectory, "moved.txt")));
            Assert.True(drop.Handled);
            Assert.Equal(DragDropEffects.Move, drop.DragEffects);
            await WaitForIssue11Async(() => source.Entries.Count == 0 && !source.IsDirectoryLoading);
            Assert.Empty(source.SelectedEntries);
            tabs.SelectedTab = sourceTab;
            Assert.Empty(tabs.FileList.Entries);
        }
        finally
        {
            window.Close();
            foreach (var tab in tabs.Tabs) tab.Dispose();
            source.Dispose();
            target.Dispose();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previousServices);
            Directory.Delete(root, true);
        }
    }
}
