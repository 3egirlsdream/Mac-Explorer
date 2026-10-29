using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileDelivery")]
public sealed class FileDeliveryBrowseViewModelTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task DeliveryPanelLoadsSharedStylesAndSwitchesEntriesWithoutChangingMainList()
    {
        using var fixture = new FileDeliveryTests.Fixture();
        using var service = new MacExplorer.Services.Impl.FileDeliveryService(fixture.Settings, fixture.Tags);
        foreach (var entry in service.Preferences.Entries.ToArray()) service.Remove(entry.Id);
        service.AddFolder(fixture.Root);
        service.SelectEntry(service.Preferences.Entries.Single().Id);
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
            Assert.Equal(2, window.FindControl<StackPanel>("EntryTabs")!.Children.Count);
            Assert.False(window.GetVisualDescendants().OfType<WindowTitleBar>().Single().IsVisible);
            var content = window.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(control => control.Name == "WindowContent");
            Assert.Equal(default, content.Margin);
            Assert.Equal(window.ClientSize.Width, content.Bounds.Width, 0);
            Assert.Equal(window.ClientSize.Height, content.Bounds.Height, 0);
            Assert.Equal(0, content.Bounds.Y);
            Assert.Single(panelVm.Entries);
            Assert.Empty(mainVm.Entries);
            Assert.DoesNotContain("无法访问", window.FindControl<TextBlock>("StatusText")!.Text ?? "");
            window.Suspend();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task StagingLoadsMixedPathsReturnsThroughHistoryAndClearsWithoutChangingFiles()
    {
        using var fixture = new FileDeliveryTests.Fixture();
        using var service = new MacExplorer.Services.Impl.FileDeliveryService(fixture.Settings, fixture.Tags);
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "Folder")).FullName;
        var file = Path.Combine(fixture.Root, "keep.txt");
        await File.WriteAllTextAsync(file, "keep");
        var files = new FakeFileService(fixture.Root);
        files.Seed(new() { Name = "keep.txt", FullPath = file });
        files.Seed(new() { Name = "Folder", FullPath = folder, IsDirectory = true });
        files.Seed(new() { Name = "child.txt", FullPath = Path.Combine(folder, "child.txt") });
        service.StagePaths([file, folder, Path.Combine(fixture.Root, "missing")]);
        using var vm = CreateViewModel(files, browseOnly: true);
        var window = new FileDeliveryWindow(service, fixture.Tags, vm);
        try
        {
            window.Show();
            await window.ResumeAsync();
            var clear = window.FindControl<Button>("ClearStagingButton")!;
            Assert.Equal(2, vm.Entries.Count);
            Assert.Contains("1", vm.StatusText);
            Assert.True(clear.IsVisible && clear.IsEnabled);
            Assert.Null(Assert.IsType<Button>(window.FindControl<StackPanel>("EntryTabs")!.Children[0]).ContextMenu);
            Assert.False(window.FindControl<Button>("UpButton")!.IsEnabled);
            await vm.OpenEntryAsync(vm.Entries.Single(e => e.IsDirectory));
            Assert.Equal(folder, vm.CurrentPath);
            Assert.False(clear.IsVisible);
            await vm.NavigateBackAsync();
            Assert.True(vm.IsDeliveryStaging);
            Assert.Equal(2, vm.Entries.Count);
            window.Suspend();
            await window.ResumeAsync();
            Assert.True(vm.IsDeliveryStaging);
            Assert.Equal(3, service.StagedPaths.Count);
            clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await vm.RefreshAsync();
            Assert.Empty(vm.Entries);
            Assert.False(clear.IsEnabled);
            Assert.Equal("keep", await File.ReadAllTextAsync(file));
            Assert.True(Directory.Exists(folder));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleStagingLoadCannotRepopulateAfterClearOrNavigation(bool navigateAway)
    {
        using var fixture = new FileDeliveryTests.Fixture();
        using var service = new MacExplorer.Services.Impl.FileDeliveryService(fixture.Settings, fixture.Tags);
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "Other")).FullName;
        var file = Path.Combine(fixture.Root, "staged.txt");
        var child = Path.Combine(folder, "child.txt");
        var files = new FakeFileService(fixture.Root);
        files.Seed(new() { FullPath = file, Name = "staged.txt" });
        files.Seed(new() { FullPath = child, Name = "child.txt" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeGetEntry = async _ => { entered.TrySetResult(); await release.Task; };
        service.StagePaths([file]);
        using var vm = CreateViewModel(files, browseOnly: true);
        vm.ConfigureDeliveryStaging(service);
        var pending = vm.NavigateToAsync(MacExplorer.Services.Impl.FileDeliveryService.StagingPath);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (navigateAway) await vm.NavigateToAsync(folder);
            else { service.ClearStaging(); await vm.RefreshAsync(); }
        }
        finally { release.TrySetResult(); }
        await pending;
        if (navigateAway)
        {
            Assert.Equal(folder, vm.CurrentPath);
            Assert.Equal(child, Assert.Single(vm.Entries).FullPath);
        }
        else Assert.Empty(vm.Entries);
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("zh-CN")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task StagingRestoresSelectionAndLocalizesDropWindow(string language)
    {
        using var fixture = new FileDeliveryTests.Fixture();
        using var localization = new MacExplorer.Services.Impl.LocalizationService(fixture.Settings);
        localization.SetLanguage(language);
        using var service = new MacExplorer.Services.Impl.FileDeliveryService(fixture.Settings, fixture.Tags);
        service.SelectEntry(service.Preferences.Entries[0].Id);
        using var vm = CreateViewModel(new FakeFileService(fixture.Root), browseOnly: true);
        var window = new FileDeliveryWindow(service, fixture.Tags, vm);
        var drop = new FileDeliveryDropWindow(service);
        try
        {
            window.Show();
            await window.ResumeAsync();
            Assert.False(vm.IsDeliveryStaging);
            Assert.Equal(service.Preferences.Entries[0].Location, vm.CurrentPath);
            var staging = Assert.IsType<Button>(window.FindControl<StackPanel>("EntryTabs")!.Children[0]);
            Assert.Equal(localization["delivery.staging"], Assert.IsType<StackPanel>(staging.Content).Children.OfType<TextBlock>().Single().Text);
            drop.Show();
            service.StagePaths([Path.Combine(fixture.Root, "test.txt")]);
            Assert.Equal(localization.Get("delivery.count", 1), drop.FindControl<TextBlock>("CountText")!.Text);
            drop.SetDropHover(true);
            Assert.Contains("drag-over", drop.FindControl<Border>("DropSurface")!.Classes);
            drop.SetDropHover(false);
            Assert.DoesNotContain("drag-over", drop.FindControl<Border>("DropSurface")!.Classes);
            Assert.False(drop.ShowActivated);
        }
        finally { drop.Close(); window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
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

    [AvaloniaFact(Explicit = true)]
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

    [AvaloniaFact(Explicit = true)]
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
            Assert.Single(tabs.Children);
            Assert.Null(Assert.IsType<Button>(tabs.Children[0]).ContextMenu);
            Assert.Contains(await fixture.Tags.GetSidebarTagsAsync(), t => t.Name == tag.Name);
            Assert.Equal("keep", await File.ReadAllTextAsync(file));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
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

    [AvaloniaTheory(Explicit = true)]
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

    [AvaloniaFact(Explicit = true)]
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

    [AvaloniaFact(Explicit = true)]
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

}

public abstract partial class FileListViewModelTestBase
{
    protected sealed class DeliveryPreview : IQuickLookService
    {
        public string? Path { get; private set; }
        public Task PreviewFileAsync(string filePath) { Path = filePath; return Task.CompletedTask; }
    }

}
