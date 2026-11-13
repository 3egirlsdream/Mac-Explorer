using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Sidebar")]
public sealed class SidebarGroupInteractionTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, 240)]
    [InlineData(true, 180)]
    public async Task GroupDropsPinIndependentlyWhileDirectoryRowsStillMoveFiles(bool dark, double width)
    {
        using var theme = new FastListTestTheme();
        using var fixture = new StoreFixture();
        var root = fixture.Root;
        var first = Directory.CreateDirectory(Path.Combine(root, "First")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "Second")).FullName;
        var payload = Path.Combine(root, "payload.txt");
        await File.WriteAllTextAsync(payload, "original");
        var files = new FakeFileService(root);
        foreach (var path in new[] { root, first, second })
            files.Seed(new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path), IsDirectory = true });
        files.Seed(new FileSystemEntry { FullPath = payload, Name = "payload.txt" });
        var group = await fixture.Store.CreateGroupAsync("长名称 Long group name for narrow sidebar");
        await fixture.Store.PinAsync(first, "First");
        using var pins = new PinnedFoldersViewModel(fixture.Store);
        using var otherPins = new PinnedFoldersViewModel(fixture.Store);
        using var vm = CreateViewModel(files, pinnedFolderService: fixture.Store, pinnedFolders: pins);
        using var other = CreateViewModel(files, pinnedFolderService: fixture.Store, pinnedFolders: otherPins);
        await pins.LoadPinnedFoldersAsync();
        await otherPins.LoadPinnedFoldersAsync();
        await vm.RefreshAsync();
        using var services = new ServiceCollection().AddSingleton<IFileService>(files).BuildServiceProvider();
        var previous = App.Services;
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        var hover = new SidebarHoverScheduler();
        var sidebar = new FinderSidebarView { DataContext = vm, ScheduleFolderHover = hover.Schedule };
        AddStyles(sidebar);
        var window = new Window { Width = width, Height = 1100, Content = sidebar,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); RenderIssue11(window);
            StackPanel Group() => sidebar.GetVisualDescendants().OfType<StackPanel>().Single(panel =>
                panel.Classes.Contains("pinned-folder-group") && panel.DataContext is PinnedFolderGroup item && item.Id == group);
            Point At(Control control, bool bottom = false) => control.TranslatePoint(
                new Point(control.Bounds.Width / 2, bottom ? control.Bounds.Height - 2 : control.Bounds.Height / 2), sidebar)!.Value;
            using var folders = await Transfer(window, first, second);
            var header = Group().Children.OfType<Grid>().Single();
            await WaitForIssue11Async(() =>
            {
                RenderIssue11(window);
                var targetGroup = Group();
                header = targetGroup.Children.OfType<Grid>().Single();
                return sidebar.InputHitTest(At(header)) is Visual hit
                    && (hit == targetGroup || hit.GetVisualAncestors().Contains(targetGroup));
            });
            Assert.Equal(DragDropEffects.Copy, Raise(sidebar, folders, DragDrop.DragOverEvent, At(header)).DragEffects);
            hover.Fire();
            Assert.Equal(root, vm.CurrentPath);
            Assert.Equal(DragDropEffects.Copy, Raise(sidebar, folders, DragDrop.DropEvent, At(header)).DragEffects);
            await WaitForIssue11Async(() => other.CustomGroups.Single().Folders.Count == 2);
            RenderIssue11(window);
            Assert.True(await fixture.Store.IsPinnedAsync(first));
            Assert.True(await fixture.Store.IsPinnedAsync(first, group));
            Assert.Empty(files.MoveRequests);
            Assert.Empty(files.CopyRequests);
            var provider = new PinnedFolderOmniboxProvider(fixture.Store);
            Assert.Single(await provider.GetSuggestionsAsync(vm, first, TestContext.Current.CancellationToken));
            var customSuggestion = Assert.Single(await provider.GetSuggestionsAsync(vm, second, TestContext.Current.CancellationToken));
            Assert.Contains(vm.CustomGroups.Single().Name, customSuggestion.Subtitle);
            var menu = await vm.LoadCompleteFileContextMenuAsync(new FileSystemEntry { FullPath = payload, Name = "payload.txt" });
            var destinations = menu.Single(action => action.Label == LocalizationText.Get("移动到")).SubItems!;
            Assert.Single(destinations.Where(action => action.Label == "First"));
            Assert.Single(destinations.Where(action => action.Label == "Second"));
            Assert.Equal(DragDropEffects.Copy, Raise(sidebar, folders, DragDrop.DropEvent, At(Group(), true)).DragEffects);
            Assert.Equal(2, (await fixture.Store.GetAllAsync()).Count(pin => pin.GroupId == group));
            using var mixed = await Transfer(window, second, payload);
            Assert.Equal(DragDropEffects.None, Raise(sidebar, mixed, DragDrop.DragOverEvent, At(Group(), true)).DragEffects);
            Assert.Equal(DragDropEffects.None, Raise(sidebar, mixed, DragDrop.DropEvent, At(Group(), true)).DragEffects);
            Assert.Equal(2, (await fixture.Store.GetAllAsync()).Count(pin => pin.GroupId == group));
            var row = Group().GetVisualDescendants().OfType<Border>().Single(border => border.Tag is string path && path == first);
            using var file = await Transfer(window, payload);
            Assert.Equal(DragDropEffects.Move, Raise(sidebar, file, DragDrop.DragOverEvent, At(row)).DragEffects);
            Assert.Contains("folder-drop-target", row.Classes);
            Assert.Equal(DragDropEffects.Move, Raise(sidebar, file, DragDrop.DropEvent, At(row)).DragEffects);
            await WaitForIssue11Async(() => files.MoveCalls == 1);
            Assert.Equal((payload, first), Assert.Single(files.MoveRequests));
            RenderIssue11(window);
            row = Group().GetVisualDescendants().OfType<Border>().Single(border => border.Tag is string path && path == first);
            var unpin = row.GetVisualDescendants().OfType<Button>().Single(button => button.Tag is PinnedFolder);
            unpin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForIssue11Async(() => other.CustomGroups.Single().Folders.Count == 1);
            Assert.True(await fixture.Store.IsPinnedAsync(first));
            Assert.False(await fixture.Store.IsPinnedAsync(first, group));
            sidebar.SetRailMode(true);
            Assert.Equal(0, sidebar.FindControl<ItemsControl>("CustomGroupsControl")!.MaxHeight);
            await fixture.Store.RenameGroupAsync(group, "Renamed");
            await WaitForIssue11Async(() => other.CustomGroups.Single().Name == "Renamed");
            sidebar.SetRailMode(false);
            Assert.NotEqual(0, sidebar.FindControl<ItemsControl>("CustomGroupsControl")!.MaxHeight);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previous);
        }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupsCollapseIndependentlyRememberStateAndAcceptDropsOnCollapsedHeaders(bool dark)
    {
        using var theme = new FastListTestTheme();
        using var fixture = new StoreFixture();
        using var settings = new SettingsService(fixture.Factory);
        var groupId = await fixture.Store.CreateGroupAsync("Work");
        var emptyId = await fixture.Store.CreateGroupAsync("Empty");
        var first = Directory.CreateDirectory(Path.Combine(fixture.Root, "First")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(fixture.Root, "Second")).FullName;
        await fixture.Store.PinAsync(first, "First", groupId);
        var files = new FakeFileService(fixture.Root);
        using var pins = new PinnedFoldersViewModel(fixture.Store, settingsService: settings);
        using var otherPins = new PinnedFoldersViewModel(fixture.Store, settingsService: settings);
        using var vm = CreateViewModel(files, pinnedFolderService: fixture.Store, pinnedFolders: pins);
        await pins.LoadPinnedFoldersAsync();
        await otherPins.LoadPinnedFoldersAsync();
        using var services = new ServiceCollection().AddSingleton<IFileService>(files).BuildServiceProvider();
        var previous = App.Services;
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        var sidebar = new FinderSidebarView { DataContext = vm };
        AddStyles(sidebar);
        var window = new Window { Width = 220, Height = 1100, Content = sidebar,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); RenderIssue11(window);
            StackPanel Group(int id) => sidebar.GetVisualDescendants().OfType<StackPanel>().Single(panel =>
                panel.Classes.Contains("pinned-folder-group") && panel.DataContext is PinnedFolderGroup item && item.Id == id);
            Grid Header(int id) => Group(id).Children.OfType<Grid>().Single();
            StackPanel Body(int id) => Group(id).Children.OfType<StackPanel>().Single();
            void ClickHeader(int id)
            {
                RenderIssue11(window);
                var header = Header(id);
                var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                RenderIssue11(window);
            }
            var arrow = Header(groupId).GetVisualDescendants().OfType<PathIcon>().Single();
            Assert.Equal(10, arrow.Width);
            Assert.Equal(10, arrow.Height);
            Assert.Contains("expanded", arrow.Classes);
            ClickHeader(groupId);
            Assert.False(Body(groupId).IsVisible);
            Assert.DoesNotContain("expanded", arrow.Classes);
            Assert.True(otherPins.CustomGroups.Single(group => group.Id == groupId).IsCollapsed);
            Assert.True(Body(emptyId).IsVisible);
            using var data = await Transfer(window, second);
            var target = Header(groupId);
            var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), sidebar)!.Value;
            Assert.Equal(DragDropEffects.Copy, Raise(sidebar, data, DragDrop.DragOverEvent, point).DragEffects);
            Assert.Equal(DragDropEffects.Copy, Raise(sidebar, data, DragDrop.DropEvent, point).DragEffects);
            await WaitForIssue11Async(() => vm.CustomGroups.Single(group => group.Id == groupId).Folders.Count == 2);
            RenderIssue11(window);
            Assert.False(Body(groupId).IsVisible);
            Assert.Empty(files.MoveRequests);
            var expand = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space };
            Header(groupId).RaiseEvent(expand);
            Assert.True(expand.Handled);
            Assert.True(Body(groupId).IsVisible);
            Header(groupId).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.False(Body(groupId).IsVisible);
            ClickHeader(emptyId);
            Assert.False(Body(emptyId).IsVisible);
            await fixture.Store.RenameGroupAsync(groupId, "Renamed");
            using var restored = new PinnedFoldersViewModel(fixture.Store, settingsService: settings);
            await restored.LoadPinnedFoldersAsync();
            Assert.All(restored.CustomGroups, group => Assert.True(group.IsCollapsed));
            Assert.Equal("Renamed", restored.CustomGroups.Single(group => group.Id == groupId).Name);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previous);
        }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SettingsManageGroupsAndLanguageChangesPreserveUserNames()
    {
        using var theme = new FastListTestTheme();
        using var fixture = new StoreFixture();
        using var settings = new SettingsService(fixture.Factory);
        using var language = new LocalizationService(settings, () => AppLanguage.ChineseSimplified);
        using var http = new HttpClient();
        var files = new FakeFileService(fixture.Root);
        using var pins = new PinnedFoldersViewModel(fixture.Store);
        using var vm = CreateViewModel(files, settingsService: settings, pinnedFolderService: fixture.Store, pinnedFolders: pins);
        await pins.LoadPinnedFoldersAsync();
        var previous = App.Services;
        using var services = new ServiceCollection().AddSingleton(new PhotoLocationConsent(settings, Path.Combine(fixture.Root, "consent"))).BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        var dialog = new SettingsDialog(new AppUpdateTests.DefaultAppServiceStub(), settings,
            new AppUpdateTests.ThemeServiceStub(), new AppUpdateTests.TypographyServiceStub(),
            new AppUpdateTests.OpenWithAppServiceStub(), new AppUpdateService(http), localizationService: language,
            pinnedFolderService: fixture.Store) { DataContext = vm };
        try
        {
            dialog.Show();
            var tabs = dialog.FindControl<TabControl>("SettingsTabs")!;
            var page = tabs.Items.OfType<LazyTabItem>().Single(tab => tab.PageKey == "sidebar");
            Assert.False(page.IsContentLoaded);
            tabs.SelectedItem = page;
            RenderIssue11(dialog);
            var add = page.FindContentControl<Button>("AddSidebarGroupButton")!;
            Click(add);
            var naming = dialog.OwnedWindows.OfType<TabGroupNameDialog>().Single();
            naming.GetVisualDescendants().OfType<TextBox>().Single().Text = "My 资料";
            Click(naming.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveButton"));
            await WaitForIssue11Async(() => vm.CustomGroups.Count == 1 && !naming.IsVisible);
            var id = vm.CustomGroups.Single().Id;
            Click(add);
            naming = dialog.OwnedWindows.OfType<TabGroupNameDialog>().Single();
            naming.GetVisualDescendants().OfType<TextBox>().Single().Text = "my 资料";
            Click(naming.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveButton"));
            Assert.True(naming.IsVisible);
            Assert.True(naming.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "GroupNameError").IsVisible);
            Assert.Single(vm.CustomGroups);
            naming.Close();
            await fixture.Store.PinAsync(fixture.Root, "Root", id);
            RenderIssue11(dialog);
            var list = page.FindContentControl<ItemsControl>("SidebarGroupsList")!;
            foreach (var code in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                language.SetLanguage(code);
                RenderIssue11(dialog);
                Assert.Equal(language.Get("sidebar.groups.add"), Avalonia.Automation.AutomationProperties.GetName(add));
                Assert.Equal("My 资料", vm.CustomGroups.Single().Name);
            }
            var rename = list.GetVisualDescendants().OfType<Button>().First();
            Click(rename);
            naming = dialog.OwnedWindows.OfType<TabGroupNameDialog>().Single();
            naming.GetVisualDescendants().OfType<TextBox>().Single().Text = "Changed";
            Click(naming.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveButton"));
            await WaitForIssue11Async(() => vm.CustomGroups.Single().Name == "Changed" && !naming.IsVisible);
            RenderIssue11(dialog);
            Click(list.GetVisualDescendants().OfType<Button>().Last());
            var confirmation = dialog.OwnedWindows.OfType<ConfirmDialog>().Single();
            confirmation.Close(false);
            Assert.Single(await fixture.Store.GetGroupsAsync());
            Click(list.GetVisualDescendants().OfType<Button>().Last());
            confirmation = dialog.OwnedWindows.OfType<ConfirmDialog>().Single();
            confirmation.Close(true);
            await WaitForIssue11Async(() => vm.CustomGroups.Count == 0);
            Assert.True(Directory.Exists(fixture.Root));
            Assert.Empty(await fixture.Store.GetAllAsync());
        }
        finally
        {
            foreach (var owned in dialog.OwnedWindows.ToArray()) owned.Close();
            dialog.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previous);
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void AddStyles(Control control)
    {
        control.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        control.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
    }
    private static DragEventArgs Raise(FinderSidebarView sidebar, IDataTransfer data, RoutedEvent<DragEventArgs> evt, Point point)
    {
        var args = new DragEventArgs(evt, data, sidebar, point, KeyModifiers.None) { DragEffects = DragDropEffects.Copy | DragDropEffects.Move };
        sidebar.RaiseEvent(args);
        return args;
    }
    private static async Task<DataTransfer> Transfer(Window window, params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(path))
                : await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            data.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(item)));
        }
        return data;
    }
    private sealed class StoreFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("sidebar-group-ui-").FullName;
        public DatabaseConnectionFactory Factory { get; }
        public PinnedFolderService Store { get; }
        public StoreFixture()
        {
            Factory = new DatabaseConnectionFactory(Path.Combine(Root, "pins.db"));
            using var connection = Factory.GetConnection();
            SqliteSchema.Initialize(connection);
            Store = new PinnedFolderService(Factory);
        }
        public void Dispose()
        {
            Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }
}
