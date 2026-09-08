using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FastFileListLayoutTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupHeadersHitTestingScrollingAndResizeShareFileGeometry(bool grid)
    {
        using var theme = new FastListTestTheme();
        var list = new FastFileList { IsGrid = grid };
        var rows = Enumerable.Range(0, 100_000).Select(FastFileListTests.Entry).ToArray();
        list.SetRows(rows, [new("First", 5), new("Large", 99_990), new("Last", 5)]);
        var host = new ScrollViewer { Content = list };
        var window = new Window { Width = 860, Height = 600, Content = host };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(list.EntryAt(new Point(20, 15)));
            Assert.Equal(34, list.RowBounds(0).Top);
            Assert.Equal(grid ? 7 : 1, list.GridColumns);
            using var target = new RenderTargetBitmap(new PixelSize(860, 600));
            foreach (var index in new[] { 0, 4, 5, 999, 45_678, 99_994, 99_999 })
            {
                list.ScrollToEntry(rows[index]);
                Dispatcher.UIThread.RunJobs();
                var cell = list.RowBounds(index);
                Assert.Same(rows[index], list.EntryAt(cell.Center));
                target.Render(list);
                Assert.InRange(list.LastRenderedRowCount, 1, grid ? 49 : 21);
                Assert.InRange(list.ObservedRowCount, 1, list.LastRenderedRowCount);
                Assert.InRange(list.CachedTextCount, 1, 1024);
                Assert.Empty(list.GetVisualChildren());
            }
            list.ScrollToEntry(rows[45_678], 0);
            var anchor = list.Rows[list.VisibleRange.First];
            var anchorY = list.RowBounds(list.IndexOf(anchor)).Y;
            window.Width = 620;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(anchorY, list.RowBounds(list.IndexOf(anchor)).Y);
            Assert.Equal(grid ? 5 : 1, list.GridColumns);
            list.ScrollToOffset(0);
            Dispatcher.UIThread.RunJobs();
            var first = list.RowBounds(0);
            var second = list.RowBounds(1);
            var rectangle = new Rect(first.Center, second.Center);
            Assert.Equal(rows.Take(2), list.EntriesInRectangle(rectangle));
            target.Render(list);
            Assert.True(list.LastRenderedHeaderCount > 0);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void VariableGridRowsKeepHeadersGapsAndViewportBoundariesAligned()
    {
        var layout = new FastFileListLayout();
        double[] heights = [120, 140, 120, 120, 125, 145, 120, 120, 130];
        layout.Build(9, [new("A", 5), new("B", 4)], true, 500, false, i => heights[i]);
        Assert.Equal(new Rect(0, 34, 120, 140), layout.Bounds(0));
        Assert.Equal(new Rect(0, 174, 120, 125), layout.Bounds(4));
        Assert.Equal(new Rect(0, 333, 120, 145), layout.Bounds(5));
        Assert.Equal(478, layout.Height);
        Assert.Equal(4, layout.IndexAt(new Point(20, 298)));
        Assert.Equal(-1, layout.IndexAt(new Point(140, 298)));
        Assert.Equal(-1, layout.IndexAt(new Point(20, 299)));
        Assert.Equal(5, layout.IndexAt(new Point(20, 333)));
        Assert.Equal(-1, layout.IndexAt(new Point(20, 478)));
        Assert.Equal((4, 9), layout.VisibleRange(290, 340));
        Assert.Equal((5, 5), layout.VisibleRange(299, 333));
        Assert.Equal((0, 4), layout.VisibleRange(34, 174));
    }

    [Fact]
    public void GridVerticalNavigationCrossesPartialGroupRowsWithoutSelectingHeaders()
    {
        var layout = new FastFileListLayout();
        layout.Build(15, [new("A", 5), new("B", 10)], true, 500, false);
        Assert.Equal(4, layout.Columns);
        Assert.Equal(4, layout.MoveVertical(2, 1));
        Assert.Equal(5, layout.MoveVertical(4, 1));
        Assert.Equal(4, layout.MoveVertical(7, -1));
        Assert.Equal(13, layout.MoveVertical(5, 2));
        Assert.Equal(14, layout.MoveVertical(7, 20));
        Assert.Equal(-1, layout.IndexAt(new Point(30, 34 + 116 * 2 + 15)));
        Assert.Equal(-1, layout.IndexAt(new Point(380, 34 + 116 + 15)));
        Assert.Equal(-1, layout.IndexAt(new Point(495, 50)));
    }

    [AvaloniaFact]
    public async Task VirtualCoverLoadsWithoutCallingLocalThumbnailProviderForSentinelPaths()
    {
        var images = new FastFileListImages();
        using var bitmap = new SkiaSharp.SKBitmap(8, 8);
        bitmap.Erase(SkiaSharp.SKColors.Blue);
        using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var cover = new FileSystemEntry
        {
            Name = "People", FullPath = "__ai_face__/1", IsVirtual = true, IsDirectory = true,
            ThumbnailUrl = "data:image/png;base64," + Convert.ToBase64String(png.ToArray())
        };
        var calls = 0;
        images.ThumbnailProvider = (_, _, _) => { calls++; return Task.FromResult<MacExplorer.Services.ThumbnailResult?>(null); };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        images.Changed += () => { if (images.CachedBytes > 0) loaded.TrySetResult(); };
        try
        {
            images.UpdateVisible([cover, new FileSystemEntry { FullPath = "remote://server/file.png", Extension = ".png" }], 128);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(images.Get(cover));
            Assert.Equal(0, calls);
        }
        finally { images.Clear(); }
    }
}
