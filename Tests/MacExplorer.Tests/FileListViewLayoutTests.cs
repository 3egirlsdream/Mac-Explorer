using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    [InlineData(false)]
    [InlineData(true)]
    public void FileListContentHitTargetsLeaveWhitespaceForMarquee(bool grid)
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/layout"),
            sortFilter: new SortFilterViewModel { ViewMode = grid ? ViewMode.Grid : ViewMode.List });
        var entry = new FileSystemEntry { Name = "short.txt", FullPath = "/tmp/layout/short.txt" };
        vm.Entries.Add(entry);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 760, Height = 480, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<FastFileList>("FastList")!;
            Assert.Same(entry, list.EntryAt(list.NameBounds(0).Center, contentOnly: true));
            if (grid)
            {
                Assert.Same(entry, list.EntryAt(list.GridIconTargetBounds(0).Center, contentOnly: true));
                Assert.Null(list.EntryAt(new Point(115, 50), contentOnly: true));
            }
            else
            {
                Assert.True(list.NameBounds(0).Width < list.ColumnWidths.Name - 16);
                Assert.Null(list.EntryAt(new Point(list.ListRowRight - 4, 15), contentOnly: true));
                Assert.Same(entry, list.EntryAt(new Point(list.ListRowRight - 4, 15)));
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FastListAndHeaderShareColumnWidthsAfterResizeAndLongNamesStayWithinNameColumn()
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/layout"));
        vm.Entries.Add(new FileSystemEntry { Name = new string('W', 200) + ".txt", FullPath = "/tmp/layout/long.txt" });
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 480, Content = view };
        try
        {
            window.Show();
            foreach (var width in new[] { 900d, 600d })
            {
                window.Width = width;
                Dispatcher.UIThread.RunJobs();
                var list = view.FindControl<FastFileList>("FastList")!;
                var header = view.FindControl<Grid>("ListHeaderGrid")!;
                Assert.Equal(header.ColumnDefinitions[1].Width.Value, list.ColumnWidths.Name);
                Assert.Equal(header.ColumnDefinitions[2].Width.Value, list.ColumnWidths.Modified);
                Assert.Equal(header.ColumnDefinitions[3].Width.Value, list.ColumnWidths.Size);
                Assert.Equal(header.ColumnDefinitions[4].Width.Value, list.ColumnWidths.Type);
                Assert.True(list.NameBounds(0).Right <= 12 + 22 + list.ColumnWidths.Name);
            }
        }
        finally { window.Close(); }
    }
}
