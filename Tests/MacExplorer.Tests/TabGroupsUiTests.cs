using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "TabGroups")]
public sealed class TabGroupsUiTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraggingInactiveGroupMovesTheWholeBlockWithoutActivatingIt(bool collapsed)
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        while (fixture.Model.Tabs.Count < 4) fixture.AddTab();
        var model = fixture.Model;
        var tabs = model.Tabs.ToArray();
        var first = model.CreateGroup(tabs.Take(2));
        model.SetPaneLayout(PaneLayout.TwoRows);
        var second = model.CreateGroup(tabs.Skip(2));
        model.ActivateGroup(second);
        model.SetPaneLayout(PaneLayout.TwoColumns);
        if (collapsed) model.ToggleGroup(second);
        model.ActivateGroup(first);
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        var selected = model.SelectedTab;
        var panes = model.VisiblePanes.ToArray();
        var secondPanes = second.Panes.ToArray();
        var list = fixture.Window.FindControl<ListBox>("TabList")!;
        var source = (Control)list.ContainerFromItem(second)!;
        var destination = (Control)list.ContainerFromItem(first)!;
        var start = source.TranslatePoint(new Point(source.Bounds.Width / 2, 16), fixture.Window)!.Value;
        var end = destination.TranslatePoint(new Point(3, 16), fixture.Window)!.Value;
        fixture.Window.MouseDown(start, MouseButton.Left);
        fixture.Window.MouseMove(end);
        fixture.Window.UpdateLayout();
        Assert.Equal(tabs, model.Tabs);
        Assert.Same(selected, model.SelectedTab);
        fixture.Window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.Equal(tabs.Skip(2).Concat(tabs.Take(2)), model.Tabs);
        Assert.Same(selected, model.SelectedTab);
        Assert.Equal(panes, model.VisiblePanes);
        Assert.Equal(secondPanes, second.Panes);
        Assert.Equal(collapsed, second.IsCollapsed);
        Assert.Equal(PaneLayout.TwoRows, model.PaneLayout);

        source = (Control)list.ContainerFromItem(second)!;
        destination = (Control)list.ContainerFromItem(tabs[1])!;
        start = source.TranslatePoint(new Point(source.Bounds.Width / 2, 16), fixture.Window)!.Value;
        end = destination.TranslatePoint(new Point(destination.Bounds.Width - 4, 16), fixture.Window)!.Value;
        var before = model.Tabs.ToArray();
        fixture.Window.MouseDown(start, MouseButton.Left);
        fixture.Window.MouseMove(end);
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Window.MouseUp(end, MouseButton.Left);
        Assert.Equal(before, model.Tabs);
        Assert.Equal(panes, model.VisiblePanes);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ClickingInactiveGroupOnlyTogglesItsHeadersAndAccent()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        while (fixture.Model.Tabs.Count < 4) fixture.AddTab();
        var model = fixture.Model;
        var tabs = model.Tabs.ToArray();
        model.CreateGroup(tabs.Take(2));
        model.SetPaneLayout(PaneLayout.TwoRows);
        var inactive = model.CreateGroup(tabs.Skip(2));
        var selected = model.SelectedTab;
        var panes = model.VisiblePanes.ToArray();
        var list = fixture.Window.FindControl<ListBox>("TabList")!;

        foreach (var collapsed in new[] { true, false })
        {
            Dispatcher.UIThread.RunJobs();
            fixture.Window.UpdateLayout();
            var header = (Control)list.ContainerFromItem(inactive)!;
            var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, 16), fixture.Window)!.Value;
            fixture.Window.MouseDown(point, MouseButton.Left);
            fixture.Window.MouseUp(point, MouseButton.Left);
            if (!MacWindowChrome.PrefersReducedMotion)
            {
                Assert.True(fixture.Window.TabGroupMotion.IsRunning);
                Assert.Equal(collapsed, inactive.IsCollapsed);
            }
            await WaitForGroupMotionAsync(fixture.Window);

            Assert.Equal(collapsed, inactive.IsCollapsed);
            Assert.Equal(!collapsed, header.GetVisualDescendants().OfType<TabGroupLine>().Single().IsVisible);
            Assert.Equal(!collapsed, list.Items.Contains(tabs[2]));
            Assert.Same(selected, model.SelectedTab);
            Assert.Equal(panes, model.VisiblePanes);
            Assert.Equal(PaneLayout.TwoRows, model.PaneLayout);
        }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ClickingGroupCollapsesHeadersAndClosingLastGroupCreatesHome()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(new FakeFileService("/tmp/groups"))));
        var model = fixture.Model;
        while (model.Tabs.Count < 5) fixture.AddTab();
        var group = model.CreateGroup(model.Tabs.ToArray());
        model.SetPaneLayout(PaneLayout.FourGrid);
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        var list = fixture.Window.FindControl<ListBox>("TabList")!;
        var header = (Control)list.ContainerFromItem(group)!;
        var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, 16), fixture.Window)!.Value;
        var panes = model.VisiblePanes.ToArray();
        var workspaces = fixture.Workspaces.Values.ToArray();
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        await WaitForGroupMotionAsync(fixture.Window);
        Assert.True(group.IsCollapsed);
        Assert.Same(group, Assert.Single(list.Items));
        Assert.Equal(panes, model.VisiblePanes);
        Assert.Equal(4, fixture.Workspaces.Values.Count(workspace => workspace.IsVisible));
        await fixture.Window.CloseGroupAsync(group);
        Assert.Empty(model.Groups);
        var home = Assert.Single(model.Tabs);
        Assert.True(home.FileList.IsHomePage);
        Assert.Same(home, model.SelectedTab);
        Assert.Same(home, Assert.Single(model.VisiblePanes));
        Assert.Equal(PaneLayout.Single, model.PaneLayout);
        Assert.All(workspaces, workspace => Assert.True(workspace.IsDisposed));
    }

    private static async Task WaitForGroupMotionAsync(MainWindow window)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (window.TabGroupMotion.IsRunning)
            await Task.Delay(10, timeout.Token);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [AvaloniaFact(Explicit = true)]
    public async Task DragPreviewFollowsGroupBoundariesAndEscapeRestoresAppearance()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        fixture.Window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        while (fixture.Model.Tabs.Count < 5) fixture.AddTab();
        var model = fixture.Model;
        var tabs = model.Tabs.ToArray();
        var first = model.CreateGroup(tabs.Take(2));
        model.SetPaneLayout(PaneLayout.TwoColumns);
        var second = model.CreateGroup(tabs.Skip(2).Take(2));
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        var list = fixture.Window.FindControl<ListBox>("TabList")!;
        var source = (Control)list.ContainerFromItem(tabs[1])!;
        var line = source.GetVisualDescendants().OfType<TabGroupLine>().Single();
        Assert.Equal(2, line.Height);
        Assert.Same(first.ColorBrush, line.Background);
        var outside = (Control)list.ContainerFromItem(tabs[4])!;
        var other = (Control)list.ContainerFromItem(second)!;
        var start = source.TranslatePoint(new Point(35, 16), fixture.Window)!.Value;
        var outsidePoint = outside.TranslatePoint(new Point(outside.Bounds.Width / 2, 16), fixture.Window)!.Value;
        var otherPoint = other.TranslatePoint(new Point(other.Bounds.Width / 2, 16), fixture.Window)!.Value;
        var panes = model.VisiblePanes.ToArray();
        fixture.Window.MouseDown(start, MouseButton.Left);
        fixture.Window.MouseMove(outsidePoint); fixture.Window.UpdateLayout();
        Assert.Null(line.PreviewGroup);
        Assert.Same(first, tabs[1].Group);
        Assert.Equal(tabs, model.Tabs);
        Assert.Equal(panes, model.VisiblePanes);
        fixture.Window.MouseMove(otherPoint); fixture.Window.UpdateLayout();
        Assert.Same(second, line.PreviewGroup);
        Assert.Same(second.ColorBrush, line.Background);
        Assert.True(source.TranslatePoint(default, fixture.Window)!.Value.X
            >= other.TranslatePoint(new Point(other.Bounds.Width, 0), fixture.Window)!.Value.X - 1);
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Window.MouseUp(otherPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.Same(first, line.PreviewGroup);
        Assert.Same(first.ColorBrush, line.Background);
        Assert.Same(first, tabs[1].Group);
        Assert.Equal(panes, model.VisiblePanes);

        source = (Control)list.ContainerFromItem(tabs[1])!;
        start = source.TranslatePoint(new Point(35, 16), fixture.Window)!.Value;
        fixture.Window.MouseDown(start, MouseButton.Left);
        fixture.Window.MouseMove(outsidePoint); fixture.Window.UpdateLayout();
        fixture.Window.MouseUp(outsidePoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.Null(tabs[1].Group);
        Assert.Same(tabs[1], model.Tabs[^1]);
        Assert.Equal(PaneLayout.Single, model.PaneLayout);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task PullingStraightDownPreviewsRemovalWithoutMutatingTheGroup()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        fixture.AddTab();
        var model = fixture.Model;
        var group = model.CreateGroup(model.Tabs.ToArray());
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        var list = fixture.Window.FindControl<ListBox>("TabList")!;
        var tab = model.Tabs[^1];
        var header = (Control)list.ContainerFromItem(tab)!;
        var line = header.GetVisualDescendants().OfType<TabGroupLine>().Single();
        var start = header.TranslatePoint(new Point(35, 16), fixture.Window)!.Value;
        var end = start + new Vector(0, 64);
        fixture.Window.MouseDown(start, MouseButton.Left);
        fixture.Window.MouseMove(end); fixture.Window.UpdateLayout();
        Assert.Null(line.PreviewGroup);
        Assert.Same(group, tab.Group);
        fixture.Window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        Assert.Null(tab.Group);
        Assert.Single(model.Members(group));
    }

    [AvaloniaFact(Explicit = true)]
    public async Task NewTabToRightOfGroupedAnchorIsCreatedOutsideTheGroup()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(new FakeFileService("/tmp/groups"))));
        fixture.AddTab();
        var model = fixture.Model;
        var group = model.CreateGroup(model.Tabs.ToArray());
        var members = model.Tabs.ToArray();
        await fixture.Window.OpenTabToRightAsync(members[0]);
        Assert.Equal(members, model.Members(group));
        Assert.Null(model.SelectedTab!.Group);
        Assert.Same(model.SelectedTab, model.Tabs[^1]);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SelectingOverflowedTabScrollsItsHeaderIntoView()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        while (fixture.Model.Tabs.Count < 12) fixture.AddTab();
        fixture.Window.Width = 1000;
        var model = fixture.Model;
        model.CreateGroup(model.Tabs.ToArray());
        model.SetPaneLayout(PaneLayout.TwoColumns);
        model.SelectedTab = model.Tabs[^1];
        Dispatcher.UIThread.RunJobs(); fixture.Window.UpdateLayout();
        var list = fixture.Window.FindControl<ListBox>("TabList")!;
        var viewport = fixture.Window.FindControl<ScrollViewer>("TabScrollViewer")!;
        var header = (Control)list.ContainerFromItem(model.SelectedTab)!;
        var origin = header.TranslatePoint(default, viewport)!.Value;
        Assert.True(viewport.Offset.X > 0);
        Assert.InRange(origin.X, -1, viewport.Bounds.Width);
        Assert.True(origin.X + header.Bounds.Width <= viewport.Bounds.Width + 1);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task GroupSessionRestoresPanesDuplicatesAndCollapsedSelectionBeforeLoading()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(new FakeFileService("/tmp/groups"))));
        var group = new FolderTabGroupSession(Guid.NewGuid(), "Project", 5, true, [0, 1, 2, 3, 4],
            PaneLayout.ThreeColumns, [2, 4, 0], 4, 1);
        var session = new FolderTabSession(["/tmp/a", "/tmp/a", "/tmp/b", "/tmp/c", "/tmp/d"], 4) { Groups = [group] };
        await fixture.Window.RestoreFolderTabSessionAsync(session);
        var model = fixture.Model;
        var restored = Assert.Single(model.Groups);
        Assert.Equal(group.Id, restored.Id);
        Assert.Equal(5, restored.ColorIndex);
        Assert.True(restored.IsCollapsed);
        Assert.Same(restored, model.SelectedStripItem);
        Assert.Same(model.Tabs[4], model.SelectedTab);
        Assert.Equal(new[] { model.Tabs[2], model.Tabs[4], model.Tabs[0] }, model.VisiblePanes);
        Assert.Equal(PaneLayout.ThreeColumns, model.PaneLayout);
        Assert.Equal(1, model.ActivePaneSlotIndex);
        Assert.Equal(5, fixture.Window.CaptureFolderTabs().Paths.Length);
    }
}
