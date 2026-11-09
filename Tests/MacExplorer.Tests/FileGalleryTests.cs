using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
            slider.Value = 2;
            Assert.Equal(96, list.GridIconSize);
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

            Dispatcher.UIThread.RunJobs();
            var thumb = slider.FindDescendantOfType<Thumb>()!;
            var rail = slider.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SliderRail");
            var fill = slider.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SliderFill");
            var valueTip = thumb.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SliderValueTip");
            Assert.Equal(21, thumb.Bounds.Width);
            Assert.Equal(21, thumb.Bounds.Height);
            Assert.Equal(6, rail.Bounds.Height);
            Assert.Equal(Color.Parse("#3778F5"), Assert.IsAssignableFrom<ISolidColorBrush>(fill.Background).Color);
            Assert.False(valueTip.IsVisible);
            Assert.False(slider.ClipToBounds);
            Assert.False(thumb.ClipToBounds);
            var surface = workspace.FindControl<Border>("WorkspaceSurface")!;
            var thumbBottom = thumb.TranslatePoint(new Point(0, thumb.Bounds.Height), surface)!.Value.Y;
            Assert.True(surface.Bounds.Height - thumbBottom >= 8);
            var thumbPoint = thumb.TranslatePoint(new Rect(thumb.Bounds.Size).Center, window)!.Value;
            var endPoint = slider.TranslatePoint(new Point(slider.Bounds.Width - 1, slider.Bounds.Height / 2), window)!.Value;
            window.MouseDown(thumbPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Assert.True(valueTip.IsVisible);
            Assert.Equal("128 pt", slider.Tag);
            window.MouseMove(endPoint, RawInputModifiers.LeftMouseButton);
            Assert.Equal(FileThumbnailSizing.GalleryLevel, slider.Value);
            Assert.False(view.IsGalleryVisible);
            window.MouseUp(endPoint, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(valueTip.IsVisible);
            Assert.True(view.IsGalleryVisible);
            Assert.True(workspace.TryHandleEscape());
            Assert.False(view.IsGalleryVisible);
            vm.SetViewMode(ViewMode.List);
            Assert.False(workspace.FindControl<StackPanel>("ThumbnailZoomHost")!.IsVisible);
            Assert.Equal(FastFileList.RowHeight, list.ItemHeight);
        }
        finally { window.Close(); }
    }
}
