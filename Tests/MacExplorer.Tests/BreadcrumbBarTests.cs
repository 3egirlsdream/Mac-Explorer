using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task BreadcrumbDropdownShowsOnlyImmediateVisibleDirectoriesAndClickNavigates()
    {
        using var host = new BreadcrumbTestHost();
        host.Seed("Zulu", true);
        host.Seed("Alpha", true);
        host.Seed(".hidden", true);
        host.Seed("notes.txt", false);
        host.Seed("Alpha/Nested", true);
        host.Seed("Example.app", true);
        host.Seed("UPPERCASE.APP", true);

        var arrow = host.Arrow(host.Root);
        host.Click(arrow);
        await host.WaitForDirectories(2);

        Assert.Equal(new[] { "Alpha", "Zulu" }, host.Directories.Select(item => item.Name));
        Assert.Equal(host.Root, host.ViewModel.CurrentPath);
        Assert.Contains("open", arrow.Classes);
        Assert.False(host.Bar.FindControl<TextBox>("PathInput")!.IsVisible);

        var row = Assert.IsType<ListBoxItem>(host.List.ContainerFromIndex(0));
        host.Click(row);
        Assert.Equal(host.Root + "/Alpha", host.ViewModel.CurrentPath);
        Assert.False(host.Popup.IsOpen);
        Assert.DoesNotContain("open", arrow.Classes);
    }

    [AvaloniaFact]
    public void BreadcrumbArrowHoverUsesSharedBrushAndLabelStaysTransparent()
    {
        using var host = new BreadcrumbTestHost();
        var arrow = host.Arrow(host.Root);
        host.Window.MouseMove(host.Center(arrow));
        Dispatcher.UIThread.RunJobs();

        var presenter = arrow.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.True(Application.Current!.TryGetResource("InteractionHoverBrush", host.Window.ActualThemeVariant, out var hover));
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(hover).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);

        var label = host.Bar.GetVisualDescendants().OfType<Button>()
            .Last(button => button.Classes.Contains("breadcrumb-label"));
        host.Window.MouseMove(host.Center(label));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(label.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(arrow.Background).Color);
    }

    [AvaloniaFact]
    public async Task BreadcrumbArrowRepeatedClicksDoNotHighlightTheBarButPathEditingDoes()
    {
        using var host = new BreadcrumbTestHost();
        host.Seed("Child", true);
        var surface = Assert.IsType<Border>(host.Bar.Content);
        var input = host.Bar.FindControl<TextBox>("PathInput")!;
        var arrow = host.Arrow(host.Root);

        host.Click(arrow);
        await host.WaitForDirectories(1);
        Assert.Equal(default, surface.BoxShadow);
        host.Click(arrow);

        Assert.True(arrow.IsFocused);
        Assert.False(host.Popup.IsOpen);
        Assert.False(input.IsVisible);
        Assert.Equal(default, surface.BoxShadow);

        host.Bar.FocusPathInput();
        Dispatcher.UIThread.RunJobs();
        Assert.True(input.IsFocused);
        Assert.NotEqual(default, surface.BoxShadow);
        host.Bar.CloseTransientUi();
        Assert.Equal(default, surface.BoxShadow);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BreadcrumbDropdownUsesTheNewToolbarMenuAppearance(bool dark)
    {
        using var host = new BreadcrumbTestHost();
        host.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        host.Seed("Child", true);
        host.Click(host.Arrow(host.Root));
        await host.WaitForDirectories(1);

        var toolbar = new FinderToolbar();
        var toolbarWindow = new Window
        {
            Width = 500, Height = 400, Content = toolbar,
            RequestedThemeVariant = host.Window.RequestedThemeVariant
        };
        try
        {
            toolbarWindow.Show();
            var newPopup = toolbar.FindControl<Popup>("NewDropdown")!;
            newPopup.IsOpen = true;
            Dispatcher.UIThread.RunJobs();

            var expected = Assert.IsType<Border>(newPopup.Child);
            var actual = Assert.IsType<Border>(host.Popup.Child);
            Assert.Equal(expected.Padding, actual.Padding);
            Assert.Equal(expected.Margin, actual.Margin);
            Assert.Equal(expected.CornerRadius, actual.CornerRadius);
            Assert.Equal(expected.BorderThickness, actual.BorderThickness);
            Assert.Equal(expected.BoxShadow, actual.BoxShadow);
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(expected.Background).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(actual.Background).Color);

            var newFolder = expected.GetVisualDescendants().OfType<Button>().First();
            var directory = Assert.IsType<ListBoxItem>(host.List.ContainerFromIndex(0));
            Assert.Equal(newFolder.MinHeight, directory.MinHeight);
            Assert.Equal(newFolder.Padding, directory.Padding);
            Assert.Equal(newFolder.CornerRadius, directory.CornerRadius);
            Assert.Equal(newFolder.FontSize, directory.FontSize);

            var label = directory.GetVisualDescendants().OfType<TextBlock>().Single();
            var labelCenter = label.TranslatePoint(new Point(0, label.Bounds.Height / 2), directory)!.Value;
            Assert.InRange(Math.Abs(labelCenter.Y - directory.Bounds.Height / 2), 0, 0.5);

            foreach (var shadow in actual.BoxShadow)
            {
                Assert.Equal(0, shadow.OffsetX);
                Assert.Equal(0, shadow.OffsetY);
            }

            newPopup.IsOpen = false;
            var contextMenu = new ContextMenu { Items = { new MenuItem { Header = "打开" } } };
            contextMenu.Open(toolbar);
            Dispatcher.UIThread.RunJobs();
            Assert.False(contextMenu.WindowManagerAddShadowHint);
            var menuShadow = contextMenu.GetVisualDescendants().OfType<Border>()
                .Single(border => border.BoxShadow.Count > 0);
            Assert.Equal(actual.BoxShadow, menuShadow.BoxShadow);
            contextMenu.Close();
        }
        finally
        {
            toolbarWindow.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("escape")]
    [InlineData("outside")]
    [InlineData("toggle")]
    [InlineData("navigate")]
    [InlineData("detach")]
    public async Task BreadcrumbDropdownClosesAndClearsArrowState(string action)
    {
        using var host = new BreadcrumbTestHost();
        host.Seed("Child", true);
        var arrow = host.Arrow(host.Root);
        host.Click(arrow);
        await host.WaitForDirectories(1);

        switch (action)
        {
            case "escape":
                host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                break;
            case "outside":
                host.Window.MouseDown(new Point(700, 380), MouseButton.Left);
                host.Window.MouseUp(new Point(700, 380), MouseButton.Left);
                break;
            case "toggle":
                host.Click(arrow);
                break;
            case "navigate":
                host.Navigation.CurrentPath = "/tmp";
                break;
            case "detach":
                host.Window.Content = null;
                break;
        }

        Assert.False(host.Popup.IsOpen);
        Assert.DoesNotContain("open", arrow.Classes);
    }

    [AvaloniaFact]
    public async Task BreadcrumbDropdownIgnoresOldLoadWhenAnotherArrowOpens()
    {
        using var host = new BreadcrumbTestHost();
        host.Seed("CurrentChild", true);
        var oldLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.BeforeEnumerate = async (path, _) =>
        {
            if (path == "/tmp")
            {
                oldStarted.TrySetResult();
                await oldLoad.Task;
            }
        };
        host.Click(host.Arrow("/tmp"));
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        host.Click(host.Arrow(host.Root));
        await host.WaitForDirectories(1);
        oldLoad.TrySetResult();
        await Task.Delay(50);

        Assert.Equal("CurrentChild", Assert.Single(host.Directories).Name);
        Assert.Same(host.Arrow(host.Root), host.Popup.PlacementTarget);
        Assert.True(host.Bar.TryCloseTransientUi());
        Assert.False(host.Popup.IsOpen);
    }

    [AvaloniaFact]
    public async Task BreadcrumbDropdownSupportsKeyboardNavigationAndEmptyDirectories()
    {
        using var host = new BreadcrumbTestHost();
        host.Click(host.Arrow(host.Root));
        await host.WaitUntil(() => host.Bar.FindControl<TextBlock>("DirectoryDropdownStatus")!.Text == "没有子目录");
        Assert.False(host.List.IsVisible);
        host.Bar.CloseTransientUi();

        host.Seed("Child", true);
        host.Arrow(host.Root).Focus();
        host.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        await host.WaitForDirectories(1);
        host.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(0, host.List.SelectedIndex);
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(host.Root + "/Child", host.ViewModel.CurrentPath);
        Assert.False(host.Popup.IsOpen);
    }

    [AvaloniaFact]
    public async Task BreadcrumbDropdownReportsReadErrorsAndHonorsHiddenFolderSetting()
    {
        using var host = new BreadcrumbTestHost();
        host.Seed(".hidden", true);
        host.SortFilter.HideDotFolders = false;
        var result = await host.ViewModel.GetBreadcrumbDirectoriesAsync(host.Root, CancellationToken.None);
        Assert.Equal(".hidden", Assert.Single(result).Name);

        host.Service.BeforeEnumerate = (_, _) => throw new UnauthorizedAccessException();
        host.Click(host.Arrow(host.Root));
        await host.WaitUntil(() => host.Bar.FindControl<TextBlock>("DirectoryDropdownStatus")!.Text == "无法读取此目录");
        Assert.True(host.Popup.IsOpen);
        Assert.False(host.List.IsVisible);
    }

    [AvaloniaFact]
    public async Task BreadcrumbSearchFiltersLiveAndResetsWithoutReloadingDirectories()
    {
        using var host = new BreadcrumbTestHost(inWorkspace: true);
        foreach (var name in new[] { "Alpha", "Alphabet", "Beta", "文档", "Example.app" })
            host.Seed(name, true);
        host.Seed("notes.txt", false);
        host.Click(host.Arrow(host.Root));
        await host.WaitForDirectories(4);

        var search = host.Bar.FindControl<TextBox>("DirectoryDropdownSearch")!;
        Assert.True(search.IsFocused);
        host.Click(search);
        Assert.True(host.Popup.IsOpen);
        Assert.Equal(default, Assert.IsType<Border>(host.Bar.Content).BoxShadow);
        var readCount = host.Service.EnumerateDirectoryCallCount;

        search.Text = "ALP";
        await host.WaitForDirectories(2);
        Assert.Equal(new[] { "Alpha", "Alphabet" }, host.Directories.Select(directory => directory.Name));
        search.Text = "文";
        await host.WaitForDirectories(1);
        Assert.Equal("文档", Assert.Single(host.Directories).Name);
        search.Text = "missing";
        await host.WaitUntil(() => host.List.ItemCount == 0);
        Assert.False(host.List.IsVisible);
        Assert.Equal("没有匹配的目录", host.Bar.FindControl<TextBlock>("DirectoryDropdownStatus")!.Text);
        Assert.True(search.IsFocused);
        Assert.True(host.Popup.IsOpen);

        search.Text = string.Empty;
        await host.WaitForDirectories(4);
        Assert.Equal(readCount, host.Service.EnumerateDirectoryCallCount);
        search.Text = "Beta";
        await host.WaitForDirectories(1);
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(host.Popup.IsOpen);

        host.Click(host.Arrow(host.Root));
        await host.WaitForDirectories(4);
        Assert.Equal(string.Empty, search.Text);
        search.Text = "Beta";
        await host.WaitForDirectories(1);
        host.Click(Assert.IsType<ListBoxItem>(host.List.ContainerFromIndex(0)));
        Assert.Equal(host.Root + "/Beta", host.ViewModel.CurrentPath);
        Assert.False(host.Popup.IsOpen);
    }

    [AvaloniaFact]
    public async Task BreadcrumbSearchEnteredDuringLoadingAppliesWhenReadyAndSupportsKeyboardNavigation()
    {
        using var host = new BreadcrumbTestHost();
        host.Seed("Alpha", true);
        host.Seed("Beta", true);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Service.BeforeEnumerate = (_, token) => loaded.Task.WaitAsync(token);
        host.Click(host.Arrow(host.Root));

        var search = host.Bar.FindControl<TextBox>("DirectoryDropdownSearch")!;
        search.Text = "bet";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("正在加载…", host.Bar.FindControl<TextBlock>("DirectoryDropdownStatus")!.Text);
        loaded.SetResult();
        await host.WaitForDirectories(1);
        Assert.Equal("Beta", Assert.Single(host.Directories).Name);
        Assert.True(search.IsFocused);

        host.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(0, host.List.SelectedIndex);
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(host.Root + "/Beta", host.ViewModel.CurrentPath);
        Assert.False(host.Popup.IsOpen);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BreadcrumbScrollbarSurvivesWorkspaceOutsideClickHandling(bool clickTrack)
    {
        using var host = new BreadcrumbTestHost(inWorkspace: true);
        for (var i = 0; i < 120; i++)
            host.Seed($"Folder{i:000}", true);
        host.Click(host.Arrow(host.Root));
        await host.WaitForDirectories(120);

        var scrollBar = host.List.GetVisualDescendants().OfType<ScrollBar>()
            .Single(bar => bar.IsEffectivelyVisible && bar.Orientation == Orientation.Vertical);
        var scrollViewer = scrollBar.FindAncestorOfType<ScrollViewer>()!;
        var track = scrollBar.GetVisualDescendants().OfType<Track>().Single();
        var start = clickTrack
            ? track.TranslatePoint(new Point(track.Bounds.Width / 2, track.Bounds.Height - 8), host.Window)!.Value
            : host.Center(track.Thumb!);
        var end = clickTrack ? start : start + new Vector(0, 70);
        host.Window.MouseMove(start);
        host.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        Assert.True(host.Popup.IsOpen);
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        host.Window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(host.Popup.IsOpen);
        Assert.True(scrollViewer.Offset.Y > 0);
        Assert.Equal(host.Root, host.ViewModel.CurrentPath);

        scrollViewer.Offset = default;
        Dispatcher.UIThread.RunJobs();
        host.Click(Assert.IsType<ListBoxItem>(host.List.ContainerFromIndex(0)));
        Assert.Equal(host.Root + "/Folder000", host.ViewModel.CurrentPath);
        Assert.False(host.Popup.IsOpen);
    }

    private sealed class BreadcrumbTestHost : IDisposable
    {
        private readonly Application _application = Application.Current!;
        private readonly IStyle[] _styles;
        private readonly ExplorerWorkspaceView? _workspace;
        private readonly ExplorerTabViewModel? _tab;
        public string Root => "/tmp/BreadcrumbTests";
        public FakeFileService Service { get; }
        public NavigationViewModel Navigation { get; }
        public SortFilterViewModel SortFilter { get; } = new();
        public FileListViewModel ViewModel { get; }
        public BreadcrumbBar Bar { get; }
        public Window Window { get; }
        public Popup Popup => Bar.FindControl<Popup>("DirectoryDropdownPopup")!;
        public ListBox List => Bar.FindControl<ListBox>("DirectoryDropdownList")!;
        public IEnumerable<BreadcrumbSegment> Directories => List.Items.OfType<BreadcrumbSegment>();

        public BreadcrumbTestHost(bool inWorkspace = false)
        {
            _styles = [new FluentTheme(),
                (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")),
                (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml"))];
            _application.Styles.Insert(0, _styles[0]);
            _application.Styles.Add(_styles[1]);
            _application.Styles.Add(_styles[2]);
            Service = new FakeFileService(Root);
            Navigation = new NavigationViewModel(Service) { CurrentPath = Root, IsHomePage = false };
            ViewModel = CreateViewModel(Service, navigation: Navigation, sortFilter: SortFilter);
            Navigation.UpdateBreadcrumbs();
            if (inWorkspace)
            {
                _tab = new ExplorerTabViewModel(ViewModel) { IsActive = true };
                _workspace = new ExplorerWorkspaceView { DataContext = _tab };
                Bar = _workspace.FindControl<BreadcrumbBar>("BreadcrumbControl")!;
                Window = new Window { Width = 1280, Height = 600, Content = _workspace };
                Window.AddHandler(InputElement.PointerPressedEvent, (_, e) => _workspace.CloseTransientUi(e.Source),
                    RoutingStrategies.Tunnel, handledEventsToo: true);
            }
            else
            {
                Bar = new BreadcrumbBar { DataContext = ViewModel, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
                Window = new Window { Width = 800, Height = 420, Content = Bar };
            }
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Seed(string name, bool directory) => Service.Seed(new FileSystemEntry
        {
            Name = Path.GetFileName(name), FullPath = Root + "/" + name, IsDirectory = directory,
            IconKey = directory ? "folder" : "file-generic"
        });

        public Button Arrow(string path) => Bar.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("breadcrumb-dropdown")
                && button.DataContext is BreadcrumbSegment segment && segment.FullPath == path);

        public Point Center(Control control) => control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;

        public void Click(Control control)
        {
            var point = Center(control);
            Window.MouseMove(point);
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public Task WaitForDirectories(int count) => WaitUntil(() => List.IsVisible && List.ItemCount == count);

        public async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            Assert.True(condition());
        }

        public void Dispose()
        {
            Window.Close();
            _workspace?.Dispose();
            _tab?.Dispose();
            ViewModel.Dispose();
            foreach (var style in _styles)
                _application.Styles.Remove(style);
        }
    }
}
