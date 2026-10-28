using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "TabStripInteraction")]
public sealed class TabStripInteractionTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task NewTabInsertsBesideAnchorAndRightCloseKeepsUnrelatedSelection()
    {
        var root = Directory.CreateTempSubdirectory("tab-insert-").FullName;
        try
        {
            using var theme = new FastListTestTheme();
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(new FakeFileService(root))));
            var first = fixture.Model.SelectedTab;
            var anchor = fixture.AddTab();
            var last = fixture.AddTab();
            await anchor.FileList.NavigateToAsync(root);
            await fixture.Window.OpenTabToRightAsync(anchor);
            var created = fixture.Model.Tabs[2];
            Assert.Equal(root, created.FileList.CurrentPath);
            Assert.Same(created, fixture.Model.SelectedTab);
            Assert.Same(last, fixture.Model.Tabs[3]);
            Assert.Null(fixture.Workspaces[created].FindControl<ContentControl>("HomeViewHost")!.Content);
            fixture.Model.SelectedTab = first;
            await fixture.Window.CloseTabsAroundAsync(anchor, MainWindow.TabCloseRange.Right);
            Assert.Equal(new[] { first, anchor }, fixture.Model.Tabs);
            Assert.Same(first, fixture.Model.SelectedTab);
            anchor.FileList.GoHome();
            await fixture.Window.OpenTabToRightAsync(anchor);
            Assert.True(fixture.Model.Tabs[2].FileList.IsHomePage);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(800)]
    [InlineData(1280)]
    public async Task DragPreviewsBeforeCommitAndPreservesSelectionAndPanes(double width)
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        fixture.Window.Width = width;
        while (fixture.Model.Tabs.Count < 5) fixture.AddTab();
        fixture.Model.Tabs[0].Title = "Long tab title with different preferred width";
        fixture.Model.SetPaneLayout(PaneLayout.TwoColumns);
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        var before = fixture.Model.Tabs.ToArray();
        var panes = fixture.Model.VisiblePanes.ToArray();
        var selected = fixture.Model.SelectedTab;
        var workspaces = fixture.Workspaces.ToDictionary();
        var list = fixture.Window.FindControl<ListBox>("TabList")!;
        var source = (ListBoxItem)list.ContainerFromItem(before[1])!;
        var last = (ListBoxItem)list.ContainerFromItem(before[^1])!;
        var start = source.TranslatePoint(new Point(35, 16), fixture.Window)!.Value;
        var end = last.TranslatePoint(new Point(last.Bounds.Width - 8, 16), fixture.Window)!.Value;
        fixture.Window.MouseDown(start, MouseButton.Left);
        fixture.Window.MouseMove(end); fixture.Window.UpdateLayout();
        Assert.Equal(before, fixture.Model.Tabs);
        Assert.True(source.GetVisualDescendants().OfType<TabDragSurface>().Single().IsVisible);
        Assert.True(source.TranslatePoint(default, fixture.Window)!.Value.X > start.X);
        fixture.Window.MouseUp(end, MouseButton.Left); Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.Equal(new[] { before[0], before[2], before[3], before[4], before[1] }, fixture.Model.Tabs);
        Assert.Same(selected, fixture.Model.SelectedTab);
        Assert.Equal(panes, fixture.Model.VisiblePanes);
        foreach (var pair in workspaces) Assert.Same(pair.Value, fixture.Workspaces[pair.Key]);
        source = (ListBoxItem)list.ContainerFromItem(before[1])!;
        Assert.False(source.GetVisualDescendants().OfType<TabDragSurface>().Single().IsVisible);

        // Drag the same header back to the beginning, then cancel without changing the order.
        source = (ListBoxItem)list.ContainerFromItem(before[1])!;
        start = source.TranslatePoint(new Point(35, 16), fixture.Window)!.Value;
        end = ((ListBoxItem)list.ContainerFromIndex(0)!).TranslatePoint(new Point(2, 16), fixture.Window)!.Value;
        var sorted = fixture.Model.Tabs.ToArray();
        fixture.Window.MouseDown(start, MouseButton.Left); fixture.Window.MouseMove(end);
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.Equal(sorted, fixture.Model.Tabs);
        fixture.Window.MouseDown(start, MouseButton.Left); fixture.Window.MouseMove(end);
        fixture.Window.MouseUp(end, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.Same(before[1], fixture.Model.Tabs[0]);
        fixture.Model.SelectedTab = before[1];
        Assert.Same(before[0], fixture.Model.SelectRelativeTab(1));
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ContextMenuTargetsInactiveTabAndBatchCloseReleasesWorkspaces()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        while (fixture.Model.Tabs.Count < 5) fixture.AddTab();
        var tabs = fixture.Model.Tabs.ToArray();
        fixture.Model.SetPaneLayout(PaneLayout.FourGrid);
        fixture.Model.SelectedTab = tabs[0];
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        var removedWorkspaces = fixture.Workspaces.Where(p => p.Key != tabs[2]).Select(p => p.Value).ToArray();
        var list = fixture.Window.FindControl<ListBox>("TabList")!;
        var header = (ListBoxItem)list.ContainerFromItem(tabs[2])!;
        var point = header.TranslatePoint(new Point(35, 16), fixture.Window)!.Value;
        fixture.Window.MouseDown(point, MouseButton.Right); fixture.Window.MouseUp(point, MouseButton.Right);
        Assert.Same(tabs[0], fixture.Model.SelectedTab);
        var menu = (ContextMenu)typeof(MainWindow).GetField("_tabContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Window)!;
        Assert.Equal(5, menu.Items.Count); Assert.IsType<Separator>(menu.Items[1]);
        Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.True(item.IsEnabled));
        menu.Close();
        await fixture.Window.CloseTabsAroundAsync(tabs[2], MainWindow.TabCloseRange.Left);
        Assert.Equal(new[] { tabs[2], tabs[3], tabs[4] }, fixture.Model.Tabs);
        Assert.Same(tabs[2], fixture.Model.SelectedTab);
        await fixture.Window.CloseTabsAroundAsync(tabs[2], MainWindow.TabCloseRange.Others);
        Assert.Same(tabs[2], Assert.Single(fixture.Model.Tabs));
        Assert.Equal(PaneLayout.Single, fixture.Model.PaneLayout);
        Assert.All(removedWorkspaces, workspace => Assert.True(workspace.IsDisposed));
        fixture.Window.ShowTabContextMenu((Control)list.ContainerFromItem(tabs[2])!, tabs[2]);
        menu = (ContextMenu)typeof(MainWindow).GetField("_tabContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Window)!;
        Assert.All(menu.Items.OfType<MenuItem>().Skip(1), item => Assert.False(item.IsEnabled));
        menu.Close();
    }
}
