using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "DiskManagement")]
public sealed class DiskManagementUiTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    [Trait("Area", "Sidebar")]
    public void HeaderGearReservesSpaceRevealsOnHoverAndFocusAndHidesWithEmptyGroup()
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService(RuntimePaths.TestRoot!));
        var sidebar = new FinderSidebarView { DataContext = vm };
        var window = new Window { Width = 260, Height = 1600, Content = sidebar };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var header = sidebar.FindControl<Grid>("MountedLocationsHeader")!;
            var button = sidebar.FindControl<Button>("DiskManagementButton")!;
            Assert.False(header.IsVisible);
            vm.ExternalVolumes.Add(new VolumeInfo { Path = "/Volumes/TestDisk", DisplayName = "TestDisk", IsExternal = true });
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            header.BringIntoView(); Dispatcher.UIThread.RunJobs();
            Assert.True(header.IsVisible);
            Assert.Equal(0, button.Opacity);
            var bounds = header.Bounds;
            var point = header.TranslatePoint(new Point(50, 12), window)!.Value;
            window.MouseMove(point); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, button.Opacity);
            window.MouseMove(button.TranslatePoint(new Point(8, 8), window)!.Value);
            Assert.Equal(1, button.Opacity);
            window.MouseMove(new Point(window.Width - 1, 1)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, button.Opacity);
            Assert.True(button.Focus()); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, button.Opacity);
            Assert.Equal(bounds, header.Bounds);
            sidebar.SetRailMode(true);
            Assert.False(button.IsVisible);
            sidebar.SetRailMode(false);
            Assert.True(button.IsVisible);
            vm.ExternalVolumes.Clear(); Dispatcher.UIThread.RunJobs();
            Assert.False(header.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task CompactPageSupportsRepeatedEntryAndHiddenPageStopsRefresh()
    {
        using var theme = new FastListTestTheme();
        var information = new DiskManagementTests.Information { Disks = [DiskManagementTests.Disk()] };
        using var page = new DiskManagementView(information, null);
        var window = new Window { Width = 900, Height = 650, Content = page };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Single(page.ViewModel.Disks);
            Assert.True(page.FindControl<Border>("DiskListPane")!.IsVisible);
            Assert.True(page.FindControl<Grid>("DiskDetailPane")!.IsVisible);
            var item = page.FindControl<ListBox>("DiskList")!.ContainerFromItem(page.ViewModel.SelectedDisk!)!;
            foreach (var bar in item.GetVisualDescendants().OfType<ProgressBar>())
                Assert.True(bar.Bounds.Width <= item.Bounds.Width - 36);
            window.Width = 600; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var list = page.FindControl<ListBox>("DiskList")!;
            var back = page.FindControl<Button>("BackToDisksButton")!;
            Assert.False(page.FindControl<Grid>("DiskDetailPane")!.IsVisible);
            for (var index = 0; index < 2; index++)
            {
                Assert.NotNull(page.ViewModel.SelectedDisk);
                Assert.True(list.ContainerFromItem(page.ViewModel.SelectedDisk)!.Focus());
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Dispatcher.UIThread.RunJobs();
                Assert.True(page.FindControl<Grid>("DiskDetailPane")!.IsVisible,
                    $"Iteration {index}; page width {page.Bounds.Width}; selected {page.ViewModel.SelectedDisk?.Info.Identity}; list visible {list.IsEffectivelyVisible}; back visible {back.IsVisible}");
                back.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.True(page.FindControl<Border>("DiskListPane")!.IsVisible);
            }
            page.IsVisible = false; Dispatcher.UIThread.RunJobs();
            var count = information.Calls;
            await page.RefreshAsync();
            Assert.Equal(count, information.Calls);
            page.IsVisible = true; Dispatcher.UIThread.RunJobs();
            Assert.True(information.Calls > count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task RefreshKeepsLayoutScrollAndFieldControlsStableWhileQueryIsPending()
    {
        using var theme = new FastListTestTheme();
        var information = new DiskManagementTests.Information { Disks = [DiskManagementTests.Disk()] };
        using var page = new DiskManagementView(information, null);
        var window = new Window { Width = 900, Height = 400, Content = page };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var technical = page.FindControl<Expander>("TechnicalDetails")!;
            technical.IsExpanded = true;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var scroll = page.FindControl<ScrollViewer>("DiskDetailsScroll")!;
            scroll.ScrollToEnd(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(scroll.Offset.Y > 0);
            var offset = scroll.Offset;
            var layout = page.FindControl<Grid>("DiskLayout")!;
            var bounds = layout.Bounds;
            var fields = page.FindControl<ItemsControl>("BasicFieldsList")!;
            var controls = fields.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("info-field-row")).ToArray();
            Assert.Equal(7, controls.Length);

            foreach (var showProgress in new[] { false, true })
            {
                var completion = new TaskCompletionSource<IReadOnlyList<DiskInfo>>();
                information.Pending = completion.Task;
                var refresh = page.RefreshAsync(showProgress);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.True(page.ViewModel.IsRefreshing);
                Assert.Equal(showProgress, page.FindControl<ProgressBar>("DiskLoadingIndicator")!.IsVisible);
                Assert.Equal(bounds, layout.Bounds);
                Assert.Equal(offset, scroll.Offset);
                completion.SetResult([DiskManagementTests.Disk() with { AvailableBytes = 310_000_000 }]);
                await refresh;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.Equal(bounds, layout.Bounds);
                Assert.Equal(offset, scroll.Offset);
                Assert.True(technical.IsExpanded);
                var updated = fields.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("info-field-row")).ToArray();
                Assert.Equal(controls, updated);
                Assert.False(page.ViewModel.IsLoading);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task WorkspaceKeepsFileToolbarStatusAndInspectorHiddenForDiskPage()
    {
        using var theme = new FastListTestTheme();
        var previous = App.Services;
        using var services = new ServiceCollection().AddSingleton<IDiskInformationService>(
            new DiskManagementTests.Information { Disks = [DiskManagementTests.Disk()] }).BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        using var model = CreateViewModel(new FakeFileService(RuntimePaths.TestRoot!));
        await model.NavigateToAsync(VirtualPath.Disks);
        using var tab = new ExplorerTabViewModel(model);
        using var workspace = new ExplorerWorkspaceView { DataContext = tab };
        var window = new Window { Width = 1200, Height = 700, Content = workspace };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(workspace.FindControl<Border>("ToolbarSurface")!.IsVisible);
            Assert.False(workspace.FindControl<Border>("StatusBar")!.IsVisible);
            Assert.False(workspace.FileListView.IsVisible);
            Assert.False(workspace.FindControl<SplitView>("InfoDrawer")!.IsPaneOpen);
            var diskPage = Assert.IsType<DiskManagementView>(workspace.FindControl<ContentControl>("DiskManagementViewHost")!.Content);
            Assert.Single(diskPage.ViewModel.Disks);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previous);
        }
    }
}
