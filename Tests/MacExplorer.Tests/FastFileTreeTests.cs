using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MacExplorer.Controls;
using MacExplorer.Models;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FastFileTreeTests
{
    [AvaloniaFact]
    public void TreeDisclosureAndIndentedNamesUseVisibleRowGeometry()
    {
        var parent = new FileSystemEntry { FullPath = "/tree/parent", Name = "parent", IsDirectory = true };
        var child = new FileSystemEntry { FullPath = "/tree/parent/child", Name = "child", IsDirectory = true };
        var leaf = new FileSystemEntry { FullPath = "/tree/parent/child/leaf.txt", Name = "leaf.txt" };
        var list = new FastFileList();
        list.Measure(new Size(700, 300));
        list.Arrange(new Rect(0, 0, 700, 300));
        list.SetTreeRows([
            new(parent, 0, true, true, false, false),
            new(child, 1, true, true, false, false),
            new(leaf, 2, false, false, false, false)
        ], []);

        using (var drawing = new DrawingGroup().Open()) list.Render(drawing);
        Assert.True(list.IsTree);
        Assert.Equal(3, list.LastRenderedRowCount);
        Assert.Equal(0, list.TreeDisclosureIndexAt(new Point(21, 15)));
        Assert.Equal(1, list.TreeDisclosureIndexAt(new Point(37, 45)));
        Assert.Equal(-1, list.TreeDisclosureIndexAt(new Point(53, 75)));
        Assert.True(list.NameBounds(0).Left < list.NameBounds(1).Left);
        Assert.True(list.NameBounds(1).Left < list.NameBounds(2).Left);

        list.SetRows([parent, child, leaf]);
        Assert.False(list.IsTree);
        Assert.Equal(-1, list.TreeDisclosureIndexAt(new Point(21, 15)));
    }

    [AvaloniaFact]
    public void NarrowDarkTreeKeepsDisclosureAndNameInsideViewport()
    {
        var folder = new FileSystemEntry { FullPath = "/tree/parent", Name = "很长的文件夹名称", IsDirectory = true };
        var child = new FileSystemEntry { FullPath = "/tree/parent/child.txt", Name = "很长的子文件名称.txt" };
        var list = new FastFileList
        {
            Background = Brushes.Black, Foreground = Brushes.White, Secondary = Brushes.LightGray
        };
        list.Measure(new Size(420, 120));
        list.Arrange(new Rect(0, 0, 420, 120));
        list.SetTreeRows([new(folder, 0, true, true, false, false),
            new(child, 1, false, false, false, false)], []);
        using var bitmap = new RenderTargetBitmap(new PixelSize(420, 120));
        bitmap.Render(list);

        Assert.Equal(0, list.TreeDisclosureIndexAt(new Point(21, 15)));
        Assert.InRange(list.NameBounds(1).Right, 1, 420);
        Assert.Equal(2, list.LastRenderedRowCount);
    }
}
