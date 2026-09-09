using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Assets;
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
    public void HeaderFiltersSupportMultipleChecksFilenameAndKeyboardWithoutSorting(bool fast)
    {
        using var host = new HeaderFilterHost(fast);
        host.Click("TypeFilterButton");
        Assert.True(host.Popup.IsOpen);
        host.Check(".txt");
        Assert.Equal(["Alpha.txt", "Zulu.txt"], host.Vm.Entries.Select(e => e.Name));
        host.Check(".png");
        Assert.Equal(3, host.Vm.Entries.Count);
        Assert.True(host.Popup.IsOpen);
        Assert.Equal(SortField.Name, host.Vm.SortField);
        Assert.True(host.Vm.SortAscending);

        host.Popup.IsOpen = false;
        host.Click("NameFilterButton");
        host.Vm.FileNameFilter = "Alpha";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, host.Vm.Entries.Count);
        host.Check("U–Z");
        Assert.Empty(host.Vm.Entries);
        Assert.True(host.View.FindControl<Button>("ClearEmptyFiltersButton")!.IsVisible);
        Assert.Empty(host.Vm.SelectedEntries);
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(host.Popup.IsOpen);
        host.Vm.ClearColumnFilter(SortField.Name);
        Assert.Equal(3, host.Vm.Entries.Count);
        Assert.True(host.Vm.HasFileListFilters);
        host.Vm.ClearFileListFilters();
        Assert.Equal(4, host.Vm.Entries.Count);
    }

    [AvaloniaFact]
    public void HeaderSortButtonsToggleEveryColumnAndStaySeparateFromDropdowns()
    {
        using var host = new HeaderFilterHost();
        foreach (var field in Enum.GetValues<SortField>())
        {
            host.Vm.SetSort(field, false);
            host.Click(field + "SortButton");
            Assert.Equal(field, host.Vm.SortField);
            Assert.True(host.Vm.SortAscending);
            Assert.Same(FileListHeaderIcons.SortUp, host.View.FindControl<PathIcon>(field + "SortIcon")!.Data);
            host.Click(field + "SortButton");
            Assert.False(host.Vm.SortAscending);
            Assert.Same(FileListHeaderIcons.SortDown, host.View.FindControl<PathIcon>(field + "SortIcon")!.Data);
            Assert.False(host.Popup.IsOpen);
        }
    }

    [AvaloniaFact]
    public void HeaderRangeKeyboardNavigationStaysInsideTheOpenFilter()
    {
        using var host = new HeaderFilterHost();
        host.Click("NameFilterButton");
        var options = host.View.FindControl<StackPanel>("ColumnFilterOptions")!.Children.OfType<CheckBox>().ToArray();
        Assert.True(options[0].IsFocused);
        host.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        host.Window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(host.Popup.IsOpen);
        Assert.True(options[1].IsFocused);
    }

    [AvaloniaTheory]
    [InlineData(620, false)]
    [InlineData(900, true)]
    public void HeaderLabelsIconsAndDropdownTargetsFitTheirColumns(double width, bool dark)
    {
        using var host = new HeaderFilterHost(width: width, dark: dark);
        foreach (var field in Enum.GetValues<SortField>())
        {
            var sort = host.View.FindControl<Button>(field + "SortButton")!;
            var filter = host.View.FindControl<Button>(field + "FilterButton")!;
            var icon = host.View.FindControl<PathIcon>(field + "SortIcon")!;
            var iconRight = icon.TranslatePoint(new Point(icon.Bounds.Width, 0), host.Window)!.Value.X;
            var filterLeft = filter.TranslatePoint(default, host.Window)!.Value.X;
            Assert.True(iconRight <= filterLeft, $"{field}: icon {iconRight}, filter {filterLeft}");
            Assert.NotEmpty(icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
            Assert.True(sort.Bounds.Height >= 28);
            Assert.Equal(24, filter.Bounds.Width);
            host.Click(field + "FilterButton");
            Assert.True(host.Popup.IsOpen);
            Assert.NotNull(((Border)host.Popup.Child!).Background);
            host.Popup.IsOpen = false;
        }
    }

    [AvaloniaFact]
    public void FilteringDropsHiddenSelectionsAndResetsOnNavigation()
    {
        using var host = new HeaderFilterHost();
        host.Vm.SelectEntry(host.Vm.Entries.Single(entry => entry.Name == "Zulu.txt"));
        host.Vm.FileNameFilter = "Alpha";
        Assert.Empty(host.Vm.SelectedEntries);
        host.Vm.ClearFileListFilters();
        Assert.Equal(4, host.Vm.Entries.Count);
        host.Vm.SetColumnFilter(SortField.Type, ".txt", true);
        host.Navigation.CurrentPath = "/test/next";
        Assert.False(host.Vm.HasFileListFilters);
    }

    private sealed class HeaderFilterHost : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public FileListViewModel Vm { get; }
        public NavigationViewModel Navigation { get; }
        public FileListView View { get; }
        public Window Window { get; }
        public Popup Popup => View.FindControl<Popup>("ColumnFilterPopup")!;

        public HeaderFilterHost(bool fast = true, double width = 900, bool dark = false)
        {
            Application.Current!.Styles.Insert(0, _theme);
            var fileService = new FakeFileService("/test");
            var sort = new SortFilterViewModel();
            Navigation = new NavigationViewModel(fileService) { CurrentPath = "/test", IsHomePage = false };
            Vm = CreateViewModel(fileService, navigation: Navigation, sortFilter: sort);
            Vm.UseFastFileList = fast;
            sort.SetRawEntries([FileColumnFilterTests.File("Alpha.txt", 2048), FileColumnFilterTests.File("Alpha.png", 4000000),
                FileColumnFilterTests.File("Zulu.txt", 1), new FileSystemEntry { Name = "Folder", FullPath = "/test/Folder", IsDirectory = true }]);
            sort.ApplySortAndGroup(entries => Vm.Entries = entries);
            View = new FileListView { DataContext = Vm };
            Window = new Window { Width = width, Height = 560, Content = View,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            Window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
            Window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Click(string name)
        {
            var button = View.FindControl<Button>(name)!;
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(point);
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Check(string key)
        {
            var check = View.FindControl<StackPanel>("ColumnFilterOptions")!.Children.OfType<CheckBox>().Single(c => (string)c.Tag! == key);
            check.Focus();
            Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(check.IsChecked);
        }

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            Application.Current!.Styles.Remove(_theme);
        }
    }
}
