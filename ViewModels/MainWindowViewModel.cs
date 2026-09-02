using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MacExplorer.ViewModels;

public enum PaneLayout
{
    Single,
    TwoColumns,
    TwoRows,
    ThreeColumns,
    ThreeRows,
    MainLeftTwoRowsRight,
    MainRightTwoRowsLeft,
    FourGrid,
    FourColumns,
    FourRows,
    MainLeftThreeRowsRight,
    MainRightThreeRowsLeft
}

public sealed partial class ExplorerTabViewModel : ObservableObject, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public FileListViewModel FileList { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _canClose;

    [ObservableProperty]
    private bool _isActive;

    public ExplorerTabViewModel(FileListViewModel fileList)
    {
        FileList = fileList;
        _title = GetTitle(fileList);
        fileList.PropertyChanged += OnFileListPropertyChanged;
    }

    private void OnFileListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.CurrentLocationTitle))
            Title = GetTitle(FileList);
    }

    private static string GetTitle(FileListViewModel fileList)
    {
        var title = fileList.CurrentLocationTitle;
        return string.IsNullOrWhiteSpace(title) ? "首页" : title;
    }

    public void Dispose()
    {
        FileList.PropertyChanged -= OnFileListPropertyChanged;
    }
}

public partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<ExplorerTabViewModel> Tabs { get; } = [];
    public ObservableCollection<ExplorerTabViewModel> VisiblePanes { get; } = [];

    [ObservableProperty]
    private FileListViewModel _fileList = null!;

    [ObservableProperty]
    private ExplorerTabViewModel? _selectedTab;

    [ObservableProperty]
    private PaneLayout _paneLayout = PaneLayout.Single;

    public int PaneCount => GetPaneCount(PaneLayout);
    public bool IsMultiPane => PaneCount > 1;

    public MainWindowViewModel()
    {
    }

    public MainWindowViewModel(FileListViewModel fileList)
    {
        _fileList = fileList;
        AddTab(fileList, select: true);
    }

    public ExplorerTabViewModel AddTab(FileListViewModel fileList, bool select)
    {
        var tab = new ExplorerTabViewModel(fileList);
        Tabs.Add(tab);
        if (VisiblePanes.Count < PaneCount)
            VisiblePanes.Add(tab);
        UpdateCanClose();
        if (select)
            SelectedTab = tab;
        return tab;
    }

    public ExplorerTabViewModel? SelectRelativeTab(int offset)
    {
        if (Tabs.Count < 2 || SelectedTab == null)
            return SelectedTab;

        var currentIndex = Tabs.IndexOf(SelectedTab);
        if (currentIndex < 0)
            currentIndex = 0;
        var nextIndex = (currentIndex + offset) % Tabs.Count;
        if (nextIndex < 0)
            nextIndex += Tabs.Count;
        SelectedTab = Tabs[nextIndex];
        return SelectedTab;
    }

    public bool RemoveTab(ExplorerTabViewModel tab)
    {
        if (Tabs.Count <= 1)
            return false;

        var removedIndex = Tabs.IndexOf(tab);
        if (removedIndex < 0)
            return false;

        var wasSelected = ReferenceEquals(SelectedTab, tab);
        Tabs.RemoveAt(removedIndex);
        VisiblePanes.Remove(tab);
        if (wasSelected)
            SelectedTab = Tabs[Math.Min(removedIndex, Tabs.Count - 1)];
        EnsureVisiblePanes();
        UpdateCanClose();
        return true;
    }

    partial void OnSelectedTabChanged(ExplorerTabViewModel? value)
    {
        foreach (var tab in Tabs)
            tab.IsActive = ReferenceEquals(tab, value);

        if (value == null)
            return;

        EnsureSelectedTabVisible(value);
        if (!ReferenceEquals(FileList, value.FileList))
            FileList = value.FileList;
    }

    public void SetPaneLayout(PaneLayout layout)
    {
        if (PaneLayout != layout)
            PaneLayout = layout;
        OnPropertyChanged(nameof(PaneCount));
        OnPropertyChanged(nameof(IsMultiPane));
        EnsureVisiblePanes();
    }

    private void EnsureVisiblePanes()
    {
        var desired = PaneCount;
        while (VisiblePanes.Count > desired)
            VisiblePanes.RemoveAt(VisiblePanes.Count - 1);

        foreach (var tab in Tabs)
        {
            if (VisiblePanes.Count >= desired)
                break;
            if (!VisiblePanes.Contains(tab))
                VisiblePanes.Add(tab);
        }

        if (SelectedTab != null)
            EnsureSelectedTabVisible(SelectedTab);
    }

    private void EnsureSelectedTabVisible(ExplorerTabViewModel selected)
    {
        if (VisiblePanes.Contains(selected))
            return;

        if (VisiblePanes.Count == 0)
            VisiblePanes.Add(selected);
        else
            VisiblePanes[VisiblePanes.Count - 1] = selected;
    }

    public static int GetPaneCount(PaneLayout layout) => layout switch
    {
        PaneLayout.Single => 1,
        PaneLayout.TwoColumns or PaneLayout.TwoRows => 2,
        PaneLayout.ThreeColumns or PaneLayout.ThreeRows
            or PaneLayout.MainLeftTwoRowsRight or PaneLayout.MainRightTwoRowsLeft => 3,
        _ => 4
    };

    private void UpdateCanClose()
    {
        var canClose = Tabs.Count > 1;
        foreach (var tab in Tabs)
            tab.CanClose = canClose;
    }
}
