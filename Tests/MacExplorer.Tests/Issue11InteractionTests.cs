using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
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
    public void Issue11PreferencesDefaultSafelyAndUpdateExistingTabs()
    {
        var settings = new StartupSettings();
        var files = new FakeFileService("/tmp/Issue11");
        using var first = CreateViewModel(files, settingsService: settings);
        using var existingTab = CreateViewModel(files, settingsService: settings);
        Assert.True(first.ConfirmBeforeTrash);
        Assert.False(first.DoubleClickEmptyAreaGoUp);
        Assert.Empty(settings.Writes);

        first.ConfirmBeforeTrash = false;
        first.DoubleClickEmptyAreaGoUp = true;
        Assert.False(existingTab.ConfirmBeforeTrash);
        Assert.True(existingTab.DoubleClickEmptyAreaGoUp);
        using var newTab = CreateViewModel(files, settingsService: settings);
        Assert.False(newTab.ConfirmBeforeTrash);
        Assert.True(newTab.DoubleClickEmptyAreaGoUp);
        existingTab.ConfirmBeforeTrash = true;
        existingTab.DoubleClickEmptyAreaGoUp = false;
        Assert.True(first.ConfirmBeforeTrash);
        Assert.False(first.DoubleClickEmptyAreaGoUp);
    }

    [AvaloniaFact]
    public void Issue11PreferencesSurviveSettingsServiceRestart()
    {
        var root = Directory.CreateTempSubdirectory("issue11-settings-").FullName;
        try
        {
            var factory = new DatabaseConnectionFactory(Path.Combine(root, "settings.db"));
            using (var connection = factory.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
                command.ExecuteNonQuery();
            }
            using (var settings = new SettingsService(factory))
            using (var vm = CreateViewModel(new FakeFileService(root), settingsService: settings))
            {
                vm.ConfirmBeforeTrash = false;
                vm.DoubleClickEmptyAreaGoUp = true;
            }
            using var reloaded = new SettingsService(factory);
            using var reopened = CreateViewModel(new FakeFileService(root), settingsService: reloaded);
            Assert.False(reopened.ConfirmBeforeTrash);
            Assert.True(reopened.DoubleClickEmptyAreaGoUp);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Issue11DeleteConfirmationUsesSnapshotAndCancelIsSafe()
    {
        var files = new FakeFileService("/tmp/Issue11");
        var first = new FileSystemEntry { FullPath = "/tmp/Issue11/a.txt", Name = "a.txt" };
        var second = new FileSystemEntry { FullPath = "/tmp/Issue11/b.txt", Name = "b.txt" };
        files.Seed(first);
        files.Seed(second);
        using var vm = CreateViewModel(files);
        vm.SetSelection([first]);
        await vm.RequestDeleteSelectedAsync();
        Assert.True(vm.IsDeleteConfirmDialogVisible);
        Assert.Empty(files.DeleteRequests);
        vm.CancelDeleteConfirmDialog();
        await vm.ConfirmDeleteSelectedAsync();
        Assert.Empty(files.DeleteRequests);

        await vm.RequestDeleteSelectedAsync();
        vm.SetSelection([second]);
        // Repeated requests must not replace the selection shown in the dialog.
        await vm.RequestDeleteSelectedAsync();
        await vm.ConfirmDeleteSelectedAsync();
        Assert.Equal((first.FullPath, true), Assert.Single(files.DeleteRequests));
        Assert.NotNull(await files.GetEntryAsync(second.FullPath));
        Assert.False(vm.IsDeleteConfirmDialogVisible);
    }

    [AvaloniaFact]
    public async Task Issue11DeleteWithoutConfirmationIsImmediateAndNotReentrant()
    {
        var files = new FakeFileService("/tmp/Issue11");
        var first = new FileSystemEntry { FullPath = "/tmp/Issue11/a.txt", Name = "a.txt" };
        var second = new FileSystemEntry { FullPath = "/tmp/Issue11/b.txt", Name = "b.txt" };
        files.Seed(first);
        files.Seed(second);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeDelete = () => blocked.Task;
        using var vm = CreateViewModel(files);
        vm.ConfirmBeforeTrash = false;
        vm.SetSelection([first]);
        var deleting = vm.RequestDeleteSelectedAsync();
        try
        {
            Assert.False(vm.IsDeleteConfirmDialogVisible);
            Assert.Single(files.DeleteRequests);
            vm.SetSelection([second]);
            await vm.RequestDeleteSelectedAsync();
            Assert.Single(files.DeleteRequests);
        }
        finally { blocked.TrySetResult(); }
        await deleting;
        Assert.NotNull(await files.GetEntryAsync(second.FullPath));
        Assert.Null(await files.GetEntryAsync(first.FullPath));
    }

    [AvaloniaTheory]
    [InlineData("keyboard", true)]
    [InlineData("keyboard", false)]
    [InlineData("toolbar", true)]
    [InlineData("toolbar", false)]
    [InlineData("menu", true)]
    [InlineData("menu", false)]
    public async Task Issue11AllDeleteEntrypointsHonorPreference(string entrypoint, bool confirm)
    {
        var files = new FakeFileService("/tmp/Issue11");
        var entry = new FileSystemEntry { FullPath = "/tmp/Issue11/a.txt", Name = "a.txt" };
        files.Seed(entry);
        using var vm = CreateViewModel(files);
        vm.ConfirmBeforeTrash = confirm;
        vm.Entries.Add(entry);
        vm.SetSelection([entry]);
        if (entrypoint == "keyboard")
        {
            var view = new FileListView { DataContext = vm };
            Assert.True(view.TryHandleFileShortcut(new KeyEventArgs
                { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete }));
        }
        else if (entrypoint == "toolbar")
        {
            var toolbar = new FinderToolbar { DataContext = vm };
            toolbar.FindControl<Button>("DeleteButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        else
        {
            var actions = await vm.LoadCompleteFileContextMenuAsync(entry);
            await Assert.Single(actions, action => action.Label == "删除").Execute!();
        }
        Assert.Equal(confirm, vm.IsDeleteConfirmDialogVisible);
        if (confirm) Assert.Empty(files.DeleteRequests);
        else
        {
            await WaitForIssue11Async(() => vm.Entries.Count == 0 && !vm.IsDirectoryLoading && files.DeleteRequests.Count == 1);
            Assert.Equal((entry.FullPath, true), Assert.Single(files.DeleteRequests));
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue11RemoteDeleteAlwaysRequiresConfirmation(bool mixed)
    {
        var files = new FakeFileService("/tmp/Issue11");
        using var vm = CreateViewModel(files);
        vm.ConfirmBeforeTrash = false;
        var entries = new List<FileSystemEntry>
        {
            new() { FullPath = VirtualPath.BuildRemotePath("test", "/a.txt"), Name = "a.txt" }
        };
        if (mixed) entries.Add(new FileSystemEntry { FullPath = "/tmp/Issue11/b.txt", Name = "b.txt" });
        vm.SetSelection(entries);
        await vm.RequestDeleteSelectedAsync();
        Assert.True(vm.IsDeleteConfirmDialogVisible);
        Assert.True(vm.DeleteConfirmIncludesRemoteFiles);
        Assert.Equal(entries.Count, vm.DeleteConfirmItemCount);
        Assert.Empty(files.DeleteRequests);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Issue11BackgroundDoubleClickIsOptInInEveryFileView(bool grid, bool grouped)
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("issue11-background-").FullName;
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        var files = new FakeFileService(child);
        files.Seed(new FileSystemEntry { FullPath = Path.Combine(child, "a.txt"), Name = "a.txt", Extension = ".txt" });
        using var vm = CreateViewModel(files);
        vm.SetViewMode(grid ? ViewMode.Grid : ViewMode.List);
        vm.GroupField = grouped ? GroupField.Type : GroupField.None;
        await vm.RefreshAsync();
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var surface = view.FindControl<Grid>("FileScroll")!;
            var blank = surface.TranslatePoint(new Point(24, 200), window)!.Value;
            DoubleClickIssue11(window, blank);
            Assert.Equal(child, vm.CurrentPath);
            vm.DoubleClickEmptyAreaGoUp = true;
            // Move away from the preceding click sequence to reset click count.
            DoubleClickIssue11(window, blank + new Vector(40, 0));
            await WaitForIssue11Async(() => vm.CurrentPath == root && !vm.IsDirectoryLoading);
            Assert.Equal(root, vm.CurrentPath);
            Assert.False(view.FindControl<Border>("SelectionMarquee")!.IsVisible);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Issue11BackgroundPreferenceDoesNotOverrideFolderDoubleClick()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("issue11-folder-").FullName;
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        var files = new FakeFileService(root);
        files.Seed(new FileSystemEntry { FullPath = child, Name = "child", IsDirectory = true });
        using var vm = CreateViewModel(files);
        vm.DoubleClickEmptyAreaGoUp = true;
        await vm.RefreshAsync();
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var host = view.FindControl<FastFileList>("FastList")!;
            var point = host.TranslatePoint(new Point(750, 14), window)!.Value;
            DoubleClickIssue11(window, point);
            await WaitForIssue11Async(() => vm.CurrentPath == child && !vm.IsDirectoryLoading);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue11GroupHeaderDoubleClickIsNotBackground(bool grid)
    {
        using var theme = new FastListTestTheme();
        var files = new FakeFileService("/tmp/Issue11");
        files.Seed(new FileSystemEntry { FullPath = "/tmp/Issue11/a.txt", Name = "a.txt", Extension = ".txt" });
        using var vm = CreateViewModel(files);
        vm.SetViewMode(grid ? ViewMode.Grid : ViewMode.List);
        vm.GroupField = GroupField.Type;
        vm.DoubleClickEmptyAreaGoUp = true;
        await vm.RefreshAsync();
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            RenderIssue11(window);
            var header = view.FindControl<FastFileList>("FastList")!;
            DoubleClickIssue11(window, header.TranslatePoint(new Point(50, 10), window)!.Value);
            Assert.Equal(files.HomeDirectory, vm.CurrentPath);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Issue11FileDragActivatesHoveredTabWithoutDroppingOrLosingSourceSelection()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("issue11-tab-").FullName;
        var file = Path.Combine(root, "a.txt");
        await File.WriteAllTextAsync(file, "unchanged");
        var files = new FakeFileService(root);
        using var first = CreateViewModel(files);
        using var second = CreateViewModel(files);
        first.SetSelection([new FileSystemEntry { FullPath = file, Name = "a.txt" }]);
        var vm = new MainWindowViewModel(first);
        var target = vm.AddTab(second, select: false);
        var tabs = new ListBox { ItemsSource = vm.Tabs };
        using var binding = tabs.Bind(ListBox.SelectedItemProperty,
            new Binding(nameof(MainWindowViewModel.SelectedTab)) { Source = vm, Mode = BindingMode.TwoWay });
        DragDrop.SetAllowDrop(tabs, true);
        var canActivate = false;
        tabs.AddHandler(DragDrop.DragOverEvent, (_, e) => FileTabDragNavigation.DragOver(tabs, e, canActivate));
        tabs.AddHandler(DragDrop.DropEvent, (_, e) => FileTabDragNavigation.Drop(e));
        var window = new Window { Width = 700, Height = 400, Content = tabs };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var row = tabs.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext == target);
            var center = new Point(row.Bounds.Width / 2, row.Bounds.Height / 2);
            using var transfer = new DataTransfer();
            var storage = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(file));
            transfer.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(storage)));
            RenderIssue11(window);
            var over = new DragEventArgs(DragDrop.DragOverEvent, transfer, row, center, KeyModifiers.None);
            row.RaiseEvent(over);
            Assert.Same(first, vm.FileList); // modal/overlay guard
            canActivate = true;
            row.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, transfer, row, center, KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(target, tabs.SelectedItem);
            Assert.Same(target, vm.SelectedTab);
            Assert.Same(second, vm.FileList);
            Assert.Same(target, Assert.Single(vm.VisiblePanes));
            Assert.Equal(file, Assert.Single(first.SelectedEntries).FullPath);
            var drop = new DragEventArgs(DragDrop.DropEvent, transfer, row, center, KeyModifiers.None);
            row.RaiseEvent(drop);
            Assert.True(drop.Handled);
            Assert.Equal(DragDropEffects.None, drop.DragEffects);
            Assert.Equal(0, files.CopyCalls);
            Assert.Equal(0, files.MoveCalls);
            Assert.Equal("unchanged", await File.ReadAllTextAsync(file));

            vm.SelectedTab = vm.Tabs[0];
            using var text = new DataTransfer();
            text.Add(DataTransferItem.CreateText("not a file"));
            row.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, text, row, center, KeyModifiers.None));
            Assert.Same(first, vm.FileList);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    public static IEnumerable<object[]> Issue11FileAreaDropCases()
    {
        foreach (var grid in new[] { false, true })
        foreach (var onFolder in new[] { false, true })
        foreach (var sameDirectory in new[] { false, true })
        foreach (var modifiers in new[] { KeyModifiers.None, KeyModifiers.Alt })
            yield return [grid, onFolder, sameDirectory, modifiers];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Issue11FileAreaDropCases))]
    public async Task Issue11FileAreaDropMovesToFinalPositionAndIgnoresSourceDirectory(
        bool grid, bool onFolder, bool sameDirectory, KeyModifiers modifiers)
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("issue11-drop-").FullName;
        var target = Path.Combine(root, "target");
        var child = Path.Combine(target, "child");
        Directory.CreateDirectory(child);
        var source = Path.Combine(sameDirectory ? target : root, "a.txt");
        await File.WriteAllTextAsync(source, "original");
        var sourceEntry = new FileSystemEntry { FullPath = source, Name = "a.txt" };
        var childEntry = new FileSystemEntry { FullPath = child, Name = "child", IsDirectory = true };
        var files = new FakeFileService(target);
        files.Seed(sourceEntry);
        files.Seed(childEntry);
        using var vm = CreateViewModel(files);
        vm.SetViewMode(grid ? ViewMode.Grid : ViewMode.List);
        await vm.RefreshAsync();
        // Cross-tab drops use the transfer, not the target tab's selection.
        vm.SetSelection([sameDirectory ? sourceEntry : childEntry]);
        using var services = new ServiceCollection()
            .AddSingleton<IFileService>(files).BuildServiceProvider();
        var previousServices = App.Services;
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var surface = view.FindControl<Grid>("InteractionSurface")!;
            using var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(
                await window.StorageProvider.TryGetFileFromPathAsync(new Uri(source)))));
            RenderIssue11(window);
            Point GetFolderPoint()
            {
                var list = view.FindControl<FastFileList>("FastList")!;
                return list.TranslatePoint(list.RowBounds(list.IndexOfPath(child)).Center, surface)!.Value;
            }
            var folderPoint = GetFolderPoint();
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, surface, folderPoint, modifiers);
            surface.RaiseEvent(over);
            Assert.Equal(DragDropEffects.Move, over.DragEffects);
            Assert.Empty(files.MoveRequests);
            Assert.Empty(files.CopyRequests);
            RenderIssue11(window);
            var release = onFolder ? folderPoint : new Point(40, surface.Bounds.Height - 40);
            var finalOver = new DragEventArgs(DragDrop.DragOverEvent, data, surface, release, modifiers);
            surface.RaiseEvent(finalOver);
            var noOp = sameDirectory && !onFolder;
            Assert.Equal(noOp ? DragDropEffects.None : DragDropEffects.Move, finalOver.DragEffects);
            var drop = new DragEventArgs(DragDrop.DropEvent, data, surface, release, modifiers);
            surface.RaiseEvent(drop);
            if (noOp)
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(files.MoveRequests);
                Assert.False(vm.IsMoveConfirmDialogVisible);
                Assert.False(vm.IsPasteConfirmDialogVisible);
                Assert.Equal("original", await File.ReadAllTextAsync(source));
                Assert.Equal(new[] { source }, Directory.GetFiles(target));
            }
            else
            {
                await WaitForIssue11Async(() => files.MoveCalls == 1 && !vm.IsDirectoryLoading);
                Assert.Equal((source, onFolder ? child : target), Assert.Single(files.MoveRequests));
            }
            Assert.Empty(files.CopyRequests);
            Assert.True(drop.Handled);
            Assert.Equal(noOp ? DragDropEffects.None : DragDropEffects.Move, drop.DragEffects);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previousServices);
            Directory.Delete(root, true);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Issue11SftpTransfersKeepCopyBehavior(bool remoteSource, bool remoteTarget)
    {
        var root = Directory.CreateTempSubdirectory("issue11-sftp-copy-").FullName;
        try
        {
            var localSource = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(localSource, "original");
            var source = remoteSource ? "__remote:test:/source.txt" : localSource;
            var target = remoteTarget ? "__remote:test:/target" : root;
            var files = new FakeFileService(root);
            files.Seed(new FileSystemEntry { FullPath = source, Name = "source.txt" });
            using var vm = CreateViewModel(files);
            var bridge = new MacDragDropBridge(files, new FakeDirectoryChangeNotifier());
            bridge.Register(vm);

            Assert.Equal(DragDropEffects.Copy, FileDropPolicy.GetEffect([source], target));
            Assert.True(await bridge.DropFilesAsync([source], target, forceCopy: true, forceMove: false));
            Assert.Equal((source, target), Assert.Single(files.CopyRequests));
            Assert.Empty(files.MoveRequests);
            Assert.Empty(files.DeleteRequests);
            Assert.NotNull(await files.GetEntryAsync(source));
            Assert.Equal("original", await File.ReadAllTextAsync(localSource));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void DoubleClickIssue11(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void RenderIssue11(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForIssue11Async(Func<bool> ready)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(ready(), "The interaction did not complete before timeout.");
    }
}

public class FileDropPolicyTests
{
    [Theory]
    [InlineData("/source/folder", "/target", DragDropEffects.Move)]
    [InlineData("/source/folder", "/source", DragDropEffects.None)]
    [InlineData("/source/folder/", "/source/", DragDropEffects.None)]
    [InlineData("/source/folder", "/source/folder", DragDropEffects.None)]
    [InlineData("/source/folder", "/source/folder/child", DragDropEffects.None)]
    [InlineData("/source/folder", "/source/folder-other", DragDropEffects.Move)]
    [InlineData("/source/folder", "/source/../source/folder/child", DragDropEffects.None)]
    [InlineData("/source/file", "__remote:test:/target", DragDropEffects.Copy)]
    [InlineData("__remote:test:/source/file", "/target", DragDropEffects.Copy)]
    [InlineData("__remote:test:/source/file", "__remote:test:/target", DragDropEffects.Copy)]
    [InlineData("__remote:test:/source", "__remote:test:/source/child", DragDropEffects.None)]
    [InlineData("__remote:first:/source", "__remote:second:/source", DragDropEffects.Copy)]
    [InlineData("/source/file", "", DragDropEffects.None)]
    public void FileDropChoosesLocalMoveOrRemoteCopy(string source, string target, DragDropEffects effect)
        => Assert.Equal(effect, FileDropPolicy.GetEffect([source], target));
}
