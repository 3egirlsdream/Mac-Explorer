using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileList")]
public sealed class FileGalleryTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public void ZoomPopupKeyboardAndWheelSwitchBetweenGridSizesAndGallery()
    {
        using var fixture = CreateFixture(20);
        using var otherPane = CreateViewModel(new FakeFileService("/tmp/other-gallery"),
            sortFilter: new SortFilterViewModel { ViewMode = ViewMode.Grid });
        fixture.Vm.SelectEntry(fixture.Rows[1]);
        Assert.Equal(56, fixture.List.GridIconSize);
        Assert.True(fixture.Button.IsEffectivelyVisible);
        Assert.False(fixture.Popup.IsOpen);

        var buttonPoint = CenterIn(fixture.Button, fixture.Window);
        var statusRow = Assert.IsType<Grid>(fixture.Workspace.FindControl<Border>("StatusBar")!.Child);
        var statusSummary = Assert.IsType<TextBlock>(statusRow.Children[0]);
        var resizeIcon = fixture.Button.FindDescendantOfType<PathIcon>()!;
        Assert.Equal(buttonPoint.Y, CenterIn(statusSummary, fixture.Window).Y, precision: 1);
        Assert.Equal(buttonPoint.Y, CenterIn(resizeIcon, fixture.Window).Y, precision: 1);

        Click(fixture.Window, buttonPoint);
        Assert.True(fixture.Popup.IsOpen);
        Assert.Equal(new[] { "32 pt", "56 pt", "96 pt", "128 pt", LocalizationText.Get("file_gallery.title") },
            fixture.Labels.Select(label => label.Text));
        var firstLabelBaseline = fixture.Labels[0].TranslatePoint(default, fixture.Window)!.Value.Y + fixture.Labels[0].TextLayout.Baseline;
        Assert.All(fixture.Labels, label =>
        {
            Assert.Equal(statusSummary.FontFamily, label.FontFamily);
            Assert.Equal(statusSummary.LineHeight, label.LineHeight);
            Assert.Equal(firstLabelBaseline, label.TranslatePoint(default, fixture.Window)!.Value.Y + label.TextLayout.Baseline, precision: 1);
        });
        Assert.True(fixture.Labels[1].Classes.Contains("active"));
        Assert.True(fixture.Slider.IsFocused);

        fixture.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(96, fixture.List.GridIconSize);
        Assert.True(fixture.Labels[2].Classes.Contains("active"));
        Click(fixture.Window, buttonPoint);
        Assert.False(fixture.Popup.IsOpen);

        fixture.Window.MouseWheel(fixture.List.TranslatePoint(new Point(40, 40), fixture.Window)!.Value,
            new Vector(0, 1), RawInputModifiers.Control);
        Assert.Equal(3, fixture.Slider.Value);
        Assert.Equal(128, fixture.List.GridIconSize);

        fixture.Window.MouseWheel(fixture.List.TranslatePoint(new Point(40, 40), fixture.Window)!.Value,
            new Vector(0, 1), RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FileThumbnailSizing.GalleryLevel, fixture.Slider.Value);
        Assert.True(fixture.View.IsGalleryVisible);
        Assert.False(otherPane.IsGalleryMode);

        fixture.Window.MouseWheel(fixture.View.GalleryFilmstrip!.TranslatePoint(new Point(40, 40), fixture.Window)!.Value,
            new Vector(0, -1), RawInputModifiers.Control);
        Assert.False(fixture.View.IsGalleryVisible);
        Assert.Equal(3, fixture.Slider.Value);
        Assert.Equal(128, fixture.List.GridIconSize);

        Click(fixture.Window, buttonPoint);
        Assert.True(fixture.Popup.IsOpen);
        fixture.Vm.SetViewMode(ViewMode.List);
        Assert.False(fixture.Popup.IsOpen);
        Assert.False(fixture.Button.IsEffectivelyVisible);
        Assert.Equal(FastFileList.RowHeight, fixture.List.ItemHeight);
    }

    [AvaloniaFact(Explicit = true)]
    public void GalleryFilmstripVirtualizesAndTracksSelectionAcrossRemovalSortFilterAndVisibility()
    {
        using var fixture = CreateFixture(1000);
        fixture.Vm.SelectEntry(fixture.Rows[1]);
        fixture.Vm.SetThumbnailZoomLevel(FileThumbnailSizing.GalleryLevel);
        Dispatcher.UIThread.RunJobs();

        Assert.True(fixture.View.IsGalleryVisible);
        var strip = fixture.View.GalleryFilmstrip!;
        Assert.Same(fixture.Rows[1], strip.SelectedItem);
        Assert.Equal(fixture.Rows, strip.Items.Cast<FileSystemEntry>());
        Assert.Null(strip.ContainerFromIndex(999));

        Assert.True(strip.Focus());
        fixture.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(fixture.Rows[2], Assert.Single(fixture.Vm.SelectedEntries));

        fixture.Vm.Entries.Remove(fixture.Rows[2]);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(fixture.Rows[3], strip.SelectedItem);
        Assert.Same(fixture.Rows[3], Assert.Single(fixture.Vm.SelectedEntries));

        fixture.SortFilter.SetRawEntries(fixture.Vm.Entries.ToArray());
        fixture.Vm.SetSort(SortField.Name, ascending: false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(fixture.Vm.Entries, strip.Items.Cast<FileSystemEntry>());
        Assert.Same(fixture.Rows[3], strip.SelectedItem);

        fixture.Vm.FileNameFilter = "File 3";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(fixture.Vm.Entries, strip.Items.Cast<FileSystemEntry>());
        Assert.Same(fixture.Rows[3], strip.SelectedItem);

        fixture.Workspace.IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(strip.ItemsSource);
        Assert.True(fixture.Vm.IsGalleryMode);
        fixture.Workspace.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(fixture.Rows[3], strip.SelectedItem);
    }

    [AvaloniaFact(Explicit = true)]
    public void ZoomSliderDragSnapsOnReleaseAndEscapeClosesPopupBeforeGallery()
    {
        using var fixture = CreateFixture(20);
        fixture.Vm.SetThumbnailZoomLevel(3);
        Dispatcher.UIThread.RunJobs();

        var buttonPoint = CenterIn(fixture.Button, fixture.Window);
        Click(fixture.Window, buttonPoint);
        Assert.True(fixture.Popup.IsOpen);

        var thumb = fixture.Slider.FindDescendantOfType<Thumb>()!;
        var rail = fixture.Slider.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SliderRail");
        var fill = fixture.Slider.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SliderFill");
        var stops = rail.GetVisualDescendants().OfType<Ellipse>().ToArray();
        Assert.Equal(5, stops.Length);
        foreach (var (label, stop) in fixture.Labels.Zip(stops))
            Assert.Equal(CenterIn(label, fixture.Window).X, CenterIn(stop, fixture.Window).X, precision: 1);
        Assert.True(fixture.Window.TryFindResource("SliderFillBrush", fixture.Window.ActualThemeVariant, out var expectedFill));
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(expectedFill).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(fill.Background).Color);
        Assert.True(rail.TranslatePoint(new Point(0, rail.Bounds.Height), fixture.Window)!.Value.Y < buttonPoint.Y);

        var thumbPoint = CenterIn(thumb, fixture.Window);
        var fillEnd = fill.TranslatePoint(new Point(fill.Bounds.Width, 0), fixture.Window)!.Value;
        Assert.Equal(thumbPoint.X, fillEnd.X, precision: 1);

        var lastStop = CenterIn(stops[4], fixture.Window);
        var dragPoint = new Point(thumbPoint.X + (lastStop.X - thumbPoint.X) * 0.75, thumbPoint.Y);
        fixture.Window.MouseDown(thumbPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        fixture.Window.MouseMove(dragPoint, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(fixture.Slider.Value, 3.74, 3.76);
        Assert.Equal(dragPoint.X, fill.TranslatePoint(new Point(fill.Bounds.Width, 0), fixture.Window)!.Value.X, precision: 1);
        Assert.True(fixture.Labels[4].Classes.Contains("active"));
        Assert.Equal(3, fixture.Vm.ThumbnailZoomLevel);
        Assert.False(fixture.View.IsGalleryVisible);

        fixture.Window.MouseUp(dragPoint, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FileThumbnailSizing.GalleryLevel, fixture.Slider.Value);
        Assert.Equal(rail.Bounds.Width, fill.Bounds.Width);
        Assert.True(fixture.Popup.IsOpen);
        Assert.True(fixture.View.IsGalleryVisible);

        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(fixture.Popup.IsOpen);
        Assert.True(fixture.Button.IsFocused);
        Assert.True(fixture.View.IsGalleryVisible);
        Assert.True(fixture.Workspace.TryHandleEscape());
        Assert.False(fixture.View.IsGalleryVisible);

        Click(fixture.Window, buttonPoint);
        Click(fixture.Window, CenterIn(stops[0], fixture.Window));
        Assert.Equal(32, fixture.List.GridIconSize);
        Assert.True(fixture.Labels[0].Classes.Contains("active"));
        Click(fixture.Window, fixture.List.TranslatePoint(new Point(40, 40), fixture.Window)!.Value);
        Assert.False(fixture.Popup.IsOpen);
    }

    private GalleryFixture CreateFixture(int count)
    {
        var theme = new FastListTestTheme();
        var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.Grid };
        var vm = CreateViewModel(new FakeFileService("/tmp/gallery"), sortFilter: sortFilter);
        var rows = Enumerable.Range(0, count).Select(index => new FileSystemEntry
        {
            FullPath = $"/tmp/gallery/{index}", Name = $"File {index}", IsReadable = false
        }).ToArray();
        foreach (var entry in rows) vm.Entries.Add(entry);
        var tab = new ExplorerTabViewModel(vm) { IsActive = true };
        var workspace = new ExplorerWorkspaceView { DataContext = tab, ForceCompact = true };
        var window = new Window { Width = 960, Height = 680, Content = workspace };
        foreach (var file in new[] { "ThemeTokens", "ComponentStyles" })
            window.Styles.Add(new StyleInclude(new Uri("avares://MacExplorer/"))
            {
                Source = new Uri($"avares://MacExplorer/Assets/{file}.axaml")
            });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new GalleryFixture(theme, sortFilter, vm, tab, workspace, window, rows);
    }

    private static Point CenterIn(Control control, Visual root) =>
        control.TranslatePoint(new Rect(control.Bounds.Size).Center, root)!.Value;

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class GalleryFixture(
        FastListTestTheme theme,
        SortFilterViewModel sortFilter,
        FileListViewModel vm,
        ExplorerTabViewModel tab,
        ExplorerWorkspaceView workspace,
        Window window,
        FileSystemEntry[] rows) : IDisposable
    {
        public SortFilterViewModel SortFilter => sortFilter;
        public FileListViewModel Vm => vm;
        public ExplorerWorkspaceView Workspace => workspace;
        public FileListView View => workspace.FileListView;
        public FastFileList List => View.FindControl<FastFileList>("FastList")!;
        public Slider Slider => workspace.FindControl<Slider>("ThumbnailZoomSlider")!;
        public Button Button => workspace.FindControl<Button>("ThumbnailZoomButton")!;
        public Popup Popup => workspace.FindControl<Popup>("ThumbnailZoomPopup")!;
        public TextBlock[] Labels => workspace.FindControl<UniformGrid>("ThumbnailZoomLabels")!.Children.Cast<TextBlock>().ToArray();
        public Window Window => window;
        public FileSystemEntry[] Rows => rows;

        public void Dispose()
        {
            window.Close();
            workspace.Dispose();
            tab.Dispose();
            vm.Dispose();
            theme.Dispose();
        }
    }
}
