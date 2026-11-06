using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "TabStripLayout")]
public sealed class TabStripLayoutTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task DividersAtTabBoundariesDisappearOnBothSidesOfHoverAndSelection()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        while (fixture.Model.Tabs.Count < 5) fixture.AddTab();
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        var tabs = fixture.Window.FindControl<ListBox>("TabList")!;
        var surface = fixture.Window.FindControl<ChromiumTabStripSurface>("TabSurface")!;
        var headers = Enumerable.Range(0, tabs.Items.Count)
            .Select(index => Assert.IsType<ListBoxItem>(tabs.ContainerFromIndex(index))).ToArray();
        var header = headers[2];
        var title = header.GetVisualDescendants().OfType<TextBlock>().Single();
        var close = header.GetVisualDescendants().OfType<Button>().Single();
        var titleBounds = title.Bounds;
        var closeBounds = close.Bounds;
        AssertDividersAt(2, 3, 4);

        fixture.Window.MouseMove(header.TranslatePoint(new Point(4, header.Bounds.Height / 2), fixture.Window)!.Value);
        fixture.Window.UpdateLayout();

        Assert.True(header.IsPointerOver);
        AssertDividersAt(4);
        Assert.Equal(titleBounds, title.Bounds);
        Assert.Equal(closeBounds, close.Bounds);
        Assert.NotSame(header.DataContext, tabs.SelectedItem);

        fixture.Model.SelectedTab = fixture.Model.Tabs[3];
        AssertDividersAt(1);
        fixture.Model.SelectedTab = fixture.Model.Tabs[1];
        AssertDividersAt(4);

        fixture.Window.MouseMove(new Point(4, 4));
        Assert.False(header.IsPointerOver);
        AssertDividersAt(3, 4);

        void AssertDividersAt(params int[] rightHeaders)
        {
            var dividers = surface.GetDividerBounds().ToArray();
            Assert.Equal(rightHeaders.Length, dividers.Length);
            for (var i = 0; i < dividers.Length; i++)
            {
                var right = headers[rightHeaders[i]];
                var left = headers[rightHeaders[i] - 1];
                var seam = right.TranslatePoint(default, surface)!.Value.X;
                Assert.Equal(seam, left.TranslatePoint(new Point(left.Bounds.Width, 0), surface)!.Value.X, 4);
                Assert.InRange(dividers[i].Center.X, seam - 0.5, seam + 0.5);
            }
        }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TabsKeepOriginalWidthsAndOnlyShrinkWhenCrowded(bool dark)
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var tabs = fixture.Window.FindControl<ListBox>("TabList")!;
        var viewport = fixture.Window.FindControl<ScrollViewer>("TabScrollViewer")!;
        var add = fixture.Window.FindControl<Button>("NewTabButton")!;
        foreach (var longTitle in new[] { false, true })
        foreach (var count in new[] { 1, 5, 12, 24, 5, 1 })
        {
            while (fixture.Model.Tabs.Count < count) fixture.AddTab();
            while (fixture.Model.Tabs.Count > count)
            {
                var removed = fixture.Model.Tabs[^1];
                fixture.Model.RemoveTab(removed);
                removed.Dispose();
                removed.FileList.Dispose();
            }
            foreach (var tab in fixture.Model.Tabs)
                tab.Title = longTitle ? "很长的文件夹名称 A folder with a very long name that must stay inside its tab" : "桌面";
            foreach (var width in new[] { 1468d, 800d, 1280d })
            {
                fixture.Window.Width = width;
                Dispatcher.UIThread.RunJobs();
                fixture.Window.UpdateLayout();
                var headers = Enumerable.Range(0, count)
                    .Select(i => (ListBoxItem)tabs.ContainerFromIndex(i)!).ToArray();
                Assert.All(headers, header => Assert.True(header.Bounds.Width > 0));
                var last = headers[^1];
                var end = last.TranslatePoint(new Point(last.Bounds.Width, 0), viewport)!.Value.X;
                var expectedExtent = Math.Max(viewport.Bounds.Width, count * 112);
                Assert.InRange(end, expectedExtent - 1, expectedExtent + 1);
                Assert.InRange(add.TranslatePoint(default, viewport)!.Value.X - viewport.Bounds.Width, 5, 7);
                Assert.Equal(0, viewport.Offset.X);
                if (count == 1)
                    Assert.InRange(headers[0].Bounds.Width, longTitle ? 279 : 179, longTitle ? 281 : 181);
                if (count == 5 && width == 1468 && !longTitle)
                {
                    Assert.All(headers, header => Assert.InRange(header.Bounds.Width, 179, 181));
                    Assert.True(end > 840);
                }
                if (count >= 12) Assert.All(headers, header => Assert.True(header.Bounds.Width < 180));
                Assert.All(headers.SelectMany(header => header.GetVisualDescendants().OfType<TextBlock>()),
                    text => Assert.True(text.ClipToBounds));
            }
        }
    }
}
