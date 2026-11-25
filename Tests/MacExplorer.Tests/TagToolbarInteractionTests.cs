using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
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
    public async Task FinderToolbarOverflowsTagsBeforeFileActionsAndRestoresBothOnResize()
    {
        var root = Directory.CreateTempSubdirectory("fkfinder-toolbar-layout-").FullName;
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        using var vm = CreateViewModel(new FakeFileService(root), fileTagService: service);
        var toolbar = new FinderToolbar { DataContext = vm };
        var window = new Window { Width = 1300, Height = 120, Content = toolbar };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        window.Show();
        try
        {
            void Resize(double width)
            {
                window.Width = width;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            var tags = toolbar.FindControl<TagToolbar>("ToolbarTags")!;
            var inlineTags = toolbar.FindControl<Grid>("InlineTagsHost")!;
            var tagsInMore = toolbar.FindControl<Button>("TagsOverflowButton")!;
            var copy = toolbar.FindControl<Button>("CopyButton")!;
            var copyInMore = toolbar.FindControl<Button>("CopyOverflowButton")!;
            var more = toolbar.FindControl<Button>("MoreBtn")!;
            Assert.False(inlineTags.IsVisible);
            Assert.False(tagsInMore.IsVisible);
            Assert.False(copy.IsEnabled);
            Assert.False(copyInMore.IsEnabled);

            var file = Path.Combine(root, "example.txt");
            for (var i = 1; i <= 4; i++)
            {
                var tag = await service.CreateTagAsync($"Project folder for delivery {i}");
                await service.SetTagAsync([file], tag, true);
            }
            await TestTime.WaitUntilAsync(() => vm.SidebarTags.Count(tag => tag.ItemCount > 0) == 4);
            vm.SelectedEntries.Add(new FileSystemEntry { Name = "example.txt", FullPath = file });
            Resize(1300);
            Assert.True(copy.IsEnabled);
            Assert.True(copyInMore.IsEnabled);
            Assert.True(inlineTags.IsVisible);
            Assert.Empty(tags.OverflowTags);
            var firstTag = tags.GetVisualDescendants().OfType<Button>().First(b => b.Tag is FileTag);
            var tagMore = tags.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tag-toolbar-more"));
            var actionsWidth = Math.Ceiling(toolbar.Bounds.Width - inlineTags.Bounds.Width);
            var tagsWidth = Math.Ceiling(toolbar.Bounds.Width - tags.Bounds.Width + TagToolbar.OverflowWidth);

            Resize(tagsWidth + firstTag.Bounds.Width + 4);
            Assert.True(firstTag.IsEffectivelyVisible);
            Assert.InRange(tags.OverflowTags.Count, 1, 3);
            Assert.True(copy.IsEffectivelyVisible);
            Assert.True(firstTag.Focus());

            Resize(tagsWidth);
            Assert.Equal(4, tags.OverflowTags.Count);
            Assert.True(tagMore.IsEffectivelyVisible);
            Assert.True(tagMore.IsFocused);
            Assert.True(copy.IsEffectivelyVisible);
            Assert.False(tagsInMore.IsVisible);

            Resize(tagsWidth - 1);
            Assert.False(inlineTags.IsVisible);
            Assert.True(tagsInMore.IsVisible);
            Assert.True(copy.IsEffectivelyVisible);
            Assert.True(more.IsFocused);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(copyInMore.IsEffectivelyVisible);
            Assert.True(toolbar.FindControl<Popup>("MoreDropdown")!.IsOpen);
            Assert.True(tagsInMore.IsFocused);
            var popupRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(tagsInMore));
            popupRoot.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.IsType<ContextMenu>(tags.ActiveMenu);
            Assert.True(menu.IsOpen);
            Assert.Equal(4, menu.Items.OfType<MenuItem>().Count(item => item.Tag is FileTag));
            Assert.True(toolbar.TryCloseDropdown());
            Assert.True(more.IsFocused);

            Resize(actionsWidth);
            Assert.True(copy.IsEffectivelyVisible);
            Assert.True(copy.Focus());
            Resize(actionsWidth - 1);
            Assert.False(copy.IsEffectivelyVisible);
            Assert.True(more.IsFocused);
            toolbar.ToggleMenu(ToolbarMenuKind.More);
            Dispatcher.UIThread.RunJobs();
            Assert.True(copyInMore.IsEffectivelyVisible);
            Assert.True(tagsInMore.IsEffectivelyVisible);
            Assert.True(copyInMore.Focus());

            Resize(actionsWidth);
            Assert.True(copy.IsEffectivelyVisible);
            Assert.True(more.IsFocused);
            Resize(tagsWidth);
            Assert.True(tagMore.IsEffectivelyVisible);
            Assert.False(tagsInMore.IsVisible);
            Resize(1300);
            Assert.True(firstTag.IsEffectivelyVisible);
            Assert.Empty(tags.OverflowTags);

            vm.ShowToolbarTags = false;
            Resize(tagsWidth - 1);
            Assert.False(inlineTags.IsVisible);
            Assert.False(tagsInMore.IsVisible);
            Assert.True(copy.IsEffectivelyVisible);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ToolbarActionsRenameSystemAliasAndCreateChildWithoutChangingNavigation()
    {
        var root = Directory.CreateTempSubdirectory("tag-actions-").FullName;
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        using var vm = CreateViewModel(new FakeFileService(root), fileTagService: service);
        using var other = CreateViewModel(new FakeFileService(root), fileTagService: service);
        var red = FileTagCatalog.FinderColors[0];
        await service.SetTagAsync([Path.Combine(root, "red.txt")], red, true);
        await vm.NavigateToTagAsync(red); await other.NavigateToTagAsync(red);
        var appStyles = new Avalonia.Styling.IStyle[]
        {
            new FluentTheme(),
            (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")),
            (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml"))
        };
        foreach (var style in appStyles) Application.Current!.Styles.Add(style);
        var toolbar = new FinderToolbar { DataContext = vm };
        var window = new Window { Width = 900, Height = 480, Content = toolbar };
        MacExplorer.Views.Dialogs.CollectionManagerDialog? openedDialog = null;
        toolbar.PresentCollectionManagerAsync = (dialog, owner) =>
        {
            openedDialog?.Close();
            openedDialog = dialog;
            dialog.Show(owner);
            return Task.CompletedTask;
        };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var tags = toolbar.FindControl<TagToolbar>("ToolbarTags")!;
            void OpenActions()
            {
                window.UpdateLayout();
                var button = tags.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is FileTag { Name: "红色" });
                var point = button.TranslatePoint(new Point(12, 14), window)!.Value;
                window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();
            }
            OpenActions();
            var menu = Assert.IsType<ContextMenu>(tags.ActiveMenu);
            Assert.Equal(5, menu.Items.Count);
            Assert.False(((MenuItem)menu.Items[4]!).IsEnabled);
            ((MenuItem)menu.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(openedDialog);
            await openedDialog!.RefreshAsync();
            openedDialog.UpdateLayout();
            var input = openedDialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CollectionNameInput");
            input.Text = "优先处理";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await TestTime.WaitUntilAsync(() => vm.CurrentTag?.DisplayName == "优先处理" && other.CurrentTag?.DisplayName == "优先处理");
            Assert.Equal("优先处理", vm.CurrentTag!.DisplayName);
            Assert.Equal("优先处理", other.CurrentTag!.DisplayName);
            Assert.Equal("优先处理", vm.CurrentLocationTitle);
            Assert.Equal("优先处理", other.CurrentLocationTitle);
            Assert.Equal(red.VirtualPath, other.CurrentPath);
            openedDialog.Close();
            openedDialog = null;

            OpenActions();
            menu = Assert.IsType<ContextMenu>(tags.ActiveMenu);
            ((MenuItem)menu.Items[1]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(openedDialog);
            await openedDialog!.RefreshAsync();
            openedDialog.UpdateLayout();
            input = openedDialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CollectionNameInput");
            input.Text = "本周";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await WaitUntilAsync(async () => (await service.GetSidebarTagsAsync()).Any(t => t.Name == "本周"));
            Assert.Equal(red.Name, (await service.GetSidebarTagsAsync()).Single(t => t.Name == "本周").ParentName);
            Assert.Equal(red.VirtualPath, vm.CurrentPath);
            Assert.Equal(red.VirtualPath, other.CurrentPath);
        }
        finally
        {
            openedDialog?.Close();
            window.Close();
            foreach (var style in appStyles) Application.Current!.Styles.Remove(style);
            Directory.Delete(root, true);
        }
    }

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
        var window = new Window { Content = toolbar, Width = 500, Height = 320 };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            // Custom empty collections remain visible in the toolbar while unused system colors stay hidden.
            Assert.Contains(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Tag is FileTag { Name: "空根标签" });
            Assert.DoesNotContain(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Tag is FileTag { Name: "红色" });
            var before = vm.CurrentPath;
            Action? hover = null;
            toolbar.ScheduleHover = (action, _) => { hover = action; return new TestDisposable(); };
            var parentButton = toolbar.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is FileTag { Name: "父标签" });
            Assert.DoesNotContain(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("tag-toolbar-arrow"));
            var location = parentButton.TranslatePoint(new Point(15, 15), window)!.Value;
            window.MouseMove(location); Assert.NotNull(hover); hover();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(before, vm.CurrentPath);
            Assert.Equal(new Thickness(8, 0), parentButton.Padding);
            var buttonContent = Assert.IsType<StackPanel>(parentButton.Content);
            var tagIcon = Assert.IsType<PathIcon>(buttonContent.Children[0]);
            var tagLabel = Assert.IsType<TextBlock>(buttonContent.Children[1]);
            Assert.Equal(13, tagIcon.Width);
            Assert.Equal(13, tagIcon.Height);
            Assert.Equal(parentButton.FontFamily, tagLabel.FontFamily);
            Assert.Equal(parentButton.FontSize, tagLabel.FontSize);
            Assert.Equal(Avalonia.Media.FontWeight.Normal, tagLabel.FontWeight);
            Assert.Equal(6, buttonContent.Spacing);
            var menu = Assert.IsType<ContextMenu>(toolbar.ActiveMenu);
            var childItem = menu.Items.OfType<MenuItem>().Single(item => item.Tag is FileTag { Name: "子标签" });
            Assert.Equal(child.Name, Assert.IsType<FileTag>(childItem.Tag).Name);
            Assert.True(childItem.HasSubMenu);
            var grandchildItem = Assert.Single(childItem.Items.OfType<MenuItem>());
            Assert.Equal(grandchild.Name, Assert.IsType<FileTag>(grandchildItem.Tag).Name);
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.Tag is FileTag { Name: "空子标签" });
            // Repeated hover and crossing into the native menu must not toggle it closed.
            hover(); hover(); Assert.Same(menu, toolbar.ActiveMenu); Assert.True(menu.IsOpen);
            menu.UpdateLayout();
            var menuRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(childItem));
            var childPoint = childItem.TranslatePoint(new Point(40, 15), menuRoot)!.Value;
            menuRoot.MouseMove(childPoint); Dispatcher.UIThread.RunJobs();
            Assert.Same(menu, toolbar.ActiveMenu); Assert.True(menu.IsOpen);
            childItem.IsSubMenuOpen = true; Dispatcher.UIThread.RunJobs();
            Assert.True(childItem.IsSubMenuOpen);
            // Right-click the deepest entry: the action must target that entry, not its parent.
            grandchildItem.UpdateLayout();
            var leafRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(grandchildItem));
            var leafPoint = grandchildItem.TranslatePoint(new Point(40, 15), leafRoot)!.Value;
            FileTag? renameTarget = null;
            toolbar.RenameTagRequested += tag => renameTarget = tag;
            leafRoot.MouseDown(leafPoint, MouseButton.Right); leafRoot.MouseUp(leafPoint, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var actions = Assert.IsType<ContextMenu>(toolbar.ActiveMenu);
            Assert.NotSame(menu, actions); Assert.False(menu.IsOpen); Assert.Equal(5, actions.Items.Count);
            ((MenuItem)actions.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(grandchild.Name, renameTarget?.Name);
            Assert.Equal(before, vm.CurrentPath);
            parentButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            menu = Assert.IsType<ContextMenu>(toolbar.ActiveMenu);
            childItem = menu.Items.OfType<MenuItem>().Single(item => item.Tag is FileTag { Name: "子标签" });
            grandchildItem = Assert.Single(childItem.Items.OfType<MenuItem>());
            grandchildItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await TestTime.WaitUntilAsync(() => vm.CurrentTag?.Name == grandchild.Name && !toolbar.IsNavigating);
            Assert.Equal(grandchild.Name, vm.CurrentTag!.Name);
            var currentParent = toolbar.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is FileTag { Name: "父标签" });
            currentParent.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Dispatcher.UIThread.RunJobs();
            menu = Assert.IsType<ContextMenu>(toolbar.ActiveMenu); menu.UpdateLayout();
            childItem = menu.Items.OfType<MenuItem>().Single(item => item.Tag is FileTag { Name: "子标签" });
            menuRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(childItem));
            menuRoot.UpdateLayout();
            childPoint = childItem.TranslatePoint(new Point(40, 15), menuRoot)!.Value;
            menuRoot.MouseDown(childPoint, MouseButton.Left); menuRoot.MouseUp(childPoint, MouseButton.Left);
            await TestTime.WaitUntilAsync(() => vm.CurrentTag?.Name == child.Name && !toolbar.IsNavigating);
            Assert.Equal(child.Name, vm.CurrentTag!.Name);
            toolbar.CloseDropdown();
            toolbar.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is FileTag { Name: "父标签" })
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await TestTime.WaitUntilAsync(() => vm.CurrentTag?.Name == parent.Name && !toolbar.IsNavigating);
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
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            menu = Assert.IsType<ContextMenu>(toolbar.ActiveMenu); menu.UpdateLayout();
            var overflowItem = menu.Items.OfType<MenuItem>().First(item => item.Tag is FileTag);
            overflowItem.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.F10, KeyModifiers = KeyModifiers.Shift
            });
            actions = Assert.IsType<ContextMenu>(toolbar.ActiveMenu);
            Assert.Equal(5, actions.Items.Count);
            ((MenuItem)actions.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(((FileTag)overflowItem.Tag!).Name, renameTarget?.Name);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task AssignmentTreeSupportsMultiSelectionSearchAndQuickCreateWithoutHierarchyDrag()
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

            // Tag assignment popup does not allow accidental hierarchy drag-and-drop.
            using var data = new DataTransfer(); var item = new DataTransferItem(); item.Set(TagTreePanel.TagDragFormat, moving.Name); data.Add(item);
            var target = Row(parent.Name); window.UpdateLayout();
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, target, new Point(50, 16), KeyModifiers.None);
            target.RaiseEvent(over);
            Assert.Equal(DragDropEffects.None, over.DragEffects);
            Assert.Null(target.ContextMenu);

            // Typing a new name filters and creates+applies on Enter.
            var search = tree.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TagTreeSearchInput");
            search.Text = "快速新建";
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(tree.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "QuickCreateCollectionButton"));
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await WaitUntilAsync(async () => (await service.GetSidebarTagsAsync()).Any(t => t.Name == "快速新建"));
            var created = (await service.GetSidebarTagsAsync()).Single(t => t.Name == "快速新建");
            Assert.Equal(2, (await service.FindFilePathsAsync(created)).Count);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectionManagerDialogSupportsInlineCreateRenameDragScrollPreserveAndThemes(bool dark)
    {
        var root = Directory.CreateTempSubdirectory("collection-manager-").FullName;
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        var settings = new StartupSettings();
        using var language = new LocalizationService(settings, () => AppLanguage.ChineseSimplified);
        var parent = await service.CreateTagAsync("项目");
        var sibling = await service.CreateTagAsync("灵感");
        var child = await service.CreateChildTagAsync("子项", parent.Name);
        for (var i = 0; i < 12; i++)
            await service.CreateTagAsync($"额外收藏夹 {i:D2}");
        await service.SetTagAsync([Path.Combine(root, "a.txt")], child, true);

        var appStyles = new Avalonia.Styling.IStyle[]
        {
            new FluentTheme(),
            (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")),
            (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml"))
        };
        foreach (var style in appStyles) Application.Current!.Styles.Add(style);
        var dialog = new MacExplorer.Views.Dialogs.CollectionManagerDialog(service)
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            Height = 360
        };
        try
        {
            dialog.Show();
            await dialog.RefreshAsync(preserveScroll: false);
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // Verify there is no color picker or parent dropdown clutter in the single-pane outline.
            Assert.Empty(dialog.GetVisualDescendants().OfType<ComboBox>());
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name?.StartsWith("ColorSwatch_") == true);

            // Scroll down and expand a lower branch: scroll offset must not jump back to top.
            var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "CollectionTreeScrollViewer");
            scroll.Offset = new Vector(0, 120);
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var beforeOffsetY = scroll.Offset.Y;
            Assert.True(beforeOffsetY > 0);
            Border Row(string name) => dialog.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is FileTag t && t.Name == name);
            var parentRow = Row(parent.Name);
            var expandArrow = parentRow.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tag-tree-arrow"));
            expandArrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            Assert.Same(parentRow, Row(parent.Name));
            Assert.True(scroll.Offset.Y >= beforeOffsetY - 1);

            // Inline rename in place via BeginRename and Enter.
            dialog.BeginRename(child);
            Dispatcher.UIThread.RunJobs();
            var inlineInput = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CollectionNameInput");
            Assert.Equal("子项", inlineInput.Text);
            inlineInput.Text = "子项已改名";
            inlineInput.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await WaitUntilAsync(async () => (await service.GetSidebarTagsAsync()).Any(t => t.Name == "子项已改名"));

            // Inline child draft create: real pointer click on Cancel cancels without creating,
            // and real pointer click on Confirm creates the child collection.
            dialog.BeginCreate(parent.Name);
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            inlineInput = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CollectionNameInput");
            inlineInput.Text = "点取消不建";
            var cancelBtn = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CancelInlineEditButton");
            var cancelPoint = cancelBtn.TranslatePoint(new Point(10, 10), dialog)!.Value;
            dialog.MouseDown(cancelPoint, MouseButton.Left);
            dialog.MouseUp(cancelPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "CollectionNameInput");
            Assert.DoesNotContain(await service.GetSidebarTagsAsync(), t => t.Name == "点取消不建");

            dialog.BeginCreate(parent.Name);
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            inlineInput = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CollectionNameInput");
            inlineInput.Text = "点确认建成";
            var confirmBtn = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CommitInlineEditButton");
            var confirmPoint = confirmBtn.TranslatePoint(new Point(10, 10), dialog)!.Value;
            dialog.MouseDown(confirmPoint, MouseButton.Left);
            dialog.MouseUp(confirmPoint, MouseButton.Left);
            await WaitUntilAsync(async () => (await service.GetSidebarTagsAsync()).Any(t => t.Name == "点确认建成" && t.ParentName == parent.Name));

            // Inline draft create; Escape cancels without polluting DB.
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NewCollectionButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            inlineInput = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CollectionNameInput");
            inlineInput.Text = "不保存草稿";
            inlineInput.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(await service.GetSidebarTagsAsync(), t => t.Name == "不保存草稿");

            // Inline draft create with Enter creates the collection cleanly.
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NewCollectionButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            inlineInput = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CollectionNameInput");
            var longName = "A very long collection name for compact outline verification";
            inlineInput.Text = longName;
            inlineInput.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await WaitUntilAsync(async () => (await service.GetSidebarTagsAsync()).Any(t => t.Name == longName));

            // Drag & drop moves collection under parent and RootDropZone moves it back to top level.
            await dialog.RefreshAsync(preserveScroll: true);
            dialog.UpdateLayout();
            parentRow = Row(parent.Name);
            using var drag = new DataTransfer();
            var dragItem = new DataTransferItem();
            dragItem.Set(TagTreePanel.TagDragFormat, sibling.Name);
            drag.Add(dragItem);
            var over = new DragEventArgs(DragDrop.DragOverEvent, drag, parentRow, new Point(40, 15), KeyModifiers.None);
            parentRow.RaiseEvent(over);
            Assert.Equal(DragDropEffects.Move, over.DragEffects);
            parentRow.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, drag, parentRow, new Point(40, 15), KeyModifiers.None));
            await WaitUntilAsync(async () => (await service.GetSidebarTagsAsync()).Single(t => t.Name == sibling.Name).ParentName == parent.Name);

            await dialog.RefreshAsync(preserveScroll: true);
            dialog.UpdateLayout();
            var rootDrop = dialog.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RootDropSpacer");
            rootDrop.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, drag, rootDrop, new Point(40, 10), KeyModifiers.None));
            await WaitUntilAsync(async () => (await service.GetSidebarTagsAsync()).Single(t => t.Name == sibling.Name).ParentName == null);

            // Narrow width + live language switch keeps title and layout intact.
            dialog.Width = 340;
            language.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();
            await dialog.RefreshAsync(preserveScroll: true);
            dialog.UpdateLayout();
            Assert.Equal("Manage Collections", dialog.Title);
            Assert.True(dialog.Bounds.Width <= 345);
        }
        finally
        {
            dialog.Close();
            foreach (var style in appStyles) Application.Current!.Styles.Remove(style);
            Directory.Delete(root, true);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 4000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (await condition()) return;
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(await condition());
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
