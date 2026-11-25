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
using Microsoft.Extensions.DependencyInjection;
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
        ClickZoomButton(fixture);
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

        ClickZoomButton(fixture);
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
        ClickZoomButton(fixture);
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

    [AvaloniaFact(Explicit = true)]
    public void GalleryFloatingBarStepButtonsAndSplitterResizeWork()
    {
        using var fixture = CreateFixture(8);
        fixture.Vm.SelectEntry(fixture.Rows[0]);
        fixture.Vm.SetThumbnailZoomLevel(FileThumbnailSizing.GalleryLevel);
        Dispatcher.UIThread.RunJobs();

        var gallery = fixture.View.GalleryView!;
        var bar = gallery.GalleryActionBar;
        Assert.True(bar.IsVisible);
        Assert.Equal("1 / 8", bar.CounterText.Text);
        Assert.Equal(fixture.Rows[0].DisplayName, bar.NameText.Text);
        Assert.Equal(10, gallery.GalleryStage.Margin.Right);
        Assert.False(gallery.GalleryPreviousSlot.IsVisible);
        Assert.True(gallery.GalleryNextSlot.IsVisible);

        // Opening the inspector tightens the right stage margin to balance the 6px resize gutter
        // and compacts duplicate identity text in the floating action bar.
        fixture.Vm.IsInfoPanelVisible = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, gallery.GalleryStage.Margin.Right);
        Assert.False(bar.NameText.IsVisible);
        fixture.Vm.IsInfoPanelVisible = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(10, gallery.GalleryStage.Margin.Right);

        gallery.GalleryNextButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("2 / 8", bar.CounterText.Text);
        Assert.Same(fixture.Rows[1], Assert.Single(fixture.Vm.SelectedEntries));
        Assert.True(gallery.GalleryPreviousSlot.IsVisible);

        Assert.True(gallery.GalleryStrip.Focus());
        fixture.Window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("8 / 8", bar.CounterText.Text);
        Assert.False(gallery.GalleryNextSlot.IsVisible);
        Assert.True(gallery.GalleryPreviousSlot.IsVisible);

        var initialHeight = gallery.GalleryStrip.Height;
        Assert.Equal(108, initialHeight);
        var splitterCenter = CenterIn(gallery.GalleryStripSplitter, fixture.Window);
        var draggedUp = new Point(splitterCenter.X, splitterCenter.Y - 36);
        fixture.Window.MouseDown(splitterCenter, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        fixture.Window.MouseMove(draggedUp, RawInputModifiers.LeftMouseButton);
        fixture.Window.MouseUp(draggedUp, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(gallery.GalleryStrip.Height > initialHeight + 20);

        gallery.GalleryStripSplitter.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(108, gallery.GalleryStrip.Height);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task GalleryFolderCollageCardDisplaysChildrenAndCount()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_GalleryFolderCard_");
        try
        {
            var folderPath = System.IO.Path.Combine(root.FullName, "DesignAssets");
            Directory.CreateDirectory(folderPath);
            await File.WriteAllTextAsync(System.IO.Path.Combine(folderPath, "a.txt"), "hello", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(System.IO.Path.Combine(folderPath, "b.txt"), "world", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(System.IO.Path.Combine(folderPath, "c.txt"), "gallery", TestContext.Current.CancellationToken);

            var theme = new FastListTestTheme();
            var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.Grid };
            var vm = CreateViewModel(new FakeFileService(root.FullName), sortFilter: sortFilter);
            var folderEntry = new FileSystemEntry
            {
                FullPath = folderPath, Name = "DesignAssets", IsDirectory = true, IsReadable = true, IconKey = "folder"
            };
            vm.Entries.Add(folderEntry);
            var tab = new ExplorerTabViewModel(vm) { IsActive = true };
            var workspace = new ExplorerWorkspaceView { DataContext = tab, ForceCompact = true };
            var window = new Window { Width = 960, Height = 680, Content = workspace };
            foreach (var file in new[] { "ThemeTokens", "ComponentStyles" })
                window.Styles.Add(new StyleInclude(new Uri("avares://MacExplorer/"))
                {
                    Source = new Uri($"avares://MacExplorer/Assets/{file}.axaml")
                });
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.SelectEntry(folderEntry);
                vm.SetThumbnailZoomLevel(FileThumbnailSizing.GalleryLevel);
                Dispatcher.UIThread.RunJobs();

                var gallery = workspace.FileListView.GalleryView!;
                for (var attempt = 0; attempt < 30 && gallery.GalleryFolderCollageGrid.Children.Count < 3; attempt++)
                {
                    await Task.Delay(30, TestContext.Current.CancellationToken);
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.True(gallery.GalleryFolderCard.IsVisible);
                Assert.False(gallery.GalleryPreviewHost.IsVisible);
                Assert.Equal("DesignAssets", gallery.GalleryFolderName.Text);
                Assert.Equal(3, gallery.GalleryFolderCollageGrid.Children.Count);
                Assert.Contains("3", gallery.GalleryFolderMeta.Text);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
                tab.Dispose();
                vm.Dispose();
                theme.Dispose();
            }
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task GalleryImageControlsRotateZoomCheckerboardAndEscapeOrder()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_GalleryImageControls_");
        try
        {
            var pngPath = System.IO.Path.Combine(root.FullName, "icon.png");
            await File.WriteAllBytesAsync(pngPath, CreateSamplePngBytes(), TestContext.Current.CancellationToken);
            var entry = new FileSystemEntry
            {
                FullPath = pngPath, Name = "icon.png", Extension = ".png",
                Size = new FileInfo(pngPath).Length, IsReadable = true, IconKey = "file-image"
            };

            var thumbnails = new MacExplorer.Platforms.MacCatalyst.Services.MacThumbnailService(
                System.IO.Path.Combine(root.FullName, "cache"), 1024 * 1024, 0.8);
            await using var serviceFixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IThumbnailService>(thumbnails));

            var theme = new FastListTestTheme();
            var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.Grid };
            var vm = CreateViewModel(new FakeFileService(root.FullName), sortFilter: sortFilter);
            vm.Entries.Add(entry);
            var tab = new ExplorerTabViewModel(vm) { IsActive = true };
            var workspace = new ExplorerWorkspaceView { DataContext = tab, ForceCompact = true };
            var window = new Window { Width = 960, Height = 680, Content = workspace };
            foreach (var file in new[] { "ThemeTokens", "ComponentStyles" })
                window.Styles.Add(new StyleInclude(new Uri("avares://MacExplorer/"))
                {
                    Source = new Uri($"avares://MacExplorer/Assets/{file}.axaml")
                });
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.SelectEntry(entry);
                vm.SetThumbnailZoomLevel(FileThumbnailSizing.GalleryLevel);
                Dispatcher.UIThread.RunJobs();

                var ready = await workspace.FileListView.GalleryPreviewReady;
                Dispatcher.UIThread.RunJobs();
                Assert.True(ready);

                var gallery = workspace.FileListView.GalleryView!;
                var bar = gallery.GalleryActionBar;
                var preview = workspace.FileListView.GalleryPreview!;
                Assert.True(preview.HasGalleryImage);
                Assert.True(preview.GalleryShowCheckerboard); // auto-enabled for .png
                Assert.True(bar.ZoomButton.IsVisible);
                Assert.True(bar.RotateButton.IsVisible);
                Assert.True(bar.CheckerButton.IsVisible);
                Assert.True(bar.CheckerButton.IsChecked == true);

                Assert.True(gallery.GalleryStrip.Focus());
                window.KeyPress(Key.R, RawInputModifiers.None, PhysicalKey.R, null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(90, preview.GalleryImageRotation);

                window.KeyPress(Key.C, RawInputModifiers.None, PhysicalKey.C, null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(preview.GalleryShowCheckerboard);
                Assert.False(bar.CheckerButton.IsChecked == true);

                window.KeyPress(Key.Z, RawInputModifiers.None, PhysicalKey.Z, null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(preview.IsGalleryImageZoomed);
                Assert.True(bar.ZoomButton.IsChecked == true);

                // First Escape resets zoom without exiting gallery.
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(preview.IsGalleryImageZoomed);
                Assert.True(workspace.FileListView.IsGalleryVisible);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
                tab.Dispose();
                vm.Dispose();
                theme.Dispose();
            }
        }
        finally { root.Delete(true); }
    }

    private static byte[] CreateSamplePngBytes()
    {
        using var bmp = new SkiaSharp.SKBitmap(96, 64, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using var canvas = new SkiaSharp.SKCanvas(bmp);
        canvas.Clear(SkiaSharp.SKColors.Transparent);
        using var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFF, 0x4F, 0x9A), IsAntialias = true };
        canvas.DrawRoundRect(new SkiaSharp.SKRoundRect(new SkiaSharp.SKRect(12, 8, 84, 56), 12, 12), paint);
        using var image = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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

    private static void ClickZoomButton(GalleryFixture fixture)
    {
        // Zoom changes can update status-bar content and invalidate the earlier hit point.
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
        Click(fixture.Window, CenterIn(fixture.Button, fixture.Window));
    }

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
