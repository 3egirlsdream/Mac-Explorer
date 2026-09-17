using Avalonia;
using Avalonia.Headless.XUnit;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewListBoundsTests
{
    [AvaloniaFact]
    public void RightCanvasDoesNotSelectRowsButCrossingIntoTheRowDoes()
    {
        var list = new FastFileList();
        var rows = Enumerable.Range(0, 3).Select(FastFileListTests.Entry).ToArray();
        list.SetRows(rows);
        list.Measure(new Size(900, 300));
        list.Arrange(new Rect(0, 0, 900, 300));
        Assert.Equal(845, list.RowBounds(0).Right);
        Assert.Empty(list.EntriesInRectangle(new Rect(850, 0, 40, 90)));
        Assert.Empty(list.EntriesInRectangle(new Rect(-30, 0, 20, 90)));
        Assert.Equal(rows, list.EntriesInRectangle(new Rect(840, 0, 50, 90)).ToArray());
        Assert.Equal(rows, list.RowsWithCentersIn(0, 90).ToArray());
    }

    [AvaloniaFact]
    public void NarrowViewportClipsRowsAndGridStillUsesItsCells()
    {
        var list = new FastFileList();
        list.SetRows(Enumerable.Range(0, 20).Select(FastFileListTests.Entry).ToArray());
        list.Measure(new Size(600, 300));
        list.Arrange(new Rect(0, 0, 600, 300));
        Assert.Equal(600, list.RowBounds(0).Right);
        list.IsGrid = true;
        list.Measure(new Size(1000, 300));
        list.Arrange(new Rect(0, 0, 1000, 300));
        Assert.NotEmpty(list.EntriesInRectangle(new Rect(850, 0, 150, 280)));
    }
}
