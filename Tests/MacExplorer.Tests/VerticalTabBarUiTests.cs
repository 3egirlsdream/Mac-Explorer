using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "VerticalTabs")]
public sealed class VerticalTabBarUiTests : FileListViewModelTestBase
{
    private static void LoadVerticalTabStyles(MainWindow window)
    {
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/VerticalTabBarStyles.axaml")));
    }

    private static ContextMenu GetTabContextMenu(MainWindow window)
        => (ContextMenu)typeof(MainWindow).GetField("_tabContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    [AvaloniaFact(Explicit = true)]
    public async Task ContextMenuTogglesVerticalTabsHidesTitleBarAndReclaimsVerticalSpace()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        LoadVerticalTabStyles(fixture.Window);
        fixture.AddTab();
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        var titleBar = fixture.Window.GetVisualDescendants().OfType<WindowTitleBar>().Single();
        var rootLayout = fixture.Window.FindControl<Border>("RootLayout")!;
        var verticalBar = fixture.Window.FindControl<VerticalTabBar>("VerticalTabBarControl")!;
        var slot = fixture.Window.FindControl<Border>("VerticalTabSlot")!;
        var settings = App.Services.GetRequiredService<ISettingsService>();

        Assert.True(titleBar.IsVisible);
        Assert.False(verticalBar.IsVisible);
        Assert.False(slot.IsVisible);
        var horizontalRootTop = rootLayout.TranslatePoint(default, fixture.Window)!.Value.Y;
        Assert.True(horizontalRootTop >= 40);

        // Open tab context menu in horizontal mode and click the vertical tabs toggle (last item).
        var tabList = fixture.Window.FindControl<ListBox>("TabList")!;
        var tabHeader = (Control)tabList.ContainerFromItem(fixture.Model.Tabs[0])!;
        fixture.Window.ShowTabContextMenu(tabHeader, fixture.Model.Tabs[0]);
        var menu = GetTabContextMenu(fixture.Window);
        var toggleItem = menu.Items.OfType<MenuItem>().Last();
        toggleItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        Assert.True(fixture.Window.IsVerticalTabsEnabled);
        Assert.True(VerticalTabBarPreferences.IsEnabled(settings));
        Assert.False(titleBar.IsVisible);
        Assert.True(verticalBar.IsVisible);
        Assert.True(slot.IsVisible);
        Assert.Equal(0, rootLayout.TranslatePoint(default, fixture.Window)!.Value.Y, precision: 1);
        Assert.Equal(VerticalTabBarPreferences.DefaultExpandedWidth, slot.Width);
        Assert.Equal(VerticalTabBarPreferences.DefaultExpandedWidth, verticalBar.Width);
        Assert.Equal(verticalBar.Bounds.Width, rootLayout.TranslatePoint(default, fixture.Window)!.Value.X, precision: 1);

        // Open tab context menu in vertical mode and switch back to horizontal mode.
        var verticalHeader = (Control)verticalBar.TabList.ContainerFromItem(fixture.Model.Tabs[0])!;
        fixture.Window.ShowTabContextMenu(verticalHeader, fixture.Model.Tabs[0]);
        menu = GetTabContextMenu(fixture.Window);
        toggleItem = menu.Items.OfType<MenuItem>().Last();
        toggleItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        Assert.False(fixture.Window.IsVerticalTabsEnabled);
        Assert.False(VerticalTabBarPreferences.IsEnabled(settings));
        Assert.True(titleBar.IsVisible);
        Assert.False(verticalBar.IsVisible);
        Assert.True(rootLayout.TranslatePoint(default, fixture.Window)!.Value.Y >= 40);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SplitScreenSharesSingleIndependentVerticalTabBarAndAnchorsPopup()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        LoadVerticalTabStyles(fixture.Window);
        while (fixture.Model.Tabs.Count < 4) fixture.AddTab();
        fixture.Window.SetVerticalTabsEnabled(true);
        fixture.Model.SetPaneLayout(PaneLayout.FourGrid);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        var verticalBars = fixture.Window.GetVisualDescendants().OfType<VerticalTabBar>().ToArray();
        var verticalBar = Assert.Single(verticalBars);
        Assert.Equal(4, fixture.Model.VisiblePanes.Count);
        Assert.True(verticalBar.IsVisible);

        var popup = fixture.Window.FindControl<Popup>("PaneLayoutPopup")!;
        Assert.Same(verticalBar.PaneLayoutButton, popup.PlacementTarget);
        Assert.Equal(PlacementMode.RightEdgeAlignedBottom, popup.Placement);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task UnpinnedCollapseUsesRailSlotAndHoverExpandsWithoutReflowingMainContent()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        LoadVerticalTabStyles(fixture.Window);
        fixture.AddTab();
        fixture.Window.SetVerticalTabsEnabled(true);
        fixture.Window.SetVerticalTabsPinned(false);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        var verticalBar = fixture.Window.FindControl<VerticalTabBar>("VerticalTabBarControl")!;
        var slot = fixture.Window.FindControl<Border>("VerticalTabSlot")!;
        var rootLayout = fixture.Window.FindControl<Border>("RootLayout")!;
        var settings = App.Services.GetRequiredService<ISettingsService>();

        Assert.False(VerticalTabBarPreferences.IsPinned(settings));
        Assert.Equal(VerticalTabBarPreferences.CollapsedWidth, slot.Width);
        Assert.Equal(VerticalTabBarPreferences.CollapsedWidth, verticalBar.Width);
        Assert.False(verticalBar.IsEffectivelyExpanded);
        var collapsedContentX = rootLayout.TranslatePoint(default, fixture.Window)!.Value.X;
        Assert.Equal(VerticalTabBarPreferences.CollapsedWidth, collapsedContentX, precision: 1);

        // Hover-expand overlays workspace content without moving RootLayout.
        verticalBar.SetHoverExpanded(true, animate: false);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        Assert.True(verticalBar.IsHoverExpanded);
        Assert.True(verticalBar.IsEffectivelyExpanded);
        Assert.Equal(VerticalTabBarPreferences.DefaultExpandedWidth, verticalBar.Width);
        Assert.Equal(1.0, verticalBar.RevealOpacity, precision: 2);
        Assert.Equal(VerticalTabBarPreferences.CollapsedWidth, slot.Width);
        Assert.Equal(collapsedContentX, rootLayout.TranslatePoint(default, fixture.Window)!.Value.X, precision: 1);

        // Animated hover collapse starts smoothly and keeps overlay until settled.
        verticalBar.SetHoverExpanded(false, animate: true);
        if (!Platforms.MacOS.MacWindowChrome.PrefersReducedMotion)
            Assert.True(verticalBar.IsAnimatingWidth);
        verticalBar.FinishWidthAnimation();
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
        Assert.Equal(VerticalTabBarPreferences.CollapsedWidth, verticalBar.Width);
        Assert.Equal(0.0, verticalBar.RevealOpacity, precision: 2);

        // Pinning locks expanded width into the layout slot and removes overlay mode.
        fixture.Window.SetVerticalTabsPinned(true);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        Assert.True(VerticalTabBarPreferences.IsPinned(settings));
        Assert.False(verticalBar.IsHoverExpanded);
        Assert.Equal(VerticalTabBarPreferences.DefaultExpandedWidth, slot.Width);
        Assert.Equal(VerticalTabBarPreferences.DefaultExpandedWidth, rootLayout.TranslatePoint(default, fixture.Window)!.Value.X, precision: 1);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task CollapsedRailHidesCloseAndShortcutAndKeepsIconsCenteredAcrossExpandCollapse()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        LoadVerticalTabStyles(fixture.Window);
        while (fixture.Model.Tabs.Count < 3) fixture.AddTab();
        var tabs = fixture.Model.Tabs.ToArray();
        var group = fixture.Model.CreateGroup(tabs.Skip(1));
        fixture.Model.SelectedTab = tabs[1];
        fixture.Window.SetVerticalTabsEnabled(true);
        fixture.Window.SetVerticalTabsPinned(false);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        var verticalBar = fixture.Window.FindControl<VerticalTabBar>("VerticalTabBarControl")!;
        var list = verticalBar.TabList;

        var activeRow = (ListBoxItem)list.ContainerFromItem(tabs[1])!;
        var closeHost = activeRow.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("vertical-tab-close-host"));
        var shortcutHost = verticalBar.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("vertical-new-tab-shortcut-host"));
        Assert.False(closeHost.IsVisible);
        Assert.False(shortcutHost.IsVisible);

        var ungroupedIcon = ((ListBoxItem)list.ContainerFromItem(tabs[0])!).GetVisualDescendants().OfType<PathIcon>().Single(p => p.Classes.Contains("vertical-tab-icon"));
        var groupCaret = ((ListBoxItem)list.ContainerFromItem(group)!).GetVisualDescendants().OfType<PathIcon>().Single(p => p.Classes.Contains("vertical-group-caret"));
        var groupedIcon = activeRow.GetVisualDescendants().OfType<PathIcon>().Single(p => p.Classes.Contains("vertical-tab-icon"));

        double CenterX(Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, 0), verticalBar)!.Value.X;
        var collapsedUngroupedX = CenterX(ungroupedIcon);
        var collapsedCaretX = CenterX(groupCaret);
        var collapsedGroupedX = CenterX(groupedIcon);
        Assert.InRange(Math.Abs(collapsedUngroupedX - 30), 0, 1.5);
        Assert.InRange(Math.Abs(collapsedCaretX - 30), 0, 1.5);
        Assert.InRange(Math.Abs(collapsedGroupedX - 30), 0, 1.5);

        // Expand and confirm icons remain locked on the exact same horizontal center (no jump during animation).
        verticalBar.SetHoverExpanded(true, animate: false);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        Assert.True(closeHost.IsVisible);
        Assert.True(shortcutHost.IsVisible);
        Assert.InRange(Math.Abs(CenterX(ungroupedIcon) - collapsedUngroupedX), 0, 1.5);
        Assert.InRange(Math.Abs(CenterX(groupCaret) - collapsedCaretX), 0, 1.5);
        Assert.InRange(Math.Abs(CenterX(groupedIcon) - collapsedGroupedX), 0, 1.5);

        // Verify adaptive display refresh rate capping up to 120 fps and smooth ease-out curve.
        Assert.Equal(60, VerticalTabBar.ResolveAdaptiveFps(0));
        Assert.Equal(60, VerticalTabBar.ResolveAdaptiveFps(60));
        Assert.Equal(90, VerticalTabBar.ResolveAdaptiveFps(90));
        Assert.Equal(120, VerticalTabBar.ResolveAdaptiveFps(120));
        Assert.Equal(120, VerticalTabBar.ResolveAdaptiveFps(144));
        Assert.Equal(120, VerticalTabBar.ResolveAdaptiveFps(240));
        Assert.Equal(0, VerticalTabBar.EvaluateEase(0, expanding: true), precision: 4);
        Assert.Equal(1, VerticalTabBar.EvaluateEase(1, expanding: true), precision: 4);
        Assert.True(VerticalTabBar.EvaluateEase(0.5, expanding: true) > 0.85);
        Assert.True(VerticalTabBar.EvaluateEase(0.5, expanding: false) > 0.80);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task VerticalGroupsApplyContinuousLeftAccentClassesAndSupportToggleAndDragReorder()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        LoadVerticalTabStyles(fixture.Window);
        while (fixture.Model.Tabs.Count < 4) fixture.AddTab();
        var tabs = fixture.Model.Tabs.ToArray();
        var group = fixture.Model.CreateGroup(tabs.Take(3));
        fixture.Window.SetVerticalTabsEnabled(true);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        var verticalBar = fixture.Window.FindControl<VerticalTabBar>("VerticalTabBarControl")!;
        verticalBar.RefreshGroupVisualClasses();
        var list = verticalBar.TabList;

        var groupHeader = (ListBoxItem)list.ContainerFromItem(group)!;
        var first = (ListBoxItem)list.ContainerFromItem(tabs[0])!;
        var middle = (ListBoxItem)list.ContainerFromItem(tabs[1])!;
        var last = (ListBoxItem)list.ContainerFromItem(tabs[2])!;
        var ungrouped = (ListBoxItem)list.ContainerFromItem(tabs[3])!;

        Assert.True(groupHeader.Classes.Contains("group-header"));
        Assert.True(first.Classes.Contains("group-member"));
        Assert.True(first.Classes.Contains("group-first"));
        Assert.False(first.Classes.Contains("group-last"));

        Assert.True(middle.Classes.Contains("group-member"));
        Assert.False(middle.Classes.Contains("group-first"));
        Assert.False(middle.Classes.Contains("group-last"));

        Assert.True(last.Classes.Contains("group-member"));
        Assert.False(last.Classes.Contains("group-first"));
        Assert.True(last.Classes.Contains("group-last"));

        Assert.False(ungrouped.Classes.Contains("group-member"));

        // Clicking group header toggles group collapse.
        var groupPoint = groupHeader.TranslatePoint(new Point(20, groupHeader.Bounds.Height / 2), fixture.Window)!.Value;
        fixture.Window.MouseDown(groupPoint, MouseButton.Left);
        fixture.Window.MouseUp(groupPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(group.IsCollapsed);
        Assert.DoesNotContain(tabs[0], list.Items);

        // Expand again and reorder tabs[0] to after tabs[2] by vertical drag.
        fixture.Window.MouseDown(groupPoint, MouseButton.Left);
        fixture.Window.MouseUp(groupPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
        Assert.False(group.IsCollapsed);

        first = (ListBoxItem)list.ContainerFromItem(tabs[0])!;
        last = (ListBoxItem)list.ContainerFromItem(tabs[2])!;
        var start = first.TranslatePoint(new Point(40, first.Bounds.Height / 2), fixture.Window)!.Value;
        var end = last.TranslatePoint(new Point(40, last.Bounds.Height - 2), fixture.Window)!.Value;
        fixture.Window.MouseDown(start, MouseButton.Left);
        fixture.Window.MouseMove(end);
        fixture.Window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { tabs[1], tabs[2], tabs[0], tabs[3] }, fixture.Model.Tabs);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task NewWindowRestoresPersistedVerticalTabBarState()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0, configureServices: services =>
        {
            var settings = new InMemorySettingsService();
            VerticalTabBarPreferences.SetEnabled(settings, true);
            VerticalTabBarPreferences.SetPinned(settings, false);
            VerticalTabBarPreferences.SetWidth(settings, 264);
            services.AddSingleton<ISettingsService>(settings);
        });
        LoadVerticalTabStyles(fixture.Window);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();

        var verticalBar = fixture.Window.FindControl<VerticalTabBar>("VerticalTabBarControl")!;
        Assert.True(fixture.Window.IsVerticalTabsEnabled);
        Assert.False(fixture.Window.IsVerticalTabsPinned);
        Assert.True(verticalBar.IsVisible);
        Assert.Equal(VerticalTabBarPreferences.CollapsedWidth, verticalBar.Width);
        Assert.Equal(264, verticalBar.ExpandedWidth);
    }
}
