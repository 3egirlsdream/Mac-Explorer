using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using Microsoft.Extensions.DependencyInjection;
using MacExplorer.Models;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FileDragKeepsCaptureOutsideItemAndCancelsRenameWhileDataIsPending(bool grid, bool multiple)
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/DragStartTests"),
            sortFilter: new SortFilterViewModel { ViewMode = grid ? ViewMode.Grid : ViewMode.List });
        vm.Entries.Add(new FileSystemEntry { FullPath = "/tmp/DragStartTests/a.txt", Name = "a.txt" });
        vm.Entries.Add(new FileSystemEntry { FullPath = "/tmp/DragStartTests/folder", Name = "folder", IsDirectory = true });
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        var pending = new TaskCompletionSource<IReadOnlyList<IStorageItem>>();
        try
        {
            window.Show();
            vm.SelectEntry(vm.Entries[0]);
            if (multiple) vm.SelectEntry(vm.Entries[1], true);
            Dispatcher.UIThread.RunJobs();
            var fast = view.FindControl<FastFileList>("FastList")!;
            var hit = fast;
            var start = hit.TranslatePoint(fast.NameBounds(0).Center, window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            var press = Assert.IsType<PointerPressedEventArgs>(DragField("_dragPointerEvent").GetValue(view));
            Assert.Same(hit, press.Pointer.Captured);
            Assert.Equal(multiple ? 2 : 1, vm.SelectedEntries.Count);
            if (!multiple) Assert.NotNull(DragField("_renameDelayCts").GetValue(view));
            DragField("_dragStorageItemsTask").SetValue(view, pending.Task);

            // No repeated button flag: macOS capture can report this exact sequence.
            var end = new Point(window.Width + 20, start.Y + 40);
            window.MouseMove(end);
            Assert.Null(DragField("_renameDelayCts").GetValue(view));
            Assert.Same(press, DragField("_dragPointerEvent").GetValue(view));
            Assert.Same(hit, press.Pointer.Captured);
            Assert.Equal(multiple ? 2 : 1, vm.SelectedEntries.Count);

            window.MouseUp(end, MouseButton.Left);
            Assert.Null(press.Pointer.Captured);
            Assert.Null(DragField("_dragPointerEvent").GetValue(view));
            Assert.Equal(multiple ? 2 : 1, vm.SelectedEntries.Count);
        }
        finally
        {
            pending.TrySetResult([]);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FileDragCaptureLossCancelsPendingRenameAndDrag()
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/DragStartTests"));
        vm.Entries.Add(new FileSystemEntry { FullPath = "/tmp/DragStartTests/a.txt", Name = "a.txt" });
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            vm.SelectEntry(vm.Entries[0]);
            Dispatcher.UIThread.RunJobs();
            var fast = view.FindControl<FastFileList>("FastList")!;
            window.MouseDown(fast.TranslatePoint(fast.NameBounds(0).Center, window)!.Value, MouseButton.Left);
            var press = Assert.IsType<PointerPressedEventArgs>(DragField("_dragPointerEvent").GetValue(view));
            press.Pointer.Capture(window);
            Assert.Null(DragField("_dragPointerEvent").GetValue(view));
            Assert.Null(DragField("_renameDelayCts").GetValue(view));
            Assert.Same(window, press.Pointer.Captured);
            press.Pointer.Capture(null);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void FileDragPathsDoNotRequireFileSystemAccessAndExcludeVirtualEntries()
    {
        var path = $"/Volumes/Disconnected-{Guid.NewGuid():N}/file.txt";
        Assert.Equal(new[] { path, "/tmp/folder/" }, FileListView.GetLocalDragPaths([
            new() { FullPath = path }, new() { FullPath = path },
            new() { FullPath = "/tmp/folder", IsDirectory = true },
            new() { FullPath = "/tmp/virtual", IsVirtual = true },
            new() { FullPath = "sftp://server/file" }, new() { FullPath = "relative" }
        ]));
    }

    [AvaloniaFact]
    public async Task FileDragFolderUrlsAreNormalizedBeforeMoveUsesTheFileName()
    {
        var root = Path.Combine(Path.GetTempPath(), $"drag-folder-url-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var window = new Window();
        try
        {
            window.Show();
            using var folder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(root + "/"));
            Assert.NotNull(folder);
            using var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(folder));
            var path = Assert.Single(FileListView.GetDroppedPaths(data));
            Assert.Equal(root, path);
            Assert.Equal(Path.GetFileName(root), Path.GetFileName(path));
        }
        finally
        {
            window.Close();
            Directory.Delete(root);
        }
    }

    [AvaloniaFact]
    public async Task FileDragFolderDropMovesItsContentsOnDisk()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("drag-folder-move-").FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "nested.txt"), "folder drag");
        var files = new MacFileService();
        using var vm = CreateViewModel(files, navigation: new NavigationViewModel(files)
            { CurrentPath = target, IsHomePage = false });
        await vm.RefreshAsync();
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        using var services = new ServiceCollection().AddSingleton<IFileService>(files).BuildServiceProvider();
        var previousServices = App.Services;
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        try
        {
            window.Show();
            RenderIssue11(window);
            var surface = view.FindControl<Grid>("InteractionSurface")!;
            using var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(
                await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(source + "/")))));
            var drop = new DragEventArgs(DragDrop.DropEvent, data, surface,
                new Point(40, surface.Bounds.Height - 40), KeyModifiers.None);
            surface.RaiseEvent(drop);
            await WaitForIssue11Async(() => !Directory.Exists(source));
            Assert.Equal("folder drag", await File.ReadAllTextAsync(Path.Combine(target, "source", "nested.txt")));
            Assert.Equal(DragDropEffects.Move, drop.DragEffects);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previousServices);
            Directory.Delete(root, true);
        }
    }

    private static FieldInfo DragField(string name)
        => typeof(FileListView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
