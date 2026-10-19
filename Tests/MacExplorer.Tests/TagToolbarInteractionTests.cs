using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Avalonia.Markup.Xaml;
using MacExplorer.Services;
using MacExplorer.Views;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "UnifiedTagInteraction")]
public sealed class TagToolbarInteractionTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task ToolbarFiltersEmptyBranchesOverflowsAndSeparatesHoverFromClick()
    {
        var root = Directory.CreateTempSubdirectory("tag-toolbar-").FullName;
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        using var vm = CreateViewModel(new FakeFileService(root), fileTagService: service);
        var parent = await service.CreateTagAsync("父标签");
        var child = await service.CreateChildTagAsync("子标签", parent.Name);
        var grandchild = await service.CreateChildTagAsync("孙标签", child.Name);
        await service.SetTagAsync([Path.Combine(root, "child.txt")], grandchild, true);
        await service.CreateChildTagAsync("空子标签", parent.Name);
        await service.CreateTagAsync("空根标签");
        foreach (var tag in await service.GetSidebarTagsAsync()) vm.SidebarTags.Add(tag);
        var toolbar = new TagToolbar { DataContext = vm, Width = 400 };
        var window = new Window { Content = toolbar, Width = 500, Height = 120 };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Tag is FileTag { Name: "空根标签" });
            var before = vm.CurrentPath;
            Action? hover = null;
            toolbar.ScheduleHover = (action, _) => { hover = action; return new TestDisposable(); };
            var parentButton = toolbar.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tag-toolbar-name"));
            Assert.DoesNotContain(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("tag-toolbar-arrow"));
            var location = parentButton.TranslatePoint(new Point(15, 15), window)!.Value;
            window.MouseMove(location); Assert.NotNull(hover); hover();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(before, vm.CurrentPath);
            Assert.Equal(new Thickness(8, 0), parentButton.Padding);
            var buttonContent = Assert.IsType<StackPanel>(parentButton.Content);
            Assert.IsType<PathIcon>(buttonContent.Children[0]);
            Assert.IsType<TextBlock>(buttonContent.Children[1]);
            Assert.Equal(6, buttonContent.Spacing);
            var menu = Assert.IsType<ContextMenu>(toolbar.ActiveMenu);
            var childItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Tag is FileTag);
            Assert.Equal(child.Name, Assert.IsType<FileTag>(childItem.Tag).Name);
            Assert.True(childItem.HasSubMenu);
            var grandchildItem = Assert.Single(childItem.Items.OfType<MenuItem>());
            Assert.Equal(grandchild.Name, Assert.IsType<FileTag>(grandchildItem.Tag).Name);
            Assert.DoesNotContain(childItem.Items.OfType<MenuItem>(), item => item.Tag is FileTag { Name: "空子标签" });
            // Repeated hover and crossing into the native menu must not toggle it closed.
            hover(); hover(); Assert.Same(menu, toolbar.ActiveMenu); Assert.True(menu.IsOpen);
            menu.UpdateLayout();
            var menuRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(childItem));
            var childPoint = childItem.TranslatePoint(new Point(40, 15), menuRoot)!.Value;
            menuRoot.MouseMove(childPoint); Dispatcher.UIThread.RunJobs();
            Assert.Same(menu, toolbar.ActiveMenu); Assert.True(menu.IsOpen);
            childItem.IsSubMenuOpen = true; Dispatcher.UIThread.RunJobs();
            Assert.True(childItem.IsSubMenuOpen);
            grandchildItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(grandchild.Name, vm.CurrentTag!.Name);
            var currentParent = toolbar.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tag-toolbar-name"));
            currentParent.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Dispatcher.UIThread.RunJobs();
            menu = Assert.IsType<ContextMenu>(toolbar.ActiveMenu); menu.UpdateLayout();
            childItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Tag is FileTag);
            menuRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(childItem));
            childPoint = childItem.TranslatePoint(new Point(40, 15), menuRoot)!.Value;
            menuRoot.MouseDown(childPoint, MouseButton.Left); menuRoot.MouseUp(childPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(child.Name, vm.CurrentTag!.Name);
            toolbar.CloseDropdown();
            toolbar.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tag-toolbar-name"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(parent.Name, vm.CurrentTag!.Name);
            for (var i = 0; i < 8; i++)
            {
                var tag = await service.CreateTagAsync("Long toolbar tag " + i);
                await service.SetTagAsync([Path.Combine(root, "item" + i)], tag, true);
            }
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(toolbar.OverflowTags);
            var more = toolbar.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tag-toolbar-more"));
            Assert.True(more.IsVisible);
            Assert.True(more.Bounds.Right <= toolbar.Bounds.Width);
            toolbar.Width = 60; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(more.IsVisible); Assert.NotEmpty(toolbar.OverflowTags);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task TreeMultiSelectionAndDragMovesHierarchyWithoutChangingFileTags()
    {
        var root = Directory.CreateTempSubdirectory("tag-tree-ui-").FullName;
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        using var vm = CreateViewModel(new FakeFileService(root), fileTagService: service);
        var parent = await service.CreateTagAsync("父"); var moving = await service.CreateTagAsync("移动");
        var paths = new[] { Path.Combine(root, "a"), Path.Combine(root, "b") };
        await service.SetTagAsync([paths[0]], moving, true);
        var tree = new TagTreePanel(vm, paths);
        var window = new Window { Content = tree, Width = 320, Height = 500 };
        window.Styles.Add(new FluentTheme());
        try
        {
            window.Show(); await tree.RefreshAsync(); tree.UpdateLayout(); window.UpdateLayout();
            Border Row(string name) => tree.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is FileTag tag && tag.Name == name);
            Button NameButton(Border row) => row.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is FileTag);
            bool Checked(Border row) => row.GetVisualDescendants().OfType<PathIcon>().Single(t => t.Classes.Contains("tag-tree-check")).IsVisible;
            bool Partial(Border row) => row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("tag-tree-partial")).IsVisible;
            Assert.True(Partial(Row(moving.Name))); Assert.False(Checked(Row(moving.Name)));
            NameButton(Row(moving.Name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await tree.RefreshAsync(); tree.UpdateLayout();
            Assert.True(Checked(Row(moving.Name))); Assert.False(Partial(Row(moving.Name)));
            Assert.Contains(paths[1], await service.FindFilePathsAsync(moving));
            using var data = new DataTransfer(); var item = new DataTransferItem(); item.Set(TagTreePanel.TagDragFormat, moving.Name); data.Add(item);
            var target = Row(parent.Name); window.UpdateLayout();
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, target, new Point(50, 16), KeyModifiers.None);
            target.RaiseEvent(over); Assert.Equal(DragDropEffects.Move, over.DragEffects);
            target.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, target, new Point(50, 16), KeyModifiers.None));
            await tree.RefreshAsync(); tree.UpdateLayout();
            Assert.Equal(parent.Name, (await service.GetSidebarTagsAsync()).Single(t => t.Name == moving.Name).ParentName);
            Assert.Equal(2, (await service.FindFilePathsAsync(moving)).Count);
            Assert.Empty(await service.FindFilePathsAsync(parent));
            Assert.Empty(FileTagTree.Build(await service.GetSidebarTagsAsync(), true).Where(n => n.Tag.Name != parent.Name));
            window.UpdateLayout(); target = Row(parent.Name);
            using var cycle = new DataTransfer(); var cycleItem = new DataTransferItem(); cycleItem.Set(TagTreePanel.TagDragFormat, parent.Name); cycle.Add(cycleItem);
            var invalid = new DragEventArgs(DragDrop.DragOverEvent, cycle, target, new Point(50, 16), KeyModifiers.None);
            target.RaiseEvent(invalid); Assert.Equal(DragDropEffects.None, invalid.DragEffects);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagementPopupKeepsInlineEditorOpenAndUpdatesLanguageInBothThemes(bool dark)
    {
        var root = Directory.CreateTempSubdirectory("tag-manager-").FullName;
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        var settings = new StartupSettings();
        using var language = new LocalizationService(settings, () => AppLanguage.ChineseSimplified);
        using var vm = CreateViewModel(new FakeFileService(root), fileTagService: service, settingsService: settings);
        var parent = await service.CreateTagAsync("父标签");
        var child = await service.CreateChildTagAsync("子标签", parent.Name);
        await service.SetTagAsync([Path.Combine(root, "child.txt")], child, true);
        foreach (var tag in await service.GetSidebarTagsAsync()) vm.SidebarTags.Add(tag);
        var toolbar = new FinderToolbar { DataContext = vm };
        var window = new Window { Width = 640, Height = 500, Content = toolbar,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Null(toolbar.FindControl<Control>("ManageTagsButton"));
            Assert.Null(toolbar.FindControl<Control>("ShowToolbarTagsOption"));
            var tags = toolbar.FindControl<TagToolbar>("ToolbarTags")!;
            tags.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tag-toolbar-name"))
                .RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Dispatcher.UIThread.RunJobs();
            tags.ActiveMenu!.UpdateLayout();
            tags.ActiveMenu.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CreateTagButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var popup = toolbar.FindControl<Popup>("TagManagementDropdown")!;
            var tree = Assert.Single(toolbar.FindControl<Border>("TagManagementHost")!.GetVisualDescendants().OfType<TagTreePanel>());
            await tree.RefreshAsync(); tree.UpdateLayout(); window.UpdateLayout();
            var create = tree.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CreateTagButton");
            var popupRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(tree));
            var point = create.TranslatePoint(new Point(14, 14), popupRoot)!.Value;
            popupRoot.MouseDown(point, MouseButton.Left); popupRoot.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);
            var input = Assert.Single(tree.GetVisualDescendants().OfType<TextBox>());
            input.Text = "A very long tag name that remains accessible in compact windows";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await tree.RefreshAsync(); tree.UpdateLayout();
            Assert.Contains(await service.GetSidebarTagsAsync(), t => t.Name == input.Text);
            Assert.True(popup.IsOpen);
            language.SetLanguage(AppLanguage.English); Dispatcher.UIThread.RunJobs(); await tree.RefreshAsync(); tree.UpdateLayout();
            Assert.Equal("New tag…", Avalonia.Automation.AutomationProperties.GetName(create));
            Assert.IsType<PathIcon>(create.Content);
            Assert.DoesNotContain(create.GetVisualDescendants().OfType<TextBlock>(), t => !string.IsNullOrEmpty(t.Text));
            var title = Assert.Single(tree.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("tag-tree-title"));
            Assert.Equal("Collections", title.Text);
            Assert.True(title.TranslatePoint(default, tree)!.Value.X < create.TranslatePoint(default, tree)!.Value.X);
            await service.CreateChildTagAsync("子项", input.Text!); Dispatcher.UIThread.RunJobs();
            await tree.RefreshAsync(); tree.UpdateLayout(); window.UpdateLayout();
            var row = tree.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is FileTag t && t.Name == input.Text);
            Assert.Equal(FastFileList.RowHeight, row.Bounds.Height);
            var disclosure = row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tag-tree-arrow"));
            Assert.Equal(FastFileList.TreeDisclosureWidth, disclosure.Bounds.Width);
            Assert.Equal(22, disclosure.Bounds.Height);
            Assert.Equal(12, Assert.IsType<PathIcon>(disclosure.Content).Width);
            var name = row.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is FileTag);
            var rowPoint = name.TranslatePoint(new Point(20, 15), popupRoot)!.Value;
            popupRoot.MouseMove(rowPoint); Dispatcher.UIThread.RunJobs();
            Assert.True(row.IsPointerOver);
            Assert.True(row.TryFindResource("SidebarHoverBrush", row.ActualThemeVariant, out var hoverBrush));
            Assert.Equal(Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(hoverBrush).Color,
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(row.Background).Color);
            var presenter = name.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single();
            Assert.Equal(0, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.Background).Color.A);
            popupRoot.MouseDown(rowPoint, MouseButton.Right); popupRoot.MouseUp(rowPoint, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(row.ContextMenu); Assert.True(row.ContextMenu.IsOpen);
            Assert.Contains(row.ContextMenu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Add subtag");
            Assert.Contains(row.ContextMenu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Rename");
            row.ContextMenu.Close();
            Assert.True(tree.Bounds.Width <= 380);
            toolbar.CloseDropdowns(); Assert.False(popup.IsOpen);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    private sealed class TestDisposable : IDisposable { public void Dispose() { } }

    [AvaloniaFact(Explicit = true)]
    public void ToolbarPreferenceUpdatesExistingWindowsAndManagementRemainsAccessible()
    {
        var settings = new StartupSettings(); var files = new FakeFileService("/tmp/tag-preferences");
        using var first = CreateViewModel(files, settingsService: settings);
        using var second = CreateViewModel(files, settingsService: settings);
        Assert.True(first.ShowToolbarTags); Assert.True(second.ShowToolbarTags);
        first.ShowToolbarTags = false; Dispatcher.UIThread.RunJobs(); Assert.False(second.ShowToolbarTags);
        using var reopened = CreateViewModel(files, settingsService: settings); Assert.False(reopened.ShowToolbarTags);
        second.ShowToolbarTags = true; Dispatcher.UIThread.RunJobs(); Assert.True(first.ShowToolbarTags);
    }
}
