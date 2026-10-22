using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MacExplorer.Controls;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileDrop")]
public sealed class CopyOnlyFileDropTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CopyOnlyChatExportIsCopiedToReleasePositionAndSourceIsPreserved(bool grid, bool onFolder)
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("chat-export-drop-").FullName;
        var source = Path.Combine(root, "chat-export.txt");
        var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(target, "child")).FullName;
        await File.WriteAllTextAsync(source, "chat attachment");
        var files = new MacFileService();
        using var vm = CreateViewModel(files, navigation: new NavigationViewModel(files)
            { CurrentPath = target, IsHomePage = false });
        vm.SetViewMode(grid ? ViewMode.Grid : ViewMode.List);
        await vm.RefreshAsync();
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        using var services = new ServiceCollection().AddSingleton<IFileService>(files)
            .AddSingleton<IDragDropService>(new MacDragDropBridge(files, new FakeDirectoryChangeNotifier()))
            .BuildServiceProvider();
        var previousServices = App.Services;
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        try
        {
            window.Show();
            RenderIssue11(window);
            var surface = view.FindControl<Grid>("InteractionSurface")!;
            var list = view.FindControl<FastFileList>("FastList")!;
            var folderPoint = list.TranslatePoint(list.RowBounds(list.IndexOfPath(child)).Center, surface)!.Value;
            var release = onFolder ? folderPoint : new Point(40, surface.Bounds.Height - 40);
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(
                await window.StorageProvider.TryGetFileFromPathAsync(new Uri(source)))));
            var over = new DragEventArgs(DragDrop.DragOverEvent, transfer, surface, release, KeyModifiers.None)
                { DragEffects = DragDropEffects.Copy };
            surface.RaiseEvent(over);
            Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            var drop = new DragEventArgs(DragDrop.DropEvent, transfer, surface, release, KeyModifiers.None)
                { DragEffects = DragDropEffects.Copy };
            surface.RaiseEvent(drop);
            var saved = Path.Combine(onFolder ? child : target, "chat-export.txt");
            await WaitForIssue11Async(() => File.Exists(saved));
            Assert.Equal("chat attachment", await File.ReadAllTextAsync(saved));
            Assert.Equal("chat attachment", await File.ReadAllTextAsync(source));
            Assert.Equal(DragDropEffects.Copy, drop.DragEffects);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previousServices);
            Directory.Delete(root, true);
        }
    }
}

[Trait("Kind", "Unit")]
[Trait("Area", "FileDrop")]
public sealed class FileDropSourceEffectsTests
{
    [Theory]
    [InlineData(DragDropEffects.Copy, DragDropEffects.Copy)]
    [InlineData(DragDropEffects.Move, DragDropEffects.Move)]
    [InlineData(DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.Move)]
    [InlineData(DragDropEffects.Link, DragDropEffects.None)]
    [InlineData(DragDropEffects.None, DragDropEffects.None)]
    public void LocalFileDropUsesOnlyAnOperationOfferedBySource(DragDropEffects allowed, DragDropEffects expected)
        => Assert.Equal(expected, FileDropPolicy.GetEffect(["/source/chat-export.png"], "/target", allowed));

    [Fact]
    public void MoveOnlySourceCannotBeUsedForRemoteCopy()
        => Assert.Equal(DragDropEffects.None,
            FileDropPolicy.GetEffect(["/source/file"], "__remote:test:/target", DragDropEffects.Move));
}
