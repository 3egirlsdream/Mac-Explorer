using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MacExplorer.Assets;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileListStartupSort")]
public sealed class FileListStartupSortTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task FirstDirectoryLoadUsesRestoredSortInToolbarHeaderAndRows()
    {
        var settings = new StartupSettings();
        settings.Set("ViewMode", ViewMode.List);
        settings.Set("SortField", SortField.Modified);
        settings.Set("GroupField", GroupField.None);
        settings.Writes.Clear();
        var files = new FakeFileService("/tmp/startup-sort");
        var today = new DateTime(2026, 10, 12);
        foreach (var (name, date) in new[] { ("Alpha.txt", today.AddDays(-2)), ("Zulu.txt", today), ("Middle.txt", today.AddDays(-1)) })
            files.Seed(new FileSystemEntry { Name = name, FullPath = files.HomeDirectory + "/" + name, LastModified = date });
        using var vm = CreateViewModel(files, sortFilter: new SortFilterViewModel(settings), settingsService: settings);
        using var tab = new ExplorerTabViewModel(vm) { IsActive = true };
        using var workspace = new ExplorerWorkspaceView { DataContext = tab };
        var window = new Window { Content = workspace, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            await vm.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            var list = workspace.FileListView;
            var toolbar = workspace.FindControl<FinderToolbar>("ToolbarControl")!;
            Assert.Equal(SortField.Modified, vm.SortField);
            Assert.False(vm.SortAscending);
            Assert.Equal(new[] { "Zulu.txt", "Middle.txt", "Alpha.txt" }, vm.Entries.Select(entry => entry.Name));
            Assert.Equal(vm.Entries, list.FindControl<FastFileList>("FastList")!.Rows);
            Assert.Contains("sorted", list.FindControl<Button>("ModifiedSortButton")!.Classes);
            Assert.DoesNotContain("sorted", list.FindControl<Button>("NameSortButton")!.Classes);
            Assert.Same(FileListHeaderIcons.SortDown, list.FindControl<PathIcon>("ModifiedSortIcon")!.Data);
            var popup = toolbar.FindControl<Popup>("SortDropdown")!;
            var selected = Assert.Single(popup.Child!.GetLogicalDescendants().OfType<Button>(), button => button.Classes.Contains("sort-field") && button.Classes.Contains("selected"));
            Assert.Equal("Modified", selected.Tag);
            Assert.Empty(settings.Writes);

            // Reattachment must restore the header even when no sort property changes.
            window.Content = null;
            list.FindControl<Button>("ModifiedSortButton")!.Classes.Remove("sorted");
            list.FindControl<PathIcon>("ModifiedSortIcon")!.Data = FileListHeaderIcons.Sort;
            window.Content = workspace;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("sorted", list.FindControl<Button>("ModifiedSortButton")!.Classes);
            Assert.Same(FileListHeaderIcons.SortDown, list.FindControl<PathIcon>("ModifiedSortIcon")!.Data);
            Assert.Equal(new[] { "Zulu.txt", "Middle.txt", "Alpha.txt" }, vm.Entries.Select(entry => entry.Name));
            Assert.Empty(settings.Writes);
        }
        finally { window.Close(); }
    }
}
