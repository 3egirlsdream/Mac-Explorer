using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FastRenameClosesFilenameTooltipUntilEditingEnds(bool grid, bool grouped)
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/FastListTests"),
            sortFilter: new SortFilterViewModel { ViewMode = grid ? ViewMode.Grid : ViewMode.List });
        var entry = FastFileListTests.Entry(0);
        vm.Entries.Add(entry);
        if (grouped)
        {
            vm.GroupField = GroupField.Type;
            vm.Groups.Add(new FileGroup { Name = "文档", Entries = [entry] });
        }
        vm.UseFastFileList = true;
        var view = new FileListView { DataContext = vm };
        var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var origin = list.TranslatePoint(default, window)!.Value;
            var namePoint = origin + (Vector)list.NameBounds(0).Center;
            window.MouseMove(namePoint);
            Assert.Equal(entry.DisplayName, ToolTip.GetTip(list));
            ToolTip.SetIsOpen(list, true);
            Assert.True(ToolTip.GetIsOpen(list));

            vm.SelectEntry(entry);
            vm.RequestRename(entry);
            Dispatcher.UIThread.RunJobs();
            var overlay = view.FindControl<Canvas>("FastRenameOverlay")!;
            Assert.IsType<TextBox>(Assert.Single(overlay.Children));
            Assert.False(ToolTip.GetIsOpen(list));
            Assert.Null(ToolTip.GetTip(list));

            var iconPoint = origin + (Vector)(grid ? list.GridIconTargetBounds(0).Center
                : new Point(24, list.RowBounds(0).Center.Y));
            window.MouseMove(iconPoint);
            Assert.Null(ToolTip.GetTip(list));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(overlay.Children);
            Assert.Null(list.EditingPath);
            window.MouseMove(namePoint);
            Assert.Equal(entry.DisplayName, ToolTip.GetTip(list));
        }
        finally { window.Close(); }
    }
}

public sealed class FastFileListRenameTests
{
    [AvaloniaFact]
    public void GridRenameHidesOriginalNameAndSelectionDecorationAndRestoresThemAfterwards()
    {
        var entry = FastFileListTests.Entry(0);
        entry.IsSelected = true;
        var list = new FastFileList { IsGrid = true, Selected = Brushes.Blue };
        list.SetRows([entry]);
        list.Measure(new Size(240, 240));
        list.Arrange(new Rect(0, 0, 240, 240));
        var original = RenderLeaves();
        Assert.Equal(2, SelectionCount(original));
        Assert.NotEmpty(original.OfType<GlyphRunDrawing>());

        list.EditingPath = entry.FullPath;
        var editing = RenderLeaves();
        Assert.Equal(1, SelectionCount(editing));
        Assert.Empty(editing.OfType<GlyphRunDrawing>());

        list.EditingPath = null;
        var restored = RenderLeaves();
        Assert.Equal(2, SelectionCount(restored));
        Assert.NotEmpty(restored.OfType<GlyphRunDrawing>());

        static int SelectionCount(IEnumerable<Drawing> drawings) => drawings.OfType<GeometryDrawing>()
            .Count(drawing => Equals(drawing.Brush, Brushes.Blue));

        Drawing[] RenderLeaves()
        {
            var group = new DrawingGroup();
            using (var context = group.Open()) list.Render(context);
            return Leaves(group).ToArray();
        }

        static IEnumerable<Drawing> Leaves(Drawing drawing) => drawing is DrawingGroup group
            ? group.Children.SelectMany(Leaves) : [drawing];
    }
}
