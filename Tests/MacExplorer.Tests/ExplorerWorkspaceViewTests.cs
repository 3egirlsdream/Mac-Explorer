using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Views;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "ExplorerWorkspaceView")]
public sealed class ExplorerWorkspaceViewTests
{
    [AvaloniaFact(Explicit = true)]
    public void NewWideWorkspaceNeverClosesItsSidebarDuringFirstLayout()
    {
        using var workspace = new ExplorerWorkspaceView();
        var sidebar = workspace.FindControl<SplitView>("SidebarSplitView")!;
        Assert.False(workspace.IsCompact);
        Assert.True(sidebar.IsPaneOpen);
        var closedCount = 0;
        sidebar.PropertyChanged += (_, e) =>
        {
            if (e.Property == SplitView.IsPaneOpenProperty && !sidebar.IsPaneOpen)
                closedCount++;
        };
        var window = new Window { Width = 1280, Height = 800, Content = workspace };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(workspace.IsCompact);
        Assert.True(sidebar.IsPaneOpen);
        Assert.Equal(0, closedCount);
        window.Close();
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(1000, false, true)]
    [InlineData(1280, false, false)]
    [InlineData(1280, true, true)]
    public void FirstMeasureAlreadyUsesTheFinalSidebarMode(double width, bool forceCompact, bool compact)
    {
        using var workspace = new ExplorerWorkspaceView { ForceCompact = forceCompact };
        workspace.Measure(new Size(width, 800));

        Assert.Equal(compact, workspace.IsCompact);
        var sidebar = workspace.FindControl<SplitView>("SidebarSplitView")!;
        Assert.Equal(!compact, sidebar.IsPaneOpen);
        Assert.Equal(compact, workspace.FindControl<FinderSidebarView>("SidebarControl")!.IsRailMode);
    }

    [AvaloniaFact(Explicit = true)]
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

    [AvaloniaFact(Explicit = true)]
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
        Assert.Equal(240, splitView.OpenPaneLength);
        Assert.True(splitView.IsPaneOpen);

        window.Width = 1000;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SplitViewDisplayMode.CompactOverlay, splitView.DisplayMode);
        Assert.Equal(48, splitView.CompactPaneLength);
        Assert.Equal(220, splitView.OpenPaneLength);
        Assert.False(splitView.IsPaneOpen);
        Assert.True(sidebar.IsRailMode);
        var secondarySection = sidebar.FindControl<ItemsControl>("PinnedFoldersControl")!;
        var remoteServers = sidebar.FindControl<ItemsControl>("NetworkLocationsList")!;
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
        Assert.Equal(240, splitView.OpenPaneLength);
        Assert.True(splitView.IsPaneOpen);
        Assert.Same(sidebar, workspace.FindControl<FinderSidebarView>("SidebarControl"));

        window.Close();
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(1280, 240)]
    [InlineData(1000, 220)]
    public async Task DraggingSidebarEdgeResizesAndSavesTheOpenWidth(double windowWidth, double initialWidth)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var theme = new FastListTestTheme();
        var directory = Directory.CreateTempSubdirectory("sidebar-width-");
        try
        {
            var database = new DatabaseConnectionFactory(Path.Combine(directory.FullName, "settings.db"));
            using (var connection = database.GetConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
                command.ExecuteNonQuery();
            }
            using (var settings = new SettingsService(database))
            {
                using var workspace = new ExplorerWorkspaceView(settings);
                var window = new Window { Width = windowWidth, Height = 800, Content = workspace };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    var sidebar = workspace.FindControl<SplitView>("SidebarSplitView")!;
                    var handle = workspace.FindControl<Border>("SidebarResizeHandle")!;
                    if (workspace.IsCompact)
                    {
                        Assert.False(handle.IsVisible);
                        workspace.ToggleSidebar();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        await Task.Delay(250, cancellationToken);
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Dispatcher.UIThread.RunJobs();
                    }
                    Assert.True(handle.IsVisible);
                    Assert.Equal(StandardCursorType.SizeWestEast.ToString(), handle.Cursor!.ToString());
                    var start = handle.TranslatePoint(new Rect(handle.Bounds.Size).Center, window)!.Value;
                    var middle = start + new Vector(-50, 0);
                    var end = start + new Vector(-65, 0);
                    window.MouseDown(start, MouseButton.Left);
                    window.MouseMove(middle, RawInputModifiers.LeftMouseButton);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(initialWidth - 50, sidebar.OpenPaneLength);
                    Assert.Equal(initialWidth - 50, workspace.FindControl<Grid>("SidebarPane")!.Bounds.Width);
                    Assert.Empty(settings.GetAll());
                    window.MouseUp(end, MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(initialWidth - 65, sidebar.OpenPaneLength);
                    Assert.True(sidebar.IsPaneOpen);

                    window.Width = 1400;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(initialWidth - 65, sidebar.OpenPaneLength);

                    workspace.ForceCompact = true;
                    window.Width = 200;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(136, sidebar.OpenPaneLength);
                    Assert.Equal(48, sidebar.CompactPaneLength);
                    Assert.False(handle.IsVisible);
                    window.Width = 1400;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(initialWidth - 65, sidebar.OpenPaneLength);
                }
                finally { window.Close(); }
            }

            using var restoredSettings = new SettingsService(database);
            using var restoredWorkspace = new ExplorerWorkspaceView(restoredSettings);
            var restoredWindow = new Window { Width = 1280, Height = 800, Content = restoredWorkspace };
            restoredWindow.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(initialWidth - 65, restoredWorkspace.FindControl<SplitView>("SidebarSplitView")!.OpenPaneLength);
            }
            finally { restoredWindow.Close(); }
        }
        finally { directory.Delete(recursive: true); }
    }

    [AvaloniaFact(Explicit = true)]
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

    [AvaloniaFact(Explicit = true)]
    public void CompactToolbarKeepsTheFullStyleOfVisibleButtons()
    {
        using var theme = new FastListTestTheme();
        var toolbar = new FinderToolbar();
        var window = new Window { Width = 300, Height = 100, Content = toolbar };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(toolbar.FindControl<Control>("InlineActionsHost")!.IsVisible);
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

    [AvaloniaFact(Explicit = true)]
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

}

[Trait("Kind", "UI")]
[Trait("Area", "ExplorerWorkspaceView")]
public sealed class ExplorerWorkspaceViewViewModelTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public void SplitWorkspaceOnlyOverflowsFileActionsWhenTheToolbarRunsOutOfSpace()
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/fkfinder-split-toolbar"));
        using var tab = new ExplorerTabViewModel(vm) { IsActive = true };
        using var workspace = new ExplorerWorkspaceView { ForceCompact = true, DataContext = tab };
        var window = new Window { Width = 720, Height = 500, Content = workspace };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(workspace.IsCompact);
            var toolbar = workspace.FindControl<FinderToolbar>("ToolbarControl")!;
            var inlineActions = toolbar.FindControl<Control>("InlineActionsHost")!;
            Assert.True(inlineActions.IsEffectivelyVisible);

            window.Width = 370;
            Dispatcher.UIThread.RunJobs();
            Assert.False(inlineActions.IsEffectivelyVisible);

            window.Width = 720;
            Dispatcher.UIThread.RunJobs();
            Assert.True(workspace.IsCompact);
            Assert.True(inlineActions.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("NewDropdown")]
    [InlineData("SortDropdown")]
    [InlineData("MoreDropdown")]
    [InlineData("PathInput")]
    [InlineData("ContextMenu")]
    public void DisposedWorkspaceClosesTransientUiAndStopsReactingToItsTab(string transientUi)
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/workspace-dispose"));
        vm.IsInfoPanelVisible = true;
        vm.Entries.Add(new FileSystemEntry { FullPath = "/tmp/workspace-dispose/readme.txt", Name = "readme.txt" });
        using var tab = new ExplorerTabViewModel(vm) { IsActive = true };
        using var workspace = new ExplorerWorkspaceView { DataContext = tab };
        var window = new Window { Width = 1280, Height = 800, Content = workspace };
        window.Styles.Add(new FluentTheme());
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var surface = workspace.FindControl<Border>("WorkspaceSurface")!;
            var list = workspace.FindControl<FileListView>("FileListControl")!;
            var drawer = workspace.FindControl<SplitView>("InfoDrawer")!;
            Assert.Contains("active", surface.Classes);
            Assert.True(list.IsVisible);
            Assert.True(drawer.IsPaneOpen);
            Func<bool> isOpen;
            if (transientUi == "ContextMenu")
            {
                var fast = list.FindControl<FastFileList>("FastList")!;
                var point = fast.TranslatePoint(new Point(70, 15), window)!.Value;
                window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
                window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>(), item => item.IsOpen);
                Assert.True(vm.IsContextMenuVisible);
                isOpen = () => menu.IsOpen;
            }
            else if (transientUi == "PathInput")
            {
                var breadcrumb = workspace.FindControl<BreadcrumbBar>("BreadcrumbControl")!;
                breadcrumb.FocusPathInput();
                var input = breadcrumb.FindControl<TextBox>("PathInput")!;
                isOpen = () => input.IsVisible;
            }
            else
            {
                var toolbar = workspace.FindControl<FinderToolbar>("ToolbarControl")!;
                var popup = toolbar.FindControl<Popup>(transientUi)!;
                popup.IsOpen = true;
                isOpen = () => popup.IsOpen;
            }
            Assert.True(isOpen());

            workspace.Dispose();
            workspace.Dispose();
            Assert.True(workspace.IsDisposed);
            Assert.False(isOpen());
            Assert.False(vm.IsContextMenuVisible);

            tab.IsActive = false;
            vm.IsInfoPanelVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("active", surface.Classes);
            Assert.True(list.IsVisible);
            Assert.True(drawer.IsPaneOpen);
        }
        finally { window.Close(); }
    }

}
