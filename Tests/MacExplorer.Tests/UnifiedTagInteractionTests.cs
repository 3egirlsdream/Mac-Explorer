using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Themes.Fluent;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TagMenuAlignsColoredIconsAndLabelsWithStandardItems(bool isChecked)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_TagIcons_");
        using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")));
        var path = Path.Combine(root.FullName, "test.txt");
        await File.WriteAllTextAsync(path, "test", TestContext.Current.CancellationToken);
        var tag = FileTagCatalog.FinderColors[0];
        await tags.SetTagAsync([path], tag, isChecked);
        var files = new FakeFileService(root.FullName);
        var entry = new FileSystemEntry { Name = "test.txt", FullPath = path };
        using var vm = CreateViewModel(files, fileTagService: tags);
        vm.Entries.Add(entry);
        vm.SetSelection([entry]);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Styles.Add(new FluentTheme());
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var showMenu = typeof(FileListView).GetMethod("ShowMenuAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)showMenu.Invoke(view, [view, true])!;
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>());
            var submenu = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "标签");
            submenu.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var red = Assert.Single(submenu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == tag.Name);
            Assert.Equal(isChecked, red.IsChecked);
            Assert.Equal(MenuItemToggleType.CheckBox, red.ToggleType);
            var icon = Assert.IsType<PathIcon>(red.Icon);
            Assert.Equal(Avalonia.Media.Color.Parse(tag.ColorHex), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(icon.Foreground).Color);
            Assert.True(icon.IsEffectivelyVisible);
            Assert.True(icon.Bounds.Width > 0 && icon.Bounds.Height > 0);
            var newTag = Assert.Single(submenu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "新建标签…");
            var newTagIcon = Assert.IsType<PathIcon>(newTag.Icon);
            Assert.Equal(newTagIcon.TranslatePoint(default, newTag)!.Value.X, icon.TranslatePoint(default, red)!.Value.X, 2);
            var redHeader = red.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single(p => p.Name == "PART_HeaderPresenter");
            var newTagHeader = newTag.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single(p => p.Name == "PART_HeaderPresenter");
            Assert.Equal(newTagHeader.TranslatePoint(default, newTag)!.Value.X, redHeader.TranslatePoint(default, red)!.Value.X, 2);
            menu.Close();
        }
        finally { window.Close(); root.Delete(true); }
    }

    [AvaloniaFact]
    public async Task TagMenuAppliesToMultipleFilesAndFoldersAndCutPasteOnlyAddsTag()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-tag-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "a.txt");
        var folder = Path.Combine(root, "folder");
        await File.WriteAllTextAsync(path, "same");
        Directory.CreateDirectory(folder);
        var files = new FakeFileService(root);
        files.Seed(new FileSystemEntry { FullPath = path, Name = "a.txt" });
        files.Seed(new FileSystemEntry { FullPath = folder, Name = "folder", IsDirectory = true });
        using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        var clipboard = new TagTestClipboard();
        using var vm = CreateViewModel(files, fileTagService: tags, clipboardService: clipboard);
        try
        {
            var tag = await tags.CreateTagAsync("项目");
            await vm.RefreshAsync();
            vm.SetSelection(vm.Entries);
            var actions = await vm.LoadCompleteFileContextMenuAsync(vm.Entries[0]);
            var menu = Assert.Single(actions, a => a.Label == "标签");
            var toggle = Assert.Single(menu.SubItems!, a => a.Label == "项目");
            Assert.False(toggle.IsChecked);
            Assert.Equal(MacExplorer.Assets.Icons.Tag, toggle.IconSvg);
            Assert.Equal(tag.ColorHex, toggle.IconColor);
            await toggle.Execute!();
            Assert.Equal(2, (await tags.FindFilePathsAsync(tag)).Count);
            actions = await vm.LoadCompleteFileContextMenuAsync(vm.Entries[0]);
            toggle = Assert.Single(Assert.Single(actions, a => a.Label == "标签").SubItems!, a => a.Label == "项目");
            Assert.True(toggle.IsChecked);
            await toggle.Execute!();
            Assert.Empty(await tags.FindFilePathsAsync(tag));
            Assert.True(File.Exists(path));
            Assert.True(Directory.Exists(folder));

            clipboard.CutFiles([path, folder]);
            await vm.NavigateToTagAsync(tag);
            await vm.PasteAsync();
            Assert.Equal(0, files.MoveCalls);
            Assert.Equal(0, files.CopyCalls);
            Assert.True(clipboard.HasClipboardFiles);
            Assert.Equal(2, (await tags.FindFilePathsAsync(tag)).Count);
            Assert.Equal("same", await File.ReadAllTextAsync(path));

            var dropped = await tags.CreateTagAsync("拖入");
            var bridge = new MacDragDropBridge(files, new FakeDirectoryChangeNotifier(), fileTagService: tags);
            bridge.Register(vm);
            Assert.True(await bridge.DropFilesAsync([path, folder], dropped.VirtualPath, false, true));
            Assert.Equal(2, (await tags.FindFilePathsAsync(dropped)).Count);
            Assert.Equal(0, files.MoveCalls);
            Assert.Equal(0, files.CopyCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task TagViewPasteIgnoresClipboardImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-tag-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var files = new FakeFileService(root);
        using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        var clipboard = new TagTestClipboard { PasteKindOverride = ClipboardPasteKind.Image };
        using var vm = CreateViewModel(files, fileTagService: tags, clipboardService: clipboard);
        try
        {
            var tag = await tags.CreateTagAsync("项目");
            await vm.NavigateToTagAsync(tag);

            await vm.PasteAsync();

            Assert.Equal(0, files.CopyCalls);
            Assert.Equal(0, files.MoveCalls);
            Assert.Empty(files.CreatedFiles);
            Assert.Empty(await tags.FindFilePathsAsync(tag));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task SidebarCreatesEmptyTagWithEnterAndCancelsWithEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-tag-editor-" + Guid.NewGuid().ToString("N"));
        using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        using var vm = CreateViewModel(new FakeFileService(root), fileTagService: tags);
        var sidebar = new FinderSidebarView { DataContext = vm };
        var theme = new FluentTheme();
        Application.Current!.Styles.Insert(0, theme);
        var window = new Window { Content = sidebar, Width = 280, Height = 760 };
        try
        {
            window.Show();
            sidebar.FindControl<Button>("AddTagBtn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var input = sidebar.FindControl<TextBox>("NewTagInput")!;
            input.Text = "空项目";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Single(await tags.GetSidebarTagsAsync(), t => t.Name == "空项目" && t.ItemCount == 0);
            sidebar.FindControl<Button>("AddTagBtn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            input.Text = "不要创建";
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.DoesNotContain(await tags.GetSidebarTagsAsync(), t => t.Name == "不要创建");
            Dispatcher.UIThread.RunJobs();
            Assert.True(sidebar.FindControl<ItemsControl>("TagItems")!.IsVisible);
            Assert.True(sidebar.FindControl<ItemsControl>("TagItems")!.ItemCount >= 8);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var row = sidebar.FindControl<ItemsControl>("TagItems")!.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Tag is FileTag { Name: "空项目" });
            var point = row.TranslatePoint(new Point(40, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(row.ContextMenu);
            Assert.Null(sidebar.FindControl<Control>("CollectionsSectionHeader"));

            var path = Path.Combine(root, "drop.txt");
            await File.WriteAllTextAsync(path, "original");
            var tag = Assert.IsType<FileTag>(row.Tag);
            using var transfer = new DataTransfer();
            var storageItem = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            transfer.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(storageItem)));
            var sidebarDrop = new DragEventArgs(DragDrop.DropEvent, transfer, row, new Point(30, 10), KeyModifiers.None);
            row.RaiseEvent(sidebarDrop);
            Assert.True(sidebarDrop.Handled);
            Assert.Contains(path, await tags.FindFilePathsAsync(tag));
            await tags.SetTagAsync([path], tag, false);

            await vm.NavigateToTagAsync(tag);
            var list = new FileListView { DataContext = vm };
            window.Content = list;
            window.UpdateLayout();
            var surface = list.FindControl<Grid>("InteractionSurface")!;
            // A folder hovered before crossing onto blank tag space must not remain the drop target.
            typeof(FileListView).GetField("_dragOverTargetEntry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(list, new FileSystemEntry { Name = "folder", FullPath = root, IsDirectory = true });
            var over = new DragEventArgs(DragDrop.DragOverEvent, transfer, surface, new Point(200, 600), KeyModifiers.None);
            surface.RaiseEvent(over);
            Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            var drop = new DragEventArgs(DragDrop.DropEvent, transfer, surface, new Point(200, 600), KeyModifiers.None);
            surface.RaiseEvent(drop);
            Assert.True(drop.Handled);
            Assert.Contains(path, await tags.FindFilePathsAsync(tag));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
        }
        finally { window.Close(); Application.Current!.Styles.Remove(theme); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task TagRenameAndDeletionUpdateBothWindowsAndHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-tag-windows-" + Guid.NewGuid().ToString("N"));
        using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        using var first = CreateViewModel(new FakeFileService(root), fileTagService: tags);
        using var second = CreateViewModel(new FakeFileService(root), fileTagService: tags);
        using var secondTab = new ExplorerTabViewModel(second);
        try
        {
            var tag = await tags.CreateTagAsync("项目");
            await first.NavigateToTagAsync(tag);
            await second.NavigateToTagAsync(tag);
            await tags.RenameTagAsync(tag, "归档");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("归档", first.CurrentTag!.Name);
            Assert.Equal("归档", second.CurrentTag!.Name);
            await first.NavigateToAsync(root);
            await first.NavigateBackAsync();
            Assert.Equal("归档", first.CurrentTag!.Name);
            await first.NavigateForwardAsync();
            await tags.DeleteTagAsync(new("归档", "#8E8E93", FileTagKind.Custom));
            Dispatcher.UIThread.RunJobs();
            await first.NavigateBackAsync();
            Assert.True(first.IsHomePage);
            Assert.True(second.IsHomePage);
            Assert.Equal("首页", secondTab.Title);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TagTestClipboard : IClipboardService
    {
        private ClipboardEntry? _entry;
        public ClipboardPasteKind PasteKindOverride { get; set; } = ClipboardPasteKind.None;
        public bool HasClipboardFiles => _entry != null;
        public bool HasPasteableContent => HasClipboardFiles || PasteKindOverride != ClipboardPasteKind.None;
        public ClipboardPasteKind GetPasteKind() => HasClipboardFiles ? ClipboardPasteKind.InAppFiles : PasteKindOverride;
        public bool TryAdoptExternalFiles() => false;
        public ClipboardImageData? ReadExternalImage() => null;
        public void CopyFiles(string[] paths) => _entry = new ClipboardEntry { SourcePaths = paths.ToList(), Operation = ClipboardOperation.Copy };
        public void CutFiles(string[] paths) => _entry = new ClipboardEntry { SourcePaths = paths.ToList(), Operation = ClipboardOperation.Cut };
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task PasteFilesAsync(string targetDirectory) => throw new InvalidOperationException("Tag paste must not copy files");
        public ClipboardEntry? GetClipboardEntry() => _entry;
        public void Clear() => _entry = null;
    }

}
