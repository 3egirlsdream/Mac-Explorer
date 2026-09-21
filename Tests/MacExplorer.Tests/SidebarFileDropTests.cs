using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Views;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData("direct")]
    [InlineData("hover")]
    [InlineData("continue-blank")]
    [InlineData("continue-child")]
    [InlineData("final-row")]
    [InlineData("pinned")]
    [InlineData("volume")]
    [InlineData("leave")]
    [InlineData("detach")]
    [InlineData("context")]
    [InlineData("invalid")]
    [InlineData("same-directory")]
    [InlineData("self")]
    public async Task SidebarFileDragNavigatesAndMovesOnlyAtFinalDrop(string scenario)
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("sidebar-drop-").FullName;
        var desktop = Directory.CreateDirectory(Path.Combine(root, "Desktop")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(desktop, "child")).FullName;
        var source = scenario == "self" ? desktop
            : Path.Combine(scenario == "same-directory" ? desktop : root, "source.txt");
        if (scenario != "self") await File.WriteAllTextAsync(source, "sidebar-drag-fixture");
        var files = new FakeFileService(root);
        foreach (var path in new[] { root, desktop, downloads, child })
            files.Seed(new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path), IsDirectory = true });
        files.Seed(new FileSystemEntry { FullPath = source, Name = Path.GetFileName(source), IsDirectory = scenario == "self" });
        using var vm = CreateViewModel(files);
        vm.SetViewMode(ViewMode.List);
        await vm.RefreshAsync();
        using var services = new ServiceCollection().AddSingleton<IFileService>(files).BuildServiceProvider();
        var previousServices = App.Services;
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        if (scenario == "volume")
            vm.ExternalVolumes.Add(new VolumeInfo { Path = desktop, DisplayName = "Test volume", IsExternal = true });
        var sidebar = new FinderSidebarView { DataContext = vm, Width = 240 };
        sidebar.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        sidebar.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        var fileView = new FileListView { DataContext = vm };
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        panel.Children.Add(sidebar);
        Grid.SetColumn(fileView, 1);
        panel.Children.Add(fileView);
        var window = new Window { Width = 1100, Height = 850, Content = panel };
        try
        {
            window.Show();
            RenderIssue11(window);
            var row = sidebar.FindControl<Border>("DesktopItem")!;
            if (scenario == "pinned")
            {
                vm.PinnedFolders.Add(new PinnedFolder { FolderPath = desktop, DisplayName = "Pinned target" });
                RenderIssue11(window);
                row = sidebar.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is string path && path == desktop);
            }
            else if (scenario == "volume")
            {
                RenderIssue11(window);
                await WaitForIssue11Async(() =>
                {
                    RenderIssue11(window);
                    return sidebar.GetVisualDescendants().OfType<Border>().Any(b => b.Tag is VolumeInfo volume && volume.Path == desktop);
                });
                row = sidebar.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is VolumeInfo volume && volume.Path == desktop);
            }
            using var data = new DataTransfer();
            var item = scenario == "self"
                ? (IStorageItem?)await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(source))
                : await window.StorageProvider.TryGetFileFromPathAsync(new Uri(source));
            data.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(item)));
            Point RowPoint(Border target) => target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), sidebar)!.Value;
            DragEventArgs Raise(Avalonia.Interactivity.RoutedEvent<DragEventArgs> evt, Point point)
            {
                var args = new DragEventArgs(evt, data, sidebar, point, KeyModifiers.None);
                sidebar.RaiseEvent(args);
                return args;
            }
            await WaitForIssue11Async(() =>
            {
                RenderIssue11(window);
                var hit = sidebar.InputHitTest(RowPoint(row)) as Visual;
                return hit == row || hit?.GetVisualAncestors().Contains(row) == true;
            });
            var over = Raise(DragDrop.DragOverEvent, RowPoint(row));
            var invalid = scenario is "same-directory" or "self";
            Assert.Equal(invalid ? DragDropEffects.None : DragDropEffects.Move, over.DragEffects);
            Assert.Empty(files.MoveRequests);
            Assert.Equal(root, vm.CurrentPath);
            if (scenario is "hover" or "continue-blank" or "continue-child")
            {
                await WaitForIssue11Async(() => vm.CurrentPath == desktop && !vm.IsDirectoryLoading);
                Assert.Empty(files.MoveRequests);
            }
            if (scenario is "leave" or "detach" or "context" or "invalid" || invalid)
            {
                if (scenario == "leave") Raise(DragDrop.DragLeaveEvent, RowPoint(row));
                if (scenario == "detach") panel.Children.Remove(sidebar);
                if (scenario == "context") sidebar.DataContext = null;
                if (scenario == "invalid")
                {
                    var trash = sidebar.FindControl<Border>("TrashItem")!;
                    Assert.Equal(DragDropEffects.None, Raise(DragDrop.DragOverEvent, RowPoint(trash)).DragEffects);
                    Assert.Equal(DragDropEffects.None, Raise(DragDrop.DropEvent, RowPoint(trash)).DragEffects);
                }
                if (invalid) Assert.Equal(DragDropEffects.None, Raise(DragDrop.DropEvent, RowPoint(row)).DragEffects);
                await Task.Delay(750);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(root, vm.CurrentPath);
                Assert.Empty(files.MoveRequests);
                Assert.DoesNotContain("folder-drop-target", row.Classes);
                return;
            }

            DragEventArgs drop;
            var expectedTarget = desktop;
            if (scenario.StartsWith("continue-", StringComparison.Ordinal))
            {
                Raise(DragDrop.DragLeaveEvent, RowPoint(row));
                RenderIssue11(window);
                var surface = fileView.FindControl<Grid>("InteractionSurface")!;
                var list = fileView.FindControl<FastFileList>("FastList")!;
                var point = new Point(40, surface.Bounds.Height - 40);
                if (scenario == "continue-child")
                {
                    point = list.TranslatePoint(list.RowBounds(list.IndexOfPath(child)).Center, surface)!.Value;
                    expectedTarget = child;
                }
                drop = new DragEventArgs(DragDrop.DropEvent, data, surface, point, KeyModifiers.None);
                surface.RaiseEvent(drop);
            }
            else
            {
                if (scenario == "final-row")
                {
                    row = sidebar.FindControl<Border>("DownloadsItem")!;
                    expectedTarget = downloads;
                }
                drop = Raise(DragDrop.DropEvent, RowPoint(row));
            }
            await WaitForIssue11Async(() => files.MoveCalls == 1 && !vm.IsDirectoryLoading);
            Assert.Equal((source, expectedTarget), Assert.Single(files.MoveRequests));
            Assert.Empty(files.CopyRequests);
            Assert.Equal(DragDropEffects.Move, drop.DragEffects);
            Assert.DoesNotContain("folder-drop-target", row.Classes);
            await Task.Delay(700);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(scenario is "hover" or "continue-blank" or "continue-child" ? desktop : root, vm.CurrentPath);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previousServices);
            Directory.Delete(root, true);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SidebarFileDropMovesRealFilesAndPreservesContent(bool hoverFirst)
    {
        using var theme = new FastListTestTheme();
        Assert.NotNull(RuntimePaths.TestRoot); // Run this integration check through run-isolated.sh.
        RuntimePaths.PrepareTestRoot();
        var files = new MacFileService();
        var sourceFolder = Directory.CreateDirectory(Path.Combine(files.HomeDirectory, "sidebar-real-" + Guid.NewGuid().ToString("N")));
        var source = Path.Combine(sourceFolder.FullName, "payload-" + Guid.NewGuid().ToString("N") + ".txt");
        var target = Path.Combine(files.HomeDirectory, "Desktop", Path.GetFileName(source));
        await File.WriteAllTextAsync(source, "原文 / sidebar file move");
        using var vm = CreateViewModel(files);
        await vm.NavigateToAsync(sourceFolder.FullName);
        using var services = new ServiceCollection().AddSingleton<IFileService>(files).BuildServiceProvider();
        var previousServices = App.Services;
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        var sidebar = new FinderSidebarView { DataContext = vm };
        sidebar.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        var window = new Window { Width = 260, Height = 850, Content = sidebar };
        try
        {
            window.Show();
            var row = sidebar.FindControl<Border>("DesktopItem")!;
            Point point = default;
            await WaitForIssue11Async(() =>
            {
                RenderIssue11(window);
                point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), sidebar)!.Value;
                var hit = sidebar.InputHitTest(point) as Visual;
                return hit == row || hit?.GetVisualAncestors().Contains(row) == true;
            });
            using var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(
                await window.StorageProvider.TryGetFileFromPathAsync(new Uri(source)))));
            sidebar.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, sidebar, point, KeyModifiers.None));
            if (hoverFirst)
                await WaitForIssue11Async(() => vm.CurrentPath == Path.GetDirectoryName(target) && !vm.IsDirectoryLoading);
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(target));
            var drop = new DragEventArgs(DragDrop.DropEvent, data, sidebar, point, KeyModifiers.None);
            sidebar.RaiseEvent(drop);
            await WaitForIssue11Async(() => File.Exists(target) && !File.Exists(source) && !vm.IsDirectoryLoading);
            Assert.Equal("原文 / sidebar file move", await File.ReadAllTextAsync(target));
            Assert.Equal(DragDropEffects.Move, drop.DragEffects);
            Assert.DoesNotContain(vm.Entries, entry => entry.FullPath == source);
            if (hoverFirst) Assert.Contains(vm.Entries, entry => entry.FullPath == target);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previousServices);
            sourceFolder.Delete(true);
            File.Delete(target);
        }
    }
}
