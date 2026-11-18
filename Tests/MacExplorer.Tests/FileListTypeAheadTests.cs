using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileList")]
public sealed class FileListTypeAheadTests : FileListViewModelTestBase
{
    private const string Root = "/tmp/FastListTypeAhead";

    [AvaloniaTheory(Explicit = true)]
    [InlineData(ViewMode.List, false)]
    [InlineData(ViewMode.List, true)]
    [InlineData(ViewMode.Grid, false)]
    [InlineData(ViewMode.Grid, true)]
    public async Task TextInputReplacesSelectionAndRevealsOffscreenMatch(ViewMode mode, bool grouped)
    {
        var files = new FakeFileService(Root);
        files.Seed(TreeEntry(Root, "Alpha.txt"));
        for (var i = 0; i < 100; i++) files.Seed(TreeEntry(Root, $"Note-{i:D2}.txt"));
        files.Seed(TreeEntry(Root, "Zulu.txt"));
        using var vm = CreateViewModel(files);
        vm.SetViewMode(mode);
        vm.GroupField = grouped ? GroupField.Type : GroupField.None;
        await vm.RefreshAsync();
        using var host = new ListHost(vm);
        var list = host.List;
        var target = list.Rows.Single(e => e.Name == "Zulu.txt");
        var index = list.IndexOf(target);
        Assert.True(index >= list.VisibleRange.End);
        vm.SetSelection(list.Rows.Take(2));
        Assert.True(list.Focus());

        host.Window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
        host.Window.KeyTextInput("a");
        host.Window.KeyRelease(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
        Assert.Equal("Alpha.txt", Assert.Single(vm.SelectedEntries).Name);

        host.Window.KeyPress(Key.Z, RawInputModifiers.None, PhysicalKey.Z, "z");
        host.Window.KeyTextInput("z");
        host.Window.KeyRelease(Key.Z, RawInputModifiers.None, PhysicalKey.Z, "z");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(target, Assert.Single(vm.SelectedEntries));
        Assert.InRange(index, list.VisibleRange.First, list.VisibleRange.End - 1);
        Assert.Equal(target.FullPath, list.KeyboardFocusPath);
        var grid = mode == ViewMode.Grid;
        host.Window.KeyPress(grid ? Key.Left : Key.Up, RawInputModifiers.None,
            grid ? PhysicalKey.ArrowLeft : PhysicalKey.ArrowUp, null);
        Assert.Same(list.Rows[index - 1], Assert.Single(vm.SelectedEntries));
    }

    [AvaloniaFact(Explicit = true)]
    public void ShortcutTextAndInlineRenameDoNotRetargetSelection()
    {
        using var vm = CreateViewModel(new FakeFileService(Root));
        var original = TreeEntry(Root, "Original.txt");
        vm.Entries.Add(original);
        vm.Entries.Add(TreeEntry(Root, "Juniper.txt"));
        using var host = new ListHost(vm);
        vm.SelectEntry(original);
        Assert.True(host.List.Focus());
        foreach (var modifier in new[] { RawInputModifiers.Meta, RawInputModifiers.Control, RawInputModifiers.Alt })
        {
            host.Window.KeyPress(Key.J, modifier, PhysicalKey.J, "j");
            host.Window.KeyTextInput("j");
            host.Window.KeyRelease(Key.J, modifier, PhysicalKey.J, "j");
            Assert.Same(original, Assert.Single(vm.SelectedEntries));
        }

        vm.RequestRename(original);
        Dispatcher.UIThread.RunJobs();
        var overlay = host.View.FindControl<Canvas>("FastRenameOverlay")!;
        var editor = Assert.IsType<TextBox>(Assert.Single(overlay.Children));
        Assert.True(editor.IsFocused);
        host.Window.KeyTextInput("j");
        Assert.Equal("j.txt", editor.Text);
        Assert.Same(original, Assert.Single(vm.SelectedEntries));
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Empty(overlay.Children);
        Assert.Equal("Original.txt", original.Name);
    }

    [AvaloniaFact(Explicit = true)]
    public void TextInputDoesNotSelectAFilteredOutFile()
    {
        using var host = new HeaderFilterHost();
        host.Vm.FileNameFilter = "Alpha";
        Dispatcher.UIThread.RunJobs();
        var list = host.View.FindControl<FastFileList>("FastList")!;
        Assert.DoesNotContain(list.Rows, row => row.Name == "Zulu.txt");
        Assert.True(list.Focus());
        host.Vm.SelectEntry(list.Rows[0]);
        var offset = list.Viewport;

        host.Window.KeyTextInput("z");

        Assert.Same(list.Rows[0], Assert.Single(host.Vm.SelectedEntries));
        Assert.Equal(offset, list.Viewport);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task TreeTextInputFindsChildOnlyAfterItsBranchIsExpanded()
    {
        var files = new FakeFileService(Root);
        files.Seed(TreeEntry(Root, "Folder", folder: true));
        files.Seed(TreeEntry(Path.Combine(Root, "Folder"), "Child.txt"));
        using var vm = CreateViewModel(files);
        vm.SetViewMode(ViewMode.Tree);
        await vm.RefreshAsync();
        using var host = new ListHost(vm);
        var folder = Assert.Single(host.List.Rows);
        vm.SelectEntry(folder);
        Assert.True(host.List.Focus());
        host.Window.KeyTextInput("c");
        Assert.Same(folder, Assert.Single(vm.SelectedEntries));
        Assert.Single(host.List.Rows);

        await vm.ExpandTreeDirectoryAsync(folder);
        Dispatcher.UIThread.RunJobs();
        host.Window.KeyTextInput("c");
        Assert.Equal(Path.Combine(folder.FullPath, "Child.txt"), Assert.Single(vm.SelectedEntries).FullPath);
    }

    [AvaloniaFact(Explicit = true)]
    public void GalleryTextInputSelectsAndRevealsVirtualizedFilmstripItem()
    {
        using var vm = CreateViewModel(new FakeFileService(Root));
        vm.SetViewMode(ViewMode.Grid);
        for (var i = 0; i < 100; i++) vm.Entries.Add(TreeEntry(Root, $"Note-{i:D2}.txt", readable: false));
        var target = TreeEntry(Root, "Zulu.txt", readable: false);
        vm.Entries.Add(target);
        using var host = new ListHost(vm);
        vm.SetThumbnailZoomLevel(FileThumbnailSizing.GalleryLevel);
        Dispatcher.UIThread.RunJobs();
        var strip = host.View.GalleryFilmstrip!;
        Assert.True(strip.Focus());
        Assert.Null(strip.ContainerFromIndex(100));

        host.Window.KeyTextInput("n");
        Assert.StartsWith("Note-", Assert.Single(vm.SelectedEntries).Name);
        host.Window.KeyTextInput("z");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(target, Assert.Single(vm.SelectedEntries));
        Assert.Same(target, strip.SelectedItem);
        Assert.NotNull(strip.ContainerFromIndex(100));
    }

    private sealed class ListHost : IDisposable
    {
        private readonly FastListTestTheme _theme = new();
        public FileListView View { get; }
        public FastFileList List => View.FindControl<FastFileList>("FastList")!;
        public Window Window { get; }

        public ListHost(FileListViewModel vm)
        {
            View = new FileListView { DataContext = vm };
            Window = new Window { Width = 700, Height = 360, Content = View };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            _theme.Dispose();
        }
    }
}
