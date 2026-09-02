using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ExplorerWorkspaceViewTests
{
    [AvaloniaFact]
    public void BreakpointAndForceCompactUseTheSameWorkspaceAndDataContext()
    {
        var dataContext = new object();
        var workspace = new ExplorerWorkspaceView { DataContext = dataContext };
        var window = new Window { Width = 1180, Height = 800, Content = workspace };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(workspace.IsCompact);

        window.Width = 1179;
        Dispatcher.UIThread.RunJobs();
        Assert.True(workspace.IsCompact);
        Assert.Same(dataContext, workspace.DataContext);

        window.Width = 1600;
        workspace.ForceCompact = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(workspace.IsCompact);
        Assert.Same(workspace, window.Content);
        Assert.Same(dataContext, workspace.DataContext);

        window.Close();
    }

    [AvaloniaFact]
    public void CompactRoundTripReusesSidebarAndRestoresInlineWidths()
    {
        var workspace = new ExplorerWorkspaceView();
        var window = new Window { Width = 1280, Height = 800, Content = workspace };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var splitView = workspace.FindControl<SplitView>("SidebarSplitView")!;
        var sidebar = workspace.FindControl<FinderSidebarView>("SidebarControl")!;
        var volumeItem = sidebar.FindControl<Border>("VolumeItem")!;
        var originalVolumeWidth = volumeItem.Width;
        var originalVolumeMargin = volumeItem.Margin;
        var originalVolumeContentAlignment = Assert.IsType<StackPanel>(volumeItem.Child).HorizontalAlignment;
        Assert.Equal(SplitViewDisplayMode.Inline, splitView.DisplayMode);
        Assert.Equal(260, splitView.OpenPaneLength);
        Assert.True(splitView.IsPaneOpen);

        window.Width = 1000;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SplitViewDisplayMode.CompactOverlay, splitView.DisplayMode);
        Assert.Equal(48, splitView.CompactPaneLength);
        Assert.Equal(220, splitView.OpenPaneLength);
        Assert.False(splitView.IsPaneOpen);
        Assert.True(sidebar.IsRailMode);
        var secondarySection = sidebar.FindControl<ItemsControl>("PinnedFoldersControl")!;
        var remoteServers = sidebar.FindControl<ItemsControl>("RemoteServersList")!;
        Assert.Contains("rail-secondary", remoteServers.Classes);
        Assert.Equal(0, secondarySection.MaxHeight);
        Assert.Equal(0, secondarySection.Opacity);
        Assert.Contains("rail-compact-item", volumeItem.Classes);
        Assert.Equal(40, volumeItem.Width);
        Assert.Equal(new Thickness(0, 1), volumeItem.Margin);
        Assert.Equal(
            Avalonia.Layout.HorizontalAlignment.Center,
            Assert.IsType<StackPanel>(volumeItem.Child).HorizontalAlignment);

        workspace.ToggleSidebar();
        Dispatcher.UIThread.RunJobs();
        Assert.True(splitView.IsPaneOpen);
        Assert.False(sidebar.IsRailMode);
        Assert.True(double.IsPositiveInfinity(secondarySection.MaxHeight));
        Assert.Equal(1, secondarySection.Opacity);
        Assert.Equal(originalVolumeWidth, volumeItem.Width);
        Assert.Equal(originalVolumeMargin, volumeItem.Margin);
        Assert.Equal(originalVolumeContentAlignment, Assert.IsType<StackPanel>(volumeItem.Child).HorizontalAlignment);
        Assert.Same(sidebar, workspace.FindControl<FinderSidebarView>("SidebarControl"));

        window.Width = 1280;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SplitViewDisplayMode.Inline, splitView.DisplayMode);
        Assert.Equal(260, splitView.OpenPaneLength);
        Assert.True(splitView.IsPaneOpen);
        Assert.Same(sidebar, workspace.FindControl<FinderSidebarView>("SidebarControl"));

        window.Close();
    }

    [AvaloniaFact]
    public void WorkspaceContainsExactlyOneBreadcrumbAndNoWindowLevelEntries()
    {
        var workspace = new ExplorerWorkspaceView();
        var window = new Window { Width = 1280, Height = 800, Content = workspace };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(workspace.GetVisualDescendants().OfType<BreadcrumbBar>());
        Assert.DoesNotContain(workspace.GetVisualDescendants().OfType<Button>(),
            button => Equals(button.Content, "任务中心") || Equals(button.Content, "设置"));

        window.Close();
    }

    [AvaloniaFact]
    public void CompactOverflowItemsMatchPrimaryActionStateAndTooltips()
    {
        var toolbar = new FinderToolbar { IsCompact = true };
        var window = new Window { Width = 600, Height = 100, Content = toolbar };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AssertActionPair(toolbar, "CutButton", "CutOverflowButton");
        AssertActionPair(toolbar, "CopyButton", "CopyOverflowButton");
        AssertActionPair(toolbar, "PasteButton", "PasteOverflowButton");
        AssertActionPair(toolbar, "DeleteButton", "DeleteOverflowButton");
        AssertActionPair(toolbar, "HomeButton", "HomeOverflowButton");

        window.Close();
    }

    [AvaloniaFact]
    public void CompactToolbarKeepsTheFullStyleOfVisibleButtons()
    {
        var toolbar = new FinderToolbar { IsCompact = true };
        var window = new Window { Width = 600, Height = 100, Content = toolbar };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var newLabel = toolbar.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "新建");
        var sortLabel = toolbar.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "排序");
        Assert.True(newLabel.IsVisible);
        Assert.True(sortLabel.IsVisible);
        Assert.Contains("toolbar-btn", toolbar.FindControl<Button>("NewBtn")!.Classes);
        Assert.Contains("toolbar-btn", toolbar.FindControl<Button>("SortButton")!.Classes);

        window.Close();
    }

    [AvaloniaFact]
    public async Task ExplicitPreviewReloadsAndReleaseAlwaysAdvanceRequestGeneration()
    {
        var panel = new InfoPanelView();
        var window = new Window { Width = 400, Height = 600, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var initial = panel.PreviewRequestGeneration;
        await panel.SetLivePreviewStateAsync(true, 7);
        var first = panel.PreviewRequestGeneration;
        await panel.ReloadLivePreviewAsync(7);
        var second = panel.PreviewRequestGeneration;
        await panel.ReloadLivePreviewAsync(7);
        var third = panel.PreviewRequestGeneration;
        await panel.SetLivePreviewStateAsync(false, 8);

        Assert.True(first > initial);
        Assert.True(second > first);
        Assert.True(third > second);
        Assert.True(panel.PreviewRequestGeneration > third);
        Assert.False(panel.IsLivePreviewEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void DisposeIsIdempotentAndClosesViewOwnedState()
    {
        var workspace = new ExplorerWorkspaceView();

        workspace.Dispose();
        workspace.Dispose();

        Assert.True(workspace.IsDisposed);
    }

    private static void AssertActionPair(FinderToolbar toolbar, string primaryName, string overflowName)
    {
        var primary = toolbar.FindControl<Button>(primaryName)!;
        var overflow = toolbar.FindControl<Button>(overflowName)!;
        Assert.Equal(primary.IsEnabled, overflow.IsEnabled);
        Assert.Equal(ToolTip.GetTip(primary), ToolTip.GetTip(overflow));
        Assert.Same(primary.DataContext, overflow.DataContext);
    }
}
