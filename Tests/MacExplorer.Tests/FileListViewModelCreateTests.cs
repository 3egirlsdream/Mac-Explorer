using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public void EmptyAreaDragCreatesMarqueeAndSelectsMultipleRows()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.List };
        using var viewModel = CreateViewModel(fileService, sortFilter: sortFilter);
        for (var index = 0; index < 6; index++)
        {
            viewModel.Entries.Add(new FileSystemEntry
            {
                FullPath = $"/tmp/FKFinderTests/file-{index}.txt",
                Name = $"file-{index}.txt",
                Extension = ".txt",
                IconKey = "file-text"
            });
        }

        var view = new FileListView { DataContext = viewModel };
        var window = new Window { Width = 760, Height = 520, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(new Point(720, 480), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(20, 55), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Border>("SelectionMarquee")!.IsVisible);
        var realized = view.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Classes.Contains("entry-content"))
            .Select(control => $"{control.DataContext?.GetType().Name}:{control.Bounds}")
            .ToArray();
        Assert.True(viewModel.SelectedEntries.Count > 1,
            $"Selected {viewModel.SelectedEntries.Count}; realized: {string.Join(" | ", realized)}");

        window.MouseUp(new Point(20, 55), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.FindControl<Border>("SelectionMarquee")!.IsVisible);
        Assert.True(viewModel.SelectedEntries.Count > 1);
        window.Close();
    }

    [AvaloniaFact]
    public void DragBeginningInBlankPartOfListRowStartsMarquee()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        using var viewModel = CreateViewModel(fileService);
        for (var index = 0; index < 10; index++)
        {
            viewModel.Entries.Add(new FileSystemEntry
            {
                FullPath = $"/tmp/FKFinderTests/list-gap-{index}.txt",
                Name = $"list-gap-{index}.txt",
                Extension = ".txt",
                IconKey = "file-text"
            });
        }

        var view = new FileListView { DataContext = viewModel };
        var rowTemplate = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["ListEntryTemplate"]);
        var firstRow = Assert.IsAssignableFrom<Border>(rowTemplate.Build(viewModel.Entries[0]));
        firstRow.DataContext = viewModel.Entries[0];
        firstRow.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        view.FindControl<Grid>("FileScroll")!.Children.Add(firstRow);
        var window = new Window { Width = 900, Height = 520, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rowOrigin = firstRow.TranslatePoint(default, window)!.Value;
        var rowMidY = rowOrigin.Y + firstRow.Bounds.Height / 2;
        var hitRects = firstRow.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Classes.Contains("list-entry-hit"))
            .Select(control => new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size))
            .ToArray();
        var blankX = Enumerable.Range(1, Math.Max(1, (int)firstRow.Bounds.Width - 2))
            .Select(offset => rowOrigin.X + firstRow.Bounds.Width - offset)
            .First(x => hitRects.All(rect => !rect.Contains(new Point(x, rowMidY))));
        var start = new Point(blankX, rowMidY);
        var end = new Point(8, Math.Min(window.Bounds.Height - 8, rowMidY + 90));

        window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Border>("SelectionMarquee")!.IsVisible);
        Assert.True(viewModel.SelectedEntries.Count > 1,
            $"Selected {viewModel.SelectedEntries.Count} rows from a blank-area marquee");
        window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        window.Close();
    }

    [AvaloniaFact]
    public void ListMarqueeThroughRightHandWhitespaceSelectsRowsByVerticalBand()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.List };
        using var viewModel = CreateViewModel(fileService, sortFilter: sortFilter);
        viewModel.Entries.Add(new FileSystemEntry
        {
            FullPath = "/tmp/FKFinderTests/list-whitespace.txt",
            Name = "list-whitespace.txt",
            Extension = ".txt",
            IconKey = "file-text"
        });

        var view = new FileListView { DataContext = viewModel };
        var rowTemplate = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["ListEntryTemplate"]);
        var row = Assert.IsAssignableFrom<Border>(rowTemplate.Build(viewModel.Entries[0]));
        row.DataContext = viewModel.Entries[0];
        row.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        view.FindControl<Grid>("FileScroll")!.Children.Add(row);
        var window = new Window { Width = 1100, Height = 520, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var entryTargets = view.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Classes.Contains("entry-content"))
            .Select(control => new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size))
            .ToArray();
        Assert.NotEmpty(entryTargets);
        var x = Math.Min(window.Bounds.Width - 8, entryTargets.Max(rect => rect.Right) + 24);
        var rowBounds = new Rect(row.TranslatePoint(default, window)!.Value, row.Bounds.Size);
        var firstY = rowBounds.Top + 1;
        var lastY = rowBounds.Bottom + 1;

        window.MouseDown(new Point(x, firstY), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(x + 4, lastY), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Border>("SelectionMarquee")!.IsVisible);
        Assert.Single(viewModel.SelectedEntries);
        Assert.Same(viewModel.Entries[0], viewModel.SelectedEntries[0]);
        window.MouseUp(new Point(x + 4, lastY), MouseButton.Left, RawInputModifiers.None);
        window.Close();
    }

    [AvaloniaFact]
    public void RightClickingASelectedMarqueeEntryKeepsTheMultiSelection()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.List };
        using var viewModel = CreateViewModel(fileService, sortFilter: sortFilter);
        for (var index = 0; index < 4; index++)
        {
            viewModel.Entries.Add(new FileSystemEntry
            {
                FullPath = $"/tmp/FKFinderTests/context-{index}.txt",
                Name = $"context-{index}.txt",
                Extension = ".txt",
                IconKey = "file-text"
            });
        }

        var view = new FileListView { DataContext = viewModel };
        var rowTemplate = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["ListEntryTemplate"]);
        var fileScroll = view.FindControl<Grid>("FileScroll")!;
        var rows = new List<Border>();
        for (var index = 0; index < viewModel.Entries.Count; index++)
        {
            var row = Assert.IsAssignableFrom<Border>(rowTemplate.Build(viewModel.Entries[index]));
            row.DataContext = viewModel.Entries[index];
            row.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            row.Margin = new Thickness(0, index * 30, 0, 0);
            fileScroll.Children.Add(row);
            rows.Add(row);
        }

        var window = new Window { Width = 900, Height = 360, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var firstOrigin = rows[0].TranslatePoint(default, window)!.Value;
        var lastOrigin = rows[^1].TranslatePoint(default, window)!.Value;
        var blankX = window.Bounds.Width - 16;
        var start = new Point(blankX, firstOrigin.Y + 1);
        var end = new Point(blankX, lastOrigin.Y + rows[^1].Bounds.Height - 1);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        var selectedBeforeContextMenu = viewModel.SelectedEntries
            .Select(entry => entry.FullPath)
            .ToArray();
        Assert.Equal(4, selectedBeforeContextMenu.Length);

        var targetOrigin = rows[1].TranslatePoint(default, window)!.Value;
        // Use the transparent right-hand part of the list row. It is a marquee
        // canvas for left drags, but a right-click there must still target the
        // row and preserve the multi-selection.
        var targetPoint = new Point(
            Math.Max(targetOrigin.X + 1, Math.Min(window.Bounds.Width - 16, targetOrigin.X + rows[1].Bounds.Width - 4)),
            targetOrigin.Y + rows[1].Bounds.Height / 2);
        window.MouseDown(targetPoint, MouseButton.Right, RawInputModifiers.RightMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(selectedBeforeContextMenu, viewModel.SelectedEntries.Select(entry => entry.FullPath));

        // A virtualized ListBox can report a transient single/empty selection
        // after the secondary click. The view must restore the context-menu
        // snapshot instead of feeding that callback back into the view model.
        var list = view.FindControl<ListBox>("FileItemsList")!;
        Assert.NotNull(list.SelectedItems);
        list.SelectedItems!.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(selectedBeforeContextMenu, viewModel.SelectedEntries.Select(entry => entry.FullPath));

        window.MouseUp(targetPoint, MouseButton.Right, RawInputModifiers.None);
        window.Close();
    }

    [AvaloniaFact]
    public void EmptyAreaDragCreatesMarqueeAndSelectsMultipleGridItems()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.Grid };
        using var viewModel = CreateViewModel(fileService, sortFilter: sortFilter);
        for (var index = 0; index < 12; index++)
        {
            viewModel.Entries.Add(new FileSystemEntry
            {
                FullPath = $"/tmp/FKFinderTests/grid-{index}.txt",
                Name = $"grid-{index}.txt",
                Extension = ".txt",
                IconKey = "file-text"
            });
        }

        var view = new FileListView { DataContext = viewModel };
        var window = new Window { Width = 760, Height = 520, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(new Point(730, 490), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(10, 10), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Border>("SelectionMarquee")!.IsVisible);
        Assert.True(viewModel.SelectedEntries.Count > 1);
        window.MouseUp(new Point(10, 10), MouseButton.Left, RawInputModifiers.None);
        window.Close();
    }

    [AvaloniaFact]
    public void SwitchingFromListToGridIgnoresHiddenListHitRegions()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.List };
        using var viewModel = CreateViewModel(fileService, sortFilter: sortFilter);
        for (var index = 0; index < 12; index++)
        {
            viewModel.Entries.Add(new FileSystemEntry
            {
                FullPath = $"/tmp/FKFinderTests/switch-{index}.txt",
                Name = $"switch-{index}.txt",
                Extension = ".txt",
                IconKey = "file-text"
            });
        }

        var view = new FileListView { DataContext = viewModel };
        var window = new Window { Width = 760, Height = 520, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = view.FindControl<ListBox>("FileItemsList")!;
        Assert.True(list.IsVisible);

        sortFilter.ViewMode = ViewMode.Grid;
        Dispatcher.UIThread.RunJobs();
        var grid = view.FindControl<ListBox>("GridViewItems")!;
        Assert.False(list.IsVisible);
        Assert.True(grid.IsVisible);

        // Start in the canvas gap above the first row, then sweep across the
        // third and fourth cards. The hidden list rows occupy the same visual
        // tree but must not add their old row centers to this grid marquee.
        var fileScroll = view.FindControl<Grid>("FileScroll")!;
        var areaOrigin = fileScroll.TranslatePoint(default, window)!.Value;
        var start = new Point(areaOrigin.X + 225, areaOrigin.Y + 1);
        var end = new Point(areaOrigin.X + 455, areaOrigin.Y + 80);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            [viewModel.Entries[2].FullPath, viewModel.Entries[3].FullPath],
            viewModel.SelectedEntries.Select(entry => entry.FullPath));

        window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        window.Close();
    }

    [AvaloniaFact]
    public void DragBeginningInGridCellGapStartsMarqueeInsteadOfItemClick()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.Grid };
        using var viewModel = CreateViewModel(fileService, sortFilter: sortFilter);
        for (var index = 0; index < 12; index++)
        {
            viewModel.Entries.Add(new FileSystemEntry
            {
                FullPath = $"/tmp/FKFinderTests/gap-{index}.txt",
                Name = $"gap-{index}.txt",
                Extension = ".txt",
                IconKey = "file-text"
            });
        }

        var view = new FileListView { DataContext = viewModel };
        var window = new Window { Width = 760, Height = 520, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Each card is 100 points wide inside a 120-point cell. x=115 is
        // the genuine canvas gap between the first and second cards.
        window.MouseDown(new Point(115, 50), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(430, 230), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Border>("SelectionMarquee")!.IsVisible);
        Assert.True(viewModel.SelectedEntries.Count > 1);
        window.MouseUp(new Point(430, 230), MouseButton.Left, RawInputModifiers.None);
        window.Close();
    }

    [AvaloniaFact]
    public void GridMarqueeAroundOneIconSelectsOnlyThatEntry()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var sortFilter = new SortFilterViewModel { ViewMode = ViewMode.Grid };
        using var viewModel = CreateViewModel(fileService, sortFilter: sortFilter);
        viewModel.Entries.Add(new FileSystemEntry
        {
            FullPath = "/tmp/FKFinderTests/grid-precise.txt",
            Name = "grid-precise.txt",
            Extension = ".txt",
            IconKey = "file-text"
        });

        var view = new FileListView { DataContext = viewModel };
        var gridTemplate = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["GridEntryTemplate"]);
        var card = Assert.IsAssignableFrom<Border>(gridTemplate.Build(viewModel.Entries[0]));
        card.DataContext = viewModel.Entries[0];
        card.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        card.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        view.FindControl<Grid>("FileScroll")!.Children.Add(card);
        var window = new Window { Width = 760, Height = 520, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var targetEntry = viewModel.Entries[0];
        var icon = view.GetVisualDescendants().OfType<Control>()
            .Single(control => ReferenceEquals(control.DataContext, targetEntry)
                               && control.Classes.Contains("file-grid-icon-target"));
        var origin = icon.TranslatePoint(default, window)!.Value;
        var start = new Point(origin.X - 3, origin.Y - 3);
        var end = new Point(origin.X + icon.Bounds.Width + 3, origin.Y + icon.Bounds.Height + 3);

        window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Border>("SelectionMarquee")!.IsVisible);
        Assert.Equal([targetEntry.FullPath], viewModel.SelectedEntries.Select(entry => entry.FullPath));
        window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        window.Close();
    }

    [Fact]
    public async Task CreateNewFileAsync_ReloadsDirectoryThenSelectsCreatedEntryAndRequestsRename()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var viewModel = CreateViewModel(fileService);
        var renamedEntries = new List<FileSystemEntry>();
        viewModel.RenameRequested += renamedEntries.Add;
        viewModel.Entries.Add(new FileSystemEntry
        {
            FullPath = "/tmp/FKFinderTests/Existing.txt",
            Name = "Existing.txt",
            Extension = ".txt",
            IsDirectory = false,
            IconKey = "file-text"
        });

        await viewModel.CreateNewFileAsync(".txt");

        var created = Assert.Single(viewModel.Entries, e => e.Name == "未命名.txt");
        Assert.Same(created, Assert.Single(viewModel.SelectedEntries));
        Assert.Same(created, Assert.Single(renamedEntries));
        Assert.Equal(1, fileService.EnumerateDirectoryCallCount);
    }

    [Fact]
    public async Task ConfirmDeleteSelectedAsync_ReloadsDirectoryAfterDelete()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var viewModel = CreateViewModel(fileService);
        var deleted = new FileSystemEntry
        {
            FullPath = "/tmp/FKFinderTests/DeleteMe.txt",
            Name = "DeleteMe.txt",
            Extension = ".txt",
            IsDirectory = false,
            IconKey = "file-text"
        };
        var survivor = new FileSystemEntry
        {
            FullPath = "/tmp/FKFinderTests/KeepMe.txt",
            Name = "KeepMe.txt",
            Extension = ".txt",
            IsDirectory = false,
            IconKey = "file-text"
        };
        fileService.Seed(deleted);
        fileService.Seed(survivor);
        viewModel.Entries.Add(deleted);
        viewModel.Entries.Add(survivor);
        viewModel.SelectedEntries.Add(deleted);

        await viewModel.ConfirmDeleteSelectedAsync();

        Assert.DoesNotContain(viewModel.Entries, e => e.FullPath == deleted.FullPath);
        Assert.Equal(survivor.FullPath, Assert.Single(viewModel.Entries).FullPath);
        Assert.Empty(viewModel.SelectedEntries);
        Assert.Equal(1, fileService.EnumerateDirectoryCallCount);
    }

    [Fact]
    public async Task RenameEntryAsync_ReloadsDirectoryAndReselectsRenamedEntry()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var notifier = new FakeDirectoryChangeNotifier();
        var viewModel = CreateViewModel(fileService, notifier);
        var original = new FileSystemEntry
        {
            FullPath = "/tmp/FKFinderTests/Before.txt",
            Name = "Before.txt",
            Extension = ".txt",
            IsDirectory = false,
            IconKey = "file-text"
        };
        fileService.Seed(original);
        viewModel.Entries.Add(original);
        viewModel.SelectedEntries.Add(original);
        var entries = viewModel.Entries;

        var renamed = await viewModel.RenameEntryAsync(original, "After.txt");

        Assert.True(renamed);
        Assert.NotSame(entries, viewModel.Entries);
        var visible = Assert.Single(viewModel.Entries);
        Assert.Equal("After.txt", visible.Name);
        Assert.Equal("/tmp/FKFinderTests/After.txt", visible.FullPath);
        Assert.Same(visible, Assert.Single(viewModel.SelectedEntries));
        Assert.Equal(1, fileService.EnumerateDirectoryCallCount);
        Assert.Same(viewModel, notifier.ExcludedViewModel);
    }

    [Fact]
    public async Task RefreshAsync_ReplacesCollectionWithFreshEntries()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var viewModel = CreateViewModel(fileService);
        for (var i = 0; i < 300; i++)
        {
            var entry = new FileSystemEntry
            {
                FullPath = $"/tmp/FKFinderTests/file-{i:D3}.txt",
                Name = $"file-{i:D3}.txt",
                Extension = ".txt",
                IsDirectory = false,
                IconKey = "file-text"
            };
            fileService.Seed(entry);
            viewModel.Entries.Add(entry);
        }

        var entries = viewModel.Entries;
        var collectionChanges = 0;
        entries.CollectionChanged += (_, _) => collectionChanges++;

        await viewModel.RefreshAsync();

        Assert.NotSame(entries, viewModel.Entries);
        Assert.Equal(300, viewModel.Entries.Count);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(1, fileService.EnumerateDirectoryCallCount);
    }

    [Fact]
    public async Task NavigateToAsync_KeepsOverlayHiddenUntilNewDirectoryIsReady()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fkfinder-navigation-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "source");
        var targetPath = Path.Combine(root, "target");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(targetPath);
        try
        {
            var fileService = new FakeFileService(sourcePath);
            var viewModel = CreateViewModel(fileService);
            var oldEntry = new FileSystemEntry
            {
                FullPath = Path.Combine(sourcePath, "old.txt"),
                Name = "old.txt",
                Extension = ".txt"
            };
            var newEntry = new FileSystemEntry
            {
                FullPath = Path.Combine(targetPath, "new.txt"),
                Name = "new.txt",
                Extension = ".txt"
            };
            viewModel.Entries.Add(oldEntry);
            fileService.Seed(newEntry);
            var overlayBecameVisible = false;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FileListViewModel.IsLoading) && viewModel.IsLoading)
                    overlayBecameVisible = true;
            };

            await viewModel.NavigateToAsync(targetPath);

            Assert.False(overlayBecameVisible);
            Assert.False(viewModel.IsLoading);
            Assert.Equal(targetPath, viewModel.CurrentPath);
            Assert.Equal(newEntry.FullPath, Assert.Single(viewModel.Entries).FullPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CurrentLocationTitleFollowsDirectoryRootAndHomeNavigation()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var navigation = new NavigationViewModel(fileService)
        {
            CurrentPath = "/tmp/FKFinderTests/Documents",
            IsHomePage = false
        };
        navigation.UpdateBreadcrumbs();
        using var viewModel = CreateViewModel(fileService, navigation: navigation);

        Assert.Equal("Documents", viewModel.CurrentLocationTitle);

        navigation.CurrentPath = "/";
        navigation.UpdateBreadcrumbs();
        Assert.Equal("/", viewModel.CurrentLocationTitle);

        navigation.GoHome();
        Assert.Equal("首页", viewModel.CurrentLocationTitle);
    }

    [Fact]
    public void CurrentLocationTitleUsesLastSpecialViewBreadcrumbAndNotifiesBindings()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        var navigation = new NavigationViewModel(fileService);
        using var viewModel = CreateViewModel(fileService, navigation: navigation);
        var titleChanged = false;
        viewModel.PropertyChanged += (_, args) =>
            titleChanged |= args.PropertyName == nameof(FileListViewModel.CurrentLocationTitle);

        navigation.UpdateBreadcrumbsForAi("照片", "ai://photos", "最近项目");

        Assert.Equal("最近项目", viewModel.CurrentLocationTitle);
        Assert.True(titleChanged);
    }

    [Fact]
    public void TabsSwitchIndependentFileListInstancesAndSelectAdjacentTabOnClose()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        using var first = CreateViewModel(fileService);
        using var second = CreateViewModel(fileService);
        var window = new MainWindowViewModel(first);

        var firstTab = Assert.Single(window.Tabs);
        var secondTab = window.AddTab(second, select: true);

        Assert.Same(secondTab, window.SelectedTab);
        Assert.Same(second, window.FileList);
        Assert.True(firstTab.CanClose);
        Assert.True(secondTab.CanClose);

        Assert.True(window.RemoveTab(secondTab));
        secondTab.Dispose();

        Assert.Same(firstTab, window.SelectedTab);
        Assert.Same(first, window.FileList);
        Assert.False(firstTab.CanClose);
        Assert.False(window.RemoveTab(firstTab));
        firstTab.Dispose();
    }

    [Fact]
    public void RelativeTabSelectionWrapsInBothDirections()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        using var first = CreateViewModel(fileService);
        using var second = CreateViewModel(fileService);
        var window = new MainWindowViewModel(first);
        var firstTab = window.SelectedTab!;
        var secondTab = window.AddTab(second, select: true);

        Assert.Same(firstTab, window.SelectRelativeTab(1));
        Assert.Same(secondTab, window.SelectRelativeTab(-1));

        firstTab.Dispose();
        secondTab.Dispose();
    }

    [Fact]
    public void TwelvePaneLayoutsExposeTheExpectedOneToFourVisiblePanes()
    {
        var layouts = Enum.GetValues<PaneLayout>();
        Assert.Equal(12, layouts.Length);
        Assert.Equal(1, MainWindowViewModel.GetPaneCount(PaneLayout.Single));
        Assert.Equal(2, MainWindowViewModel.GetPaneCount(PaneLayout.TwoColumns));
        Assert.Equal(2, MainWindowViewModel.GetPaneCount(PaneLayout.TwoRows));
        Assert.All(layouts.Where(layout => layout.ToString().StartsWith("Three", StringComparison.Ordinal)
                                           || layout is PaneLayout.MainLeftTwoRowsRight
                                               or PaneLayout.MainRightTwoRowsLeft),
            layout => Assert.Equal(3, MainWindowViewModel.GetPaneCount(layout)));
        Assert.All(layouts.Where(layout => MainWindowViewModel.GetPaneCount(layout) == 4),
            layout => Assert.Equal(4, MainWindowViewModel.GetPaneCount(layout)));
    }

    [Fact]
    public void MultiPaneLayoutKeepsActiveTabVisibleAndRestoresSinglePane()
    {
        var fileService = new FakeFileService("/tmp/FKFinderTests");
        using var first = CreateViewModel(fileService);
        using var second = CreateViewModel(fileService);
        using var third = CreateViewModel(fileService);
        using var fourth = CreateViewModel(fileService);
        var window = new MainWindowViewModel(first);
        var tabs = new[]
        {
            window.SelectedTab!,
            window.AddTab(second, select: false),
            window.AddTab(third, select: false),
            window.AddTab(fourth, select: false)
        };

        window.SetPaneLayout(PaneLayout.FourGrid);
        Assert.Equal(4, window.VisiblePanes.Count);
        Assert.Equal(tabs, window.VisiblePanes);

        window.SelectedTab = tabs[3];
        Assert.Contains(tabs[3], window.VisiblePanes);
        Assert.True(tabs[3].IsActive);

        window.SetPaneLayout(PaneLayout.Single);
        Assert.Single(window.VisiblePanes);
        Assert.Same(tabs[3], window.VisiblePanes[0]);

        foreach (var tab in tabs)
            tab.Dispose();
    }

    private static FileListViewModel CreateViewModel(
        FakeFileService fileService,
        IDirectoryChangeNotifier? directoryChangeNotifier = null,
        NavigationViewModel? navigation = null,
        SortFilterViewModel? sortFilter = null)
    {
        navigation ??= new NavigationViewModel(fileService)
        {
            CurrentPath = fileService.HomeDirectory,
            IsHomePage = false
        };
        var index = new FakeFileIndex();
        var writer = new FakeFileIndexWriter();
        var fileOps = new FileOpsViewModel(
            fileService: fileService,
            directoryChangeNotifier: directoryChangeNotifier);

        return new FileListViewModel(
            navigation,
            fileOps,
            new SearchViewModel(),
            new ArchiveViewModel(fileService: fileService),
            new AiViewModel(fileIndex: index),
            new CollectionViewModel(fileIndex: index, fileService: fileService),
            sortFilter ?? new SortFilterViewModel(),
            fileService,
            index,
            writer,
            new IndexConfiguration(),
            directoryChangeNotifier: directoryChangeNotifier);
    }

    private sealed class FakeFileService(string homeDirectory) : IFileService
    {
        private readonly Dictionary<string, FileSystemEntry> _entries = new(StringComparer.Ordinal);

        public int EnumerateDirectoryCallCount { get; private set; }
        public string HomeDirectory { get; } = homeDirectory;
        public string RootDirectory => "/";
        public string TrashDirectory => Path.Combine(HomeDirectory, ".Trash");

        public void Seed(FileSystemEntry entry) => _entries[entry.FullPath] = entry;

        public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FileSystemEntry>>(_entries.Values.ToList());

        public async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(
            string path,
            int batchSize = 256,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            EnumerateDirectoryCallCount++;
            await Task.Yield();
            var entries = _entries.Values
                .Where(entry => string.Equals(Path.GetDirectoryName(entry.FullPath), path, StringComparison.Ordinal))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(Clone)
                .ToArray();
            for (var i = 0; i < entries.Length; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entries.Skip(i).Take(batchSize).ToArray();
            }
        }

        public Task<FileSystemEntry?> GetEntryAsync(string path)
            => Task.FromResult(_entries.GetValueOrDefault(path));

        public Task<bool> ExistsAsync(string path)
            => Task.FromResult(_entries.ContainsKey(path));

        public Task<string> CreateFolderAsync(string parentPath, string name)
        {
            var fullPath = Path.Combine(parentPath, name);
            _entries[fullPath] = new FileSystemEntry
            {
                FullPath = fullPath,
                Name = name,
                IsDirectory = true,
                IconKey = "folder"
            };
            return Task.FromResult(fullPath);
        }

        public Task<string> CreateFileAsync(string parentPath, string name)
            => CreateFileWithContentAsync(parentPath, name, []);

        public Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content)
        {
            var fullPath = Path.Combine(parentPath, name);
            _entries[fullPath] = new FileSystemEntry
            {
                FullPath = fullPath,
                Name = name,
                Extension = Path.GetExtension(name),
                IsDirectory = false,
                IconKey = "file-generic"
            };
            return Task.FromResult(fullPath);
        }

        public Task DeleteAsync(string path, bool moveToTrash = true)
        {
            _entries.Remove(path);
            return Task.CompletedTask;
        }
        public Task RenameAsync(string path, string newName)
        {
            if (_entries.Remove(path, out var entry))
            {
                var newPath = Path.Combine(Path.GetDirectoryName(path) ?? "", newName);
                _entries[newPath] = new FileSystemEntry
                {
                    FullPath = newPath,
                    Name = newName,
                    Extension = Path.GetExtension(newName),
                    IsDirectory = entry.IsDirectory,
                    Size = entry.Size,
                    LastModified = entry.LastModified,
                    Created = entry.Created,
                    IconKey = entry.IconKey
                };
            }
            return Task.CompletedTask;
        }
        public Task MoveAsync(string sourcePath, string destinationPath, bool overwrite = false) => Task.CompletedTask;
        public Task CopyAsync(string sourcePath, string destinationDirectory) => Task.CompletedTask;
        public string GetParentPath(string path) => Path.GetDirectoryName(path) ?? "";
        public string CombinePath(string directory, string name) => Path.Combine(directory, name);
        public IReadOnlyList<string> GetVolumes() => [];
        public Task DeletePermanentlyAsync(string path) => Task.CompletedTask;
        public Task EmptyTrashAsync() => Task.CompletedTask;
        public Task ResolveAppIconsAsync(IEnumerable<FileSystemEntry> entries, Action? onBatchResolved = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsCrossVolume(string sourcePath, string destinationPath) => false;
        public Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory, IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;

        private static FileSystemEntry Clone(FileSystemEntry entry) => new()
        {
            FullPath = entry.FullPath,
            Name = entry.Name,
            IsDirectory = entry.IsDirectory,
            Size = entry.Size,
            LastModified = entry.LastModified,
            Created = entry.Created,
            Extension = entry.Extension,
            IsHidden = entry.IsHidden,
            IsSymbolicLink = entry.IsSymbolicLink,
            IsReadable = entry.IsReadable,
            IsWritable = entry.IsWritable,
            IconKey = entry.IconKey,
            IsVirtual = entry.IsVirtual,
            VirtualFolderType = entry.VirtualFolderType,
            VirtualFolderKey = entry.VirtualFolderKey,
            VirtualItemCount = entry.VirtualItemCount
        };
    }

    private sealed class FakeFileIndex : IFileIndex
    {
        public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string parentPath)
            => Task.FromResult<IReadOnlyList<FileSystemEntry>>([]);

        public Task<FileSystemEntry?> GetEntryAsync(string path)
            => Task.FromResult<FileSystemEntry?>(null);

        public Task<IReadOnlyList<FileSystemEntry>> SearchByNameAsync(string pattern, int limit = 100)
            => Task.FromResult<IReadOnlyList<FileSystemEntry>>([]);

        public Task<bool> IsDirectoryFreshAsync(string path, TimeSpan freshnessThreshold)
            => Task.FromResult(false);
    }

    private sealed class FakeFileIndexWriter : IFileIndexWriter
    {
        public Task UpdateDirectoryAsync(string directoryPath, IReadOnlyList<FileSystemEntry> entries, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateDirectoriesAsync(IEnumerable<string> directoryPaths) => Task.CompletedTask;
        public Task RenameEntryAsync(string oldPath, string newPath, string newName) => Task.CompletedTask;
        public Task RemoveEntryAsync(string path) => Task.CompletedTask;
        public Task AddEntryAsync(FileSystemEntry entry) => Task.CompletedTask;
    }

    private sealed class FakeDirectoryChangeNotifier : IDirectoryChangeNotifier
    {
        public FileListViewModel? ExcludedViewModel { get; private set; }

        public void NotifyChanged(string[] directoryPaths, FileListViewModel? excludeVm = null)
            => ExcludedViewModel = excludeVm;

        public void SuppressRefresh(string[] directoryPaths, TimeSpan duration)
        {
        }

        public void Subscribe(FileListViewModel vm)
        {
        }

        public void Unsubscribe(FileListViewModel vm)
        {
        }
    }
}
