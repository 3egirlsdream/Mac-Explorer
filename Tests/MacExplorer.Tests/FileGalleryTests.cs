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
    [AvaloniaFact]
    public void SliderAndWheelShareZoomAndGalleryKeepsSelectionAfterRemoval()
    {
        using var theme = new FastListTestTheme();
        var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.Grid };
        using var vm = CreateViewModel(new FakeFileService("/tmp/gallery"), sortFilter: sortFilter);
        using var otherPane = CreateViewModel(new FakeFileService("/tmp/other-gallery"),
            sortFilter: new SortFilterViewModel { ViewMode = ViewMode.Grid });
        var rows = Enumerable.Range(0, 1000).Select(index => new FileSystemEntry
        {
            FullPath = $"/tmp/gallery/{index}", Name = $"File {index}", IsReadable = false
        }).ToArray();
        foreach (var entry in rows) vm.Entries.Add(entry);
        using var tab = new ExplorerTabViewModel(vm) { IsActive = true };
        using var workspace = new ExplorerWorkspaceView { DataContext = tab, ForceCompact = true };
        var view = workspace.FileListView;
        var list = view.FindControl<FastFileList>("FastList")!;
        var slider = workspace.FindControl<Slider>("ThumbnailZoomSlider")!;
        var button = workspace.FindControl<Button>("ThumbnailZoomButton")!;
        var popup = workspace.FindControl<Popup>("ThumbnailZoomPopup")!;
        var labels = workspace.FindControl<UniformGrid>("ThumbnailZoomLabels")!.Children.Cast<TextBlock>().ToArray();
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
            vm.SelectEntry(rows[1]);
            Assert.Equal(56, list.GridIconSize);
            Assert.True(button.IsEffectivelyVisible);
            Assert.False(popup.IsOpen);
            var buttonPoint = button.TranslatePoint(new Rect(button.Bounds.Size).Center, window)!.Value;
            var statusRow = Assert.IsType<Grid>(workspace.FindControl<Border>("StatusBar")!.Child);
            var statusSummary = Assert.IsType<TextBlock>(statusRow.Children[0]);
            var resizeIcon = button.FindDescendantOfType<PathIcon>()!;
            Assert.Equal(buttonPoint.Y, statusSummary.TranslatePoint(new Rect(statusSummary.Bounds.Size).Center, window)!.Value.Y,
                precision: 1);
            Assert.Equal(buttonPoint.Y, resizeIcon.TranslatePoint(new Rect(resizeIcon.Bounds.Size).Center, window)!.Value.Y,
                precision: 1);
            Click(window, buttonPoint);
            Assert.True(popup.IsOpen);
            Assert.Equal(new[] { "32 pt", "56 pt", "96 pt", "128 pt", LocalizationText.Get("file_gallery.title") },
                labels.Select(label => label.Text));
            var firstLabelBaseline = labels[0].TranslatePoint(default, window)!.Value.Y + labels[0].TextLayout.Baseline;
            Assert.All(labels, label =>
            {
                Assert.Equal(statusSummary.FontFamily, label.FontFamily);
                Assert.Equal(statusSummary.LineHeight, label.LineHeight);
                Assert.Equal(firstLabelBaseline, label.TranslatePoint(default, window)!.Value.Y + label.TextLayout.Baseline,
                    precision: 1);
            });
            Assert.True(labels[1].Classes.Contains("active"));
            Assert.True(slider.IsFocused);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Assert.Equal(96, list.GridIconSize);
            Assert.True(labels[2].Classes.Contains("active"));
            Click(window, buttonPoint);
            Assert.False(popup.IsOpen);
            window.MouseWheel(list.TranslatePoint(new Point(40, 40), window)!.Value,
                new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(3, slider.Value);
            Assert.Equal(128, list.GridIconSize);
            window.MouseWheel(list.TranslatePoint(new Point(40, 40), window)!.Value,
                new Vector(0, 1), RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(FileThumbnailSizing.GalleryLevel, slider.Value);
            Assert.True(view.IsGalleryVisible);
            var strip = view.GalleryFilmstrip!;
            Assert.False(otherPane.IsGalleryMode);
            Assert.Same(rows[1], strip.SelectedItem);
            Assert.Equal(rows, strip.Items.Cast<FileSystemEntry>());
            Assert.Null(strip.ContainerFromIndex(999));

            Assert.True(strip.Focus());
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(rows[2], Assert.Single(vm.SelectedEntries));
            vm.Entries.Remove(rows[2]);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(rows[3], strip.SelectedItem);
            Assert.Same(rows[3], Assert.Single(vm.SelectedEntries));

            sortFilter.SetRawEntries(vm.Entries.ToArray());
            vm.SetSort(SortField.Name, ascending: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(vm.Entries, strip.Items.Cast<FileSystemEntry>());
            Assert.Same(rows[3], strip.SelectedItem);
            vm.FileNameFilter = "File 3";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(vm.Entries, strip.Items.Cast<FileSystemEntry>());
            Assert.Same(rows[3], strip.SelectedItem);

            workspace.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(strip.ItemsSource);
            Assert.True(vm.IsGalleryMode);
            workspace.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(rows[3], strip.SelectedItem);

            window.MouseWheel(strip.TranslatePoint(new Point(40, 40), window)!.Value,
                new Vector(0, -1), RawInputModifiers.Control);
            Assert.False(view.IsGalleryVisible);
            Assert.Equal(3, slider.Value);
            Assert.Equal(128, list.GridIconSize);

            Click(window, buttonPoint);
            Assert.True(popup.IsOpen);
            var thumb = slider.FindDescendantOfType<Thumb>()!;
            var rail = slider.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SliderRail");
            var fill = slider.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SliderFill");
            var stops = rail.GetVisualDescendants().OfType<Ellipse>().ToArray();
            Assert.Equal(28, thumb.Bounds.Width);
            Assert.Equal(28, thumb.Bounds.Height);
            Assert.Equal(24, rail.Bounds.Height);
            Assert.Equal(5, stops.Length);
            foreach (var (label, stop) in labels.Zip(stops))
                Assert.Equal(label.TranslatePoint(new Rect(label.Bounds.Size).Center, window)!.Value.X,
                    stop.TranslatePoint(new Rect(stop.Bounds.Size).Center, window)!.Value.X, precision: 1);
            Assert.Equal(Color.Parse("#3778F5"), Assert.IsAssignableFrom<ISolidColorBrush>(fill.Background).Color);
            Assert.True(rail.TranslatePoint(new Point(0, rail.Bounds.Height), window)!.Value.Y < buttonPoint.Y);
            var thumbPoint = thumb.TranslatePoint(new Rect(thumb.Bounds.Size).Center, window)!.Value;
            var fillEnd = fill.TranslatePoint(new Point(fill.Bounds.Width, 0), window)!.Value;
            Assert.Equal(thumbPoint.X, fillEnd.X, precision: 1);
            var lastStop = stops[4].TranslatePoint(new Rect(stops[4].Bounds.Size).Center, window)!.Value;
            var dragPoint = new Point(thumbPoint.X + (lastStop.X - thumbPoint.X) * 0.75, thumbPoint.Y);
            window.MouseDown(thumbPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(dragPoint, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(slider.Value, 3.74, 3.76);
            fillEnd = fill.TranslatePoint(new Point(fill.Bounds.Width, 0), window)!.Value;
            Assert.Equal(dragPoint.X, fillEnd.X, precision: 1);
            Assert.True(labels[4].Classes.Contains("active"));
            Assert.Equal(3, vm.ThumbnailZoomLevel);
            Assert.False(view.IsGalleryVisible);
            window.MouseUp(dragPoint, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(FileThumbnailSizing.GalleryLevel, slider.Value);
            Assert.Equal(rail.Bounds.Width, fill.Bounds.Width);
            Assert.True(popup.IsOpen);
            Assert.True(view.IsGalleryVisible);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(popup.IsOpen);
            Assert.True(button.IsFocused);
            Assert.True(view.IsGalleryVisible);
            Assert.True(workspace.TryHandleEscape());
            Assert.False(view.IsGalleryVisible);
            Click(window, buttonPoint);
            var firstStop = stops[0].TranslatePoint(new Rect(stops[0].Bounds.Size).Center, window)!.Value;
            Click(window, firstStop);
            Assert.Equal(32, list.GridIconSize);
            Assert.True(labels[0].Classes.Contains("active"));
            Click(window, list.TranslatePoint(new Point(40, 40), window)!.Value);
            Assert.False(popup.IsOpen);
            Click(window, buttonPoint);
            Assert.True(popup.IsOpen);
            vm.SetViewMode(ViewMode.List);
            Assert.False(popup.IsOpen);
            Assert.False(button.IsEffectivelyVisible);
            Assert.Equal(FastFileList.RowHeight, list.ItemHeight);
        }
        finally { window.Close(); }
    }

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }
}
