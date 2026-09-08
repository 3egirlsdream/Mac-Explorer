using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(1000)]
    [InlineData(1280)]
    public void HomeShortcutsFocusTheOnlyVisibleInputAndRestoreDirectoryChrome(double width)
    {
        var files = new FakeFileService("/tmp/FKFinderVisualTests");
        var navigation = new NavigationViewModel(files) { IsHomePage = true };
        using var vm = CreateViewModel(files, navigation: navigation);
        using var tab = new ExplorerTabViewModel(vm);
        using var workspace = new ExplorerWorkspaceView { DataContext = tab };
        var window = new Window { Width = width, Height = 800, Content = workspace };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var home = workspace.FindControl<HomeView>("HomeViewControl")!;
            var centralInput = home.FindControl<TextBox>("HomeSearchBox")!;
            var navigationRow = workspace.FindControl<Grid>("NavigationRow")!;
            Assert.False(navigationRow.IsVisible);
            Assert.Equal(0, ((Grid)navigationRow.Parent!).RowDefinitions[0].ActualHeight);
            Assert.False(workspace.FindControl<Border>("ToolbarSurface")!.IsVisible);
            Assert.False(workspace.FindControl<Border>("StatusBar")!.IsVisible);
            Assert.False(workspace.FindControl<Grid>("SearchHost")!.IsVisible);
            workspace.FocusPathInput();
            Dispatcher.UIThread.RunJobs();
            Assert.True(centralInput.IsFocused);
            workspace.TogglePageSearch();
            Assert.True(centralInput.IsFocused);
            Assert.False(workspace.FindControl<Grid>("SearchHost")!.IsVisible);

            navigation.IsHomePage = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(navigationRow.IsVisible);
            Assert.Equal(40, ((Grid)navigationRow.Parent!).RowDefinitions[0].ActualHeight);
            Assert.True(workspace.FindControl<Border>("ToolbarSurface")!.IsVisible);
            Assert.True(workspace.FindControl<Border>("StatusBar")!.IsVisible);
            workspace.TogglePageSearch();
            Assert.True(workspace.FindControl<TextBox>("SearchBox")!.IsFocused);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DensePaneNavigationAndExpandedSearchStayInsideTheWorkspace()
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/FKFinderVisualTests"));
        using var tab = new ExplorerTabViewModel(vm);
        using var workspace = new ExplorerWorkspaceView { DataContext = tab, ForceCompact = true };
        var window = new Window { Width = 400, Height = 800, Content = workspace };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var breadcrumb = workspace.FindControl<BreadcrumbBar>("BreadcrumbControl")!;
            var toggle = workspace.FindControl<Button>("SearchToggleButton")!;
            var pathRight = breadcrumb.TranslatePoint(new Point(breadcrumb.Bounds.Width, 0), workspace)!.Value.X;
            var toggleLeft = toggle.TranslatePoint(default, workspace)!.Value.X;
            Assert.True(pathRight <= toggleLeft);
            Assert.True(toggleLeft + toggle.Bounds.Width <= workspace.Bounds.Width);
            workspace.TogglePageSearch();
            Dispatcher.UIThread.RunJobs();
            var input = workspace.FindControl<TextBox>("SearchBox")!;
            var inputRight = input.TranslatePoint(new Point(input.Bounds.Width, 0), workspace)!.Value.X;
            Assert.True(inputRight <= workspace.Bounds.Width);
            Assert.True(input.Bounds.Width > 40);
            Assert.True(input.IsFocused);
            Assert.False(breadcrumb.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClearingSearchAlsoClearsItsOldStatusSummary()
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/FKFinderVisualTests"));
        vm.StatusText = "搜索 old — 找到 0 项";
        vm.SearchScopePath = "/tmp/FKFinderVisualTests";
        await vm.ExitSearchAsync();
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Equal(string.Empty, vm.SearchScopePath);
    }

    [AvaloniaFact]
    public void ToolbarSelectionActionsStayInSyncWithTheirCompactMenuEntries()
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/FKFinderVisualTests"));
        var toolbar = new FinderToolbar { DataContext = vm };
        var window = new Window { Width = 600, Height = 100, Content = toolbar };
        window.Show();
        try
        {
            foreach (var selected in new[] { false, true, false })
            {
                window.Content = null;
                window.Content = toolbar;
                vm.SelectedEntries.Clear();
                if (selected)
                    vm.SelectedEntries.Add(new MacExplorer.Models.FileSystemEntry
                        { FullPath = "/tmp/FKFinderVisualTests/test.txt", Name = "test.txt" });
                Dispatcher.UIThread.RunJobs();
                foreach (var action in new[] { "Cut", "Copy", "Delete" })
                {
                    Assert.Equal(selected, toolbar.FindControl<Button>(action + "Button")!.IsEnabled);
                    Assert.Equal(selected, toolbar.FindControl<Button>(action + "OverflowButton")!.IsEnabled);
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReadFailureDoesNotRenderAsAnEmptyFolderAndClearsOnRecovery()
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/FKFinderVisualTests"));
        var list = new FileListView { DataContext = vm };
        var window = new Window { Width = 600, Height = 400, Content = list };
        window.Show();
        try
        {
            vm.ReadErrorMessage = "没有读取权限";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("无法读取此位置", list.FindControl<TextBlock>("EmptyStateText")!.Text);
            Assert.Equal("没有读取权限", list.FindControl<TextBlock>("EmptyStateHint")!.Text);
            Assert.True(list.FindControl<Button>("RetryReadButton")!.IsVisible);
            vm.ReadErrorMessage = string.Empty;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("此文件夹为空", list.FindControl<TextBlock>("EmptyStateText")!.Text);
            Assert.False(list.FindControl<Button>("RetryReadButton")!.IsVisible);
        }
        finally { window.Close(); }
    }
}

public sealed class UIVisualButtonTests
{
    [AvaloniaTheory]
    [InlineData("ghost", "InteractionHoverBrush", false)]
    [InlineData("ghost", "InteractionHoverBrush", true)]
    [InlineData("primary", "AccentHoverBrush", false)]
    [InlineData("primary", "AccentHoverBrush", true)]
    [InlineData("danger", "DangerHoverBrush", false)]
    [InlineData("danger", "DangerHoverBrush", true)]
    public void SemanticButtonColorsReachTheFluentPresenterWithoutFocusLayoutShift(
        string buttonClass, string hoverResource, bool dark)
    {
        var button = new Button { Content = "确认", Width = 120, Height = 36 };
        button.Classes.Add(buttonClass);
        var window = new Window
        {
            Width = 240, Height = 120, Content = button,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(p => p.Name == "PART_ContentPresenter");
            var bounds = presenter.Bounds;
            button.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(bounds, presenter.Bounds);
            Assert.NotNull(button.FocusAdorner);
            var point = button.TranslatePoint(new Point(60, 18), window)!.Value;
            window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
            AssertBrush(window, hoverResource, presenter.Background);
            window.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            AssertBrush(window, hoverResource, presenter.Background);
            window.MouseUp(point, MouseButton.Left);
        }
        finally { window.Close(); }
    }

    private static void AssertBrush(Window window, string key, IBrush? actual)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var expected));
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(expected).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
    }
}
