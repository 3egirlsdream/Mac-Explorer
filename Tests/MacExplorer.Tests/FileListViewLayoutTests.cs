using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileList")]
public sealed class FileListViewLayoutViewModelTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void FileListContentHitTargetsLeaveWhitespaceForMarquee(bool grid)
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/layout"),
            sortFilter: new SortFilterViewModel { ViewMode = grid ? ViewMode.Grid : ViewMode.List });
        var entry = new FileSystemEntry { Name = "short.txt", FullPath = "/tmp/layout/short.txt" };
        vm.Entries.Add(entry);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 760, Height = 480, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<FastFileList>("FastList")!;
            Assert.Same(entry, list.EntryAt(list.NameBounds(0).Center, contentOnly: true));
            if (grid)
            {
                Assert.Same(entry, list.EntryAt(list.GridIconTargetBounds(0).Center, contentOnly: true));
                Assert.Null(list.EntryAt(new Point(115, 50), contentOnly: true));
            }
            else
            {
                Assert.True(list.NameBounds(0).Width < list.ColumnWidths.Name - 16);
                Assert.Null(list.EntryAt(new Point(list.ListRowRight - 4, 15), contentOnly: true));
                Assert.Same(entry, list.EntryAt(new Point(list.ListRowRight - 4, 15)));
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public void FastListAndHeaderShareColumnWidthsAfterResizeAndLongNamesStayWithinNameColumn()
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/layout"));
        vm.Entries.Add(new FileSystemEntry { Name = new string('W', 200) + ".txt", FullPath = "/tmp/layout/long.txt" });
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 480, Content = view };
        try
        {
            window.Show();
            foreach (var width in new[] { 900d, 600d })
            {
                window.Width = width;
                Dispatcher.UIThread.RunJobs();
                var list = view.FindControl<FastFileList>("FastList")!;
                var header = view.FindControl<Grid>("ListHeaderGrid")!;
                Assert.Equal(header.ColumnDefinitions[1].Width.Value, list.ColumnWidths.Name);
                Assert.Equal(header.ColumnDefinitions[2].Width.Value, list.ColumnWidths.Modified);
                Assert.Equal(header.ColumnDefinitions[3].Width.Value, list.ColumnWidths.Size);
                Assert.Equal(header.ColumnDefinitions[4].Width.Value, list.ColumnWidths.Type);
                Assert.True(list.NameBounds(0).Right <= 12 + 22 + list.ColumnWidths.Name);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public void HeaderCheckboxesStayOpenAcrossTogglesAndRestoreColumnWidth()
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/layout"),
            sortFilter: new SortFilterViewModel { ViewMode = ViewMode.List });
        var entry = new FileSystemEntry { Name = "report.pdf", Extension = ".pdf", FullPath = "/tmp/layout/report.pdf" };
        vm.Entries.Add(entry);
        vm.ColumnLayoutService.Preview(FileListColumn.Name, 320);
        vm.ColumnLayoutService.Preview(FileListColumn.Type, 190);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 480, Content = view };
        try
        {
            window.Show();
            Settle(window);
            var list = view.FindControl<FastFileList>("FastList")!;
            var header = view.FindControl<Grid>("ListHeaderGrid")!;
            var button = view.FindControl<AppButton>("ListColumnsButton")!;
            var popup = view.FindControl<Popup>("ColumnVisibilityPopup")!;
            var modified = view.FindControl<CheckBox>("ShowModifiedColumn")!;
            var size = view.FindControl<CheckBox>("ShowSizeColumn")!;
            var kind = view.FindControl<CheckBox>("ShowTypeColumn")!;
            var kindPoint = new Point(list.ListRowRight - 185, 15);
            Assert.Same(entry, list.EntryAt(kindPoint, contentOnly: true));

            Click(window, Center(button, window));
            Assert.True(popup.IsOpen);
            Assert.All(new[] { modified, size, kind }, item => Assert.True(item.IsChecked));
            Assert.Same(modified, window.FocusManager!.GetFocusedElement());
            Click(window, Center(kind, window));
            Assert.True(popup.IsOpen);
            Assert.Equal(0, header.ColumnDefinitions[4].Width.Value);
            Assert.Equal(0, list.ColumnWidths.Type);
            Assert.False(view.FindControl<Border>("TypeResizeHandle")!.IsVisible);
            Assert.Null(list.EntryAt(kindPoint, contentOnly: true));

            Click(window, Center(size, window));
            Assert.True(popup.IsOpen);
            Assert.Equal(0, list.ColumnWidths.Size);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Settle(window);
            Assert.True(popup.IsOpen);
            Assert.Equal(110, list.ColumnWidths.Size);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle(window);
            Assert.False(popup.IsOpen);
            Assert.Same(button, window.FocusManager.GetFocusedElement());
            var sizeHandle = view.FindControl<Border>("SizeResizeHandle")!;
            Drag(window, sizeHandle, 20);
            Assert.Equal(130, list.ColumnWidths.Size);
            Drag(window, sizeHandle, -20);
            Assert.Equal(110, list.ColumnWidths.Size);

            Click(window, Center(button, window));
            Assert.True(popup.IsOpen);
            Assert.False(kind.IsChecked);
            Click(window, Center(kind, window));
            Assert.True(popup.IsOpen);
            Assert.Equal(190, list.ColumnWidths.Type);
            Assert.Same(entry, list.EntryAt(kindPoint, contentOnly: true));
            Click(window, new Point(20, 400));
            Assert.False(popup.IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("Name", 1)]
    [InlineData("Modified", 2)]
    [InlineData("Size", 3)]
    [InlineData("Type", 4)]
    public void HeaderDividersMoveBothWaysAndStayAlignedWithFileRows(string name, int index)
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/layout"),
            sortFilter: new SortFilterViewModel { ViewMode = ViewMode.List });
        var entry = new FileSystemEntry { Name = "Quarterly project report.pdf", FullPath = "/tmp/layout/report.pdf" };
        vm.Entries.Add(entry);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 1100, Height = 480, Content = view };
        try
        {
            window.Show();
            Settle(window);
            var list = view.FindControl<FastFileList>("FastList")!;
            var host = view.FindControl<ScrollViewer>("FastListHost")!;
            var handle = view.FindControl<Border>(name + "ResizeHandle")!;
            var last = view.FindControl<Border>("TypeResizeHandle")!;
            var header = view.FindControl<Grid>("ListHeaderGrid")!;
            var button = view.FindControl<AppButton>("ListColumnsButton")!;
            var originalWidth = header.ColumnDefinitions[index].Width.Value;
            var originalNamePosition = list.NameBounds(0).X;
            var originalButtonPosition = Center(button, window);
            Assert.True(list.NameBounds(0).Width > 140);
            Assert.Equal(0, host.Offset.X);

            Drag(window, handle, -25);
            Assert.Equal(originalWidth - 25, header.ColumnDefinitions[index].Width.Value, 1);
            Drag(window, handle, 45);
            Assert.Equal(originalWidth + 20, header.ColumnDefinitions[index].Width.Value, 1);
            Assert.True(list.Extent.Width > list.Viewport.Width);

            host.Offset = new Vector(10000, 0);
            Settle(window);
            Assert.True(list.Offset.X > 0);
            Assert.Equal(originalNamePosition - list.Offset.X, list.NameBounds(0).X, 1);
            var lastRight = last.TranslatePoint(new Point(last.Bounds.Width, last.Bounds.Height / 2), window)!.Value;
            var rowRight = list.TranslatePoint(new Point(list.ListRowRight, 15), window)!.Value;
            Assert.Equal(rowRight.X, lastRight.X, 0);
            Assert.Equal(originalButtonPosition, Center(button, window));

            host.Offset = default;
            Settle(window);
            if (index == 1)
            {
                var nameDivider = Center(handle, window);
                Click(window, nameDivider);
                Click(window, nameDivider);
                Assert.Equal(originalWidth, list.ColumnWidths.Name, 1);
            }
            var oldLast = Center(last, window);
            window.Width += 150;
            Settle(window);
            Assert.Equal(oldLast.X + 150, Center(last, window).X, 0);
            Assert.Equal(originalButtonPosition.X + 150, Center(button, window).X, 0);
        }
        finally { window.Close(); }
    }

    private static void Drag(Window window, Border divider, double delta)
    {
        var start = Center(divider, window);
        var end = start + new Vector(delta, 0);
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Settle(window);
        Assert.Equal(end.X, Center(divider, window).X, 0);
        window.MouseUp(end, MouseButton.Left);
        Settle(window);
    }

    private static Point Center(Control control, Visual root)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;

    private static void Click(TopLevel root, Point point)
    {
        root.MouseMove(point);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        Settle(root);
    }

    private static void Settle(TopLevel root)
    {
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

}
