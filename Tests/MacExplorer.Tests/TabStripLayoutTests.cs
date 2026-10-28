using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "TabStripLayout")]
public sealed class TabStripLayoutTests : FileListViewModelTestBase
{
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
                Assert.InRange(end, viewport.Bounds.Width - 1, viewport.Bounds.Width + 1);
                Assert.InRange(add.TranslatePoint(default, viewport)!.Value.X - end, -1, 1);
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
