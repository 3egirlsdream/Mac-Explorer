using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task DeliveryPanelLoadsSharedStylesAndSwitchesEntriesWithoutChangingMainList()
    {
        using var fixture = new FileDeliveryTests.Fixture();
        using var service = new MacExplorer.Services.Impl.FileDeliveryService(fixture.Settings, fixture.Tags);
        foreach (var entry in service.Preferences.Entries.ToArray()) service.Remove(entry.Id);
        service.AddFolder(fixture.Root);
        var fileService = new FakeFileService(fixture.Root);
        fileService.Seed(new() { Name = "keep.txt", FullPath = Path.Combine(fixture.Root, "keep.txt") });
        using var panelVm = CreateViewModel(fileService, browseOnly: true);
        using var mainVm = CreateViewModel(fileService);
        var window = new FileDeliveryWindow(service, fixture.Tags, panelVm);
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/FileDeliveryStyles.axaml")));
        try
        {
            window.Show();
            await window.ResumeAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Single(window.FindControl<StackPanel>("EntryTabs")!.Children);
            Assert.Single(panelVm.Entries);
            Assert.Empty(mainVm.Entries);
            Assert.DoesNotContain("无法访问", window.FindControl<TextBlock>("StatusText")!.Text ?? "");
            window.Suspend();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DeliveryAddMenuReferencesExistingFavoriteAndReloadsRenamedChoices()
    {
        using var fixture = new FileDeliveryTests.Fixture();
        var tag = await fixture.Tags.CreateTagAsync("测试素材");
        using var service = new MacExplorer.Services.Impl.FileDeliveryService(fixture.Settings, fixture.Tags);
        using var vm = CreateViewModel(new FakeFileService(fixture.Root), browseOnly: true);
        var window = new FileDeliveryWindow(service, fixture.Tags, vm);
        try
        {
            window.Show();
            var add = window.FindControl<Button>("AddEntryButton")!;
            add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var menu = add.ContextMenu!;
            Assert.True(menu.IsOpen);
            Assert.True(window.HasOpenPopup);
            var favorites = Assert.Single(menu.Items.OfType<MenuItem>(), i => Equals(i.Header, "添加收藏夹"));
            var choice = Assert.Single(favorites.Items.OfType<MenuItem>(), i => Equals(i.Header, tag.Name));
            choice.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Contains(service.Preferences.Entries, e => e.Location == tag.VirtualPath);
            Assert.Contains(await fixture.Tags.GetSidebarTagsAsync(), t => t.Name == tag.Name);
            menu.Close();
            await fixture.Tags.RenameTagAsync(tag, "已改名素材");
            add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            favorites = Assert.Single(add.ContextMenu!.Items.OfType<MenuItem>(), i => Equals(i.Header, "添加收藏夹"));
            Assert.Contains(favorites.Items.OfType<MenuItem>(), i => Equals(i.Header, "已改名素材"));
            Assert.DoesNotContain(favorites.Items.OfType<MenuItem>(), i => Equals(i.Header, "测试素材"));
            window.Suspend();
            Assert.False(add.ContextMenu.IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DeliveryTabsShowLocationAndTagIcons()
    {
        using var fixture = new FileDeliveryTests.Fixture();
        var desktop = Directory.CreateDirectory(Path.Combine(fixture.Root, "Desktop")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(fixture.Root, "Other")).FullName;
        var tag = await fixture.Tags.CreateTagAsync("素材");
        using var service = new MacExplorer.Services.Impl.FileDeliveryService(fixture.Settings, fixture.Tags);
        foreach (var entry in service.Preferences.Entries.ToArray()) service.Remove(entry.Id);
        service.AddFolder(desktop);
        service.AddFolder(other);
        service.AddTag(tag);
        using var vm = CreateViewModel(new FakeFileService(fixture.Root), browseOnly: true);
        var window = new FileDeliveryWindow(service, fixture.Tags, vm);
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var tabs = window.FindControl<StackPanel>("EntryTabs")!;
            foreach (var (name, iconData) in new[]
                     {
                         ("Desktop", Assets.Icons.Desktop),
                         ("Other", Assets.Icons.Folder),
                         (tag.Name, Assets.Icons.Tag)
                     })
            {
                var tab = Assert.Single(tabs.Children.OfType<Button>(), button =>
                    Assert.IsType<StackPanel>(button.Content).Children.OfType<TextBlock>().Single().Text == name);
                var content = Assert.IsType<StackPanel>(tab.Content);
                var icon = Assert.IsType<PathIcon>(content.Children[0]);
                Assert.True(icon.IsEffectivelyVisible);
                Assert.Equal(Geometry.Parse(iconData).ToString(), icon.Data?.ToString());
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DeliveryTabDeleteRemovesOnlyTheEntryAndClosesItsMenu()
    {
        using var fixture = new FileDeliveryTests.Fixture();
        var tag = await fixture.Tags.CreateTagAsync("保留收藏");
        var file = Path.Combine(fixture.Root, "保留文件.txt");
        await File.WriteAllTextAsync(file, "keep");
        using var service = new MacExplorer.Services.Impl.FileDeliveryService(fixture.Settings, fixture.Tags);
        foreach (var entry in service.Preferences.Entries.ToArray()) service.Remove(entry.Id);
        service.AddFolder(fixture.Root);
        service.AddTag(tag);
        using var vm = CreateViewModel(new FakeFileService(fixture.Root), browseOnly: true);
        var window = new FileDeliveryWindow(service, fixture.Tags, vm);
        try
        {
            window.Show();
            await window.ResumeAsync();
            var tabs = window.FindControl<StackPanel>("EntryTabs")!;
            foreach (var name in new[] { tag.Name, Path.GetFileName(fixture.Root) })
            {
                var tab = Assert.Single(tabs.Children.OfType<Button>(), b =>
                    Assert.IsType<StackPanel>(b.Content).Children.OfType<TextBlock>().Single().Text == name);
                var menu = tab.ContextMenu!;
                menu.Open(tab);
                Assert.True(window.HasOpenPopup);
                Assert.Single(menu.Items.OfType<MenuItem>()).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.False(menu.IsOpen);
                Assert.DoesNotContain(service.Preferences.Entries, e => e.Name == name);
            }
            Assert.Empty(tabs.Children);
            Assert.Contains(await fixture.Tags.GetSidebarTagsAsync(), t => t.Name == tag.Name);
            Assert.Equal("keep", await File.ReadAllTextAsync(file));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DeliveryBrowseModeBlocksMutationCommandsAndMenus()
    {
        var files = new FakeFileService("/tmp/DeliveryTests");
        var entry = new FileSystemEntry { FullPath = "/tmp/DeliveryTests/keep.txt", Name = "keep.txt" };
        files.Seed(entry);
        using var vm = CreateViewModel(files, browseOnly: true);
        vm.Entries.Add(entry);
        vm.SelectEntry(entry);
        var renameRequested = false;
        vm.RenameRequested += _ => renameRequested = true;
        vm.RequestRename(entry);
        Assert.False(await vm.RenameEntryAsync(entry, "changed.txt"));
        await vm.CreateNewFileCommand.ExecuteAsync(".txt");
        await vm.CreateNewFolderCommand.ExecuteAsync(null);
        await vm.RequestDeleteSelectedAsync();
        await vm.ConfirmDeleteSelectedAsync();
        await vm.PasteCommand.ExecuteAsync(null);
        vm.CutSelectedCommand.Execute(null);
        await vm.ShowFileContextMenuAsync(entry, 0, 0);
        await vm.ShowBackgroundContextMenuAsync(0, 0);
        Assert.Empty(await vm.LoadCompleteFileContextMenuAsync(entry));
        Assert.False(renameRequested);
        Assert.False(vm.IsDeleteConfirmDialogVisible);
        Assert.False(vm.IsContextMenuVisible);
        Assert.Empty(vm.CutPaths);
        Assert.Contains(await files.GetDirectoryContentsAsync(files.HomeDirectory), e => e.FullPath == entry.FullPath);
        Assert.Single(await files.GetDirectoryContentsAsync(files.HomeDirectory));
        Assert.Equal(DragDropEffects.Copy, new FileListView { DataContext = vm }.AllowedDragEffects);
        using var normal = CreateViewModel(files);
        Assert.Equal(DragDropEffects.Copy | DragDropEffects.Move, new FileListView { DataContext = normal }.AllowedDragEffects);
    }

    [AvaloniaTheory]
    [InlineData(Key.Back, KeyModifiers.Meta)]
    [InlineData(Key.Delete, KeyModifiers.None)]
    [InlineData(Key.F2, KeyModifiers.None)]
    [InlineData(Key.X, KeyModifiers.Meta)]
    [InlineData(Key.V, KeyModifiers.Meta)]
    [InlineData(Key.N, KeyModifiers.Meta | KeyModifiers.Shift)]
    public void DeliveryBrowseModeConsumesModificationShortcuts(Key key, KeyModifiers modifiers)
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/DeliveryTests"), browseOnly: true);
        var view = new FileListView { DataContext = vm };
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = view };
        Assert.True(view.TryHandleFileShortcut(args));
        Assert.True(args.Handled);
        Assert.False(vm.IsDeleteConfirmDialogVisible);
    }

    [AvaloniaFact]
    public async Task DeliveryOpensFilesInPreviewAndRejectsVirtualNavigation()
    {
        var preview = new DeliveryPreview();
        using var vm = CreateViewModel(new FakeFileService("/tmp/DeliveryTests"), browseOnly: true, quickLookService: preview);
        await vm.OpenEntryAsync(new() { FullPath = "/tmp/DeliveryTests/archive.zip", Name = "archive.zip" });
        Assert.Equal("/tmp/DeliveryTests/archive.zip", preview.Path);
        Assert.False(vm.IsArchiveView);
        var before = vm.CurrentPath;
        await vm.NavigateToAsync("sftp://server/file");
        Assert.Equal(before, vm.CurrentPath);
    }

    [AvaloniaFact]
    public async Task DeliveryHiddenListDoesNotRefreshOnDirectoryNotification()
    {
        var files = new FakeFileService("/tmp/DeliveryTests");
        using var vm = CreateViewModel(files, browseOnly: true);
        vm.SetDirectoryNotificationsPaused(true);
        await vm.RefreshFromNotification();
        Assert.Equal(0, files.EnumerateDirectoryCallCount);
        vm.SetDirectoryNotificationsPaused(false);
        await vm.RefreshFromNotification();
        Assert.True(files.EnumerateDirectoryCallCount > 0);
    }

    private sealed class DeliveryPreview : IQuickLookService
    {
        public string? Path { get; private set; }
        public Task PreviewFileAsync(string filePath) { Path = filePath; return Task.CompletedTask; }
    }
}
