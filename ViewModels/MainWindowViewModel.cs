using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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

internal readonly record struct PaneSlotPlacement(
    int Row,
    int Column,
    int RowSpan = 1,
    int ColumnSpan = 1);

internal sealed record PaneLayoutDefinition(
    int Rows,
    int Columns,
    IReadOnlyList<PaneSlotPlacement> Slots,
    IReadOnlyList<double>? RowWeights = null,
    IReadOnlyList<double>? ColumnWeights = null);

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

    [ObservableProperty]
    private double _infoPanelWidth = 380;

    [ObservableProperty]
    private bool _isPreviewExpanded;

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
    private bool _suppressSelectedTabRouting;
    private bool _suppressActiveSlotRouting;

    public ObservableCollection<ExplorerTabViewModel> Tabs { get; } = [];
    public ObservableCollection<ExplorerTabViewModel> VisiblePanes { get; } = [];

    [ObservableProperty]
    private FileListViewModel _fileList = null!;

    [ObservableProperty]
    private ExplorerTabViewModel? _selectedTab;

    [ObservableProperty]
    private PaneLayout _paneLayout = PaneLayout.Single;

    [ObservableProperty]
    private int _activePaneSlotIndex;

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

    public bool ActivatePane(ExplorerTabViewModel tab)
    {
        var slot = VisiblePanes.IndexOf(tab);
        if (slot < 0)
            return false;

        ActivePaneSlotIndex = slot;
        if (!ReferenceEquals(SelectedTab, tab))
            SelectedTab = tab;
        return true;
    }

    public bool RemoveTab(ExplorerTabViewModel tab)
    {
        if (Tabs.Count <= 1)
            return false;

        var removedIndex = Tabs.IndexOf(tab);
        if (removedIndex < 0)
            return false;

        var oldTabs = Tabs.ToArray();
        var oldVisible = VisiblePanes.ToArray();
        var removedSlot = Array.IndexOf(oldVisible, tab);
        var wasSelected = ReferenceEquals(SelectedTab, tab);
        var oldActiveSlot = Math.Clamp(ActivePaneSlotIndex, 0, Math.Max(0, PaneCount - 1));

        Tabs.RemoveAt(removedIndex);

        var replacementOrder = oldTabs
            .Skip(removedIndex + 1)
            .Concat(oldTabs.Take(removedIndex).Reverse())
            .Where(candidate => !ReferenceEquals(candidate, tab))
            .ToArray();

        var nextLayout = Tabs.Count < PaneCount
            ? Tabs.Count switch
            {
                1 => PaneLayout.Single,
                2 => PaneLayout.TwoColumns,
                3 => PaneLayout.MainLeftTwoRowsRight,
                _ => PaneLayout
            }
            : PaneLayout;

        var desired = GetPaneCount(nextLayout);
        var remainingVisible = oldVisible.Where(candidate => !ReferenceEquals(candidate, tab)).ToList();
        ExplorerTabViewModel activeTab;

        if (wasSelected)
        {
            activeTab = replacementOrder.FirstOrDefault(candidate => !remainingVisible.Contains(candidate))
                        ?? replacementOrder.FirstOrDefault()
                        ?? Tabs[0];
        }
        else
        {
            activeTab = SelectedTab is { } selected && Tabs.Contains(selected)
                ? selected
                : Tabs[0];
        }

        List<ExplorerTabViewModel> normalized;
        if (nextLayout == PaneLayout && removedSlot >= 0 && Tabs.Count >= desired)
        {
            normalized = oldVisible.Where(candidate => !ReferenceEquals(candidate, tab)).ToList();
            var replacement = replacementOrder.FirstOrDefault(candidate => !normalized.Contains(candidate));
            if (replacement != null)
                normalized.Insert(Math.Min(removedSlot, normalized.Count), replacement);
            normalized = BuildNormalizedPanes(normalized, desired, activeTab, oldActiveSlot, preserveSlots: true);
        }
        else
        {
            normalized = BuildNormalizedPanes(
                remainingVisible,
                desired,
                activeTab,
                Math.Min(oldActiveSlot, desired - 1),
                preserveSlots: nextLayout == PaneLayout);
        }

        if (PaneLayout != nextLayout)
            PaneLayout = nextLayout;

        var activeIndex = Math.Clamp(
            wasSelected ? Math.Min(oldActiveSlot, desired - 1) : normalized.IndexOf(activeTab),
            0,
            desired - 1);
        activeTab = normalized[activeIndex];
        ApplyPaneState(normalized, activeIndex, activeTab);

        OnPropertyChanged(nameof(PaneCount));
        OnPropertyChanged(nameof(IsMultiPane));
        UpdateCanClose();
        return true;
    }

    partial void OnSelectedTabChanged(ExplorerTabViewModel? value)
    {
        foreach (var tab in Tabs)
            tab.IsActive = ReferenceEquals(tab, value);

        if (value == null)
            return;

        if (!_suppressSelectedTabRouting)
        {
            var visibleIndex = VisiblePanes.IndexOf(value);
            if (visibleIndex >= 0)
            {
                SetActiveSlotWithoutRouting(visibleIndex);
            }
            else if (VisiblePanes.Count == 0)
            {
                VisiblePanes.Add(value);
                SetActiveSlotWithoutRouting(0);
            }
            else
            {
                var slot = Math.Clamp(ActivePaneSlotIndex, 0, VisiblePanes.Count - 1);
                VisiblePanes[slot] = value;
                SetActiveSlotWithoutRouting(slot);
            }
        }

        if (!ReferenceEquals(FileList, value.FileList))
            FileList = value.FileList;
    }

    partial void OnActivePaneSlotIndexChanged(int value)
    {
        if (_suppressActiveSlotRouting || VisiblePanes.Count == 0)
            return;

        var clamped = Math.Clamp(value, 0, Math.Min(PaneCount, VisiblePanes.Count) - 1);
        if (clamped != value)
            SetActiveSlotWithoutRouting(clamped);

        var tab = VisiblePanes[clamped];
        if (!ReferenceEquals(SelectedTab, tab))
            SelectedTab = tab;
    }

    public void SetPaneLayout(PaneLayout layout)
    {
        var oldCount = PaneCount;
        var oldVisible = VisiblePanes.ToArray();
        var activeTab = SelectedTab ?? oldVisible.ElementAtOrDefault(ActivePaneSlotIndex) ?? Tabs.FirstOrDefault();
        var oldActiveIndex = Math.Clamp(ActivePaneSlotIndex, 0, Math.Max(0, oldCount - 1));

        if (PaneLayout != layout)
            PaneLayout = layout;

        var desired = PaneCount;
        if (activeTab != null)
        {
            var normalized = BuildNormalizedPanes(
                oldVisible,
                desired,
                activeTab,
                Math.Min(oldActiveIndex, desired - 1),
                preserveSlots: desired >= oldCount);
            var activeIndex = normalized.IndexOf(activeTab);
            ApplyPaneState(normalized, activeIndex < 0 ? 0 : activeIndex, activeTab);
        }

        OnPropertyChanged(nameof(PaneCount));
        OnPropertyChanged(nameof(IsMultiPane));
    }

    private List<ExplorerTabViewModel> BuildNormalizedPanes(
        IEnumerable<ExplorerTabViewModel> source,
        int desired,
        ExplorerTabViewModel activeTab,
        int requestedActiveIndex,
        bool preserveSlots)
    {
        var target = new ExplorerTabViewModel?[desired];
        var activeIndex = Math.Clamp(requestedActiveIndex, 0, desired - 1);
        var sourceList = source.Where(Tabs.Contains).Distinct().ToList();

        if (preserveSlots)
        {
            for (var i = 0; i < Math.Min(sourceList.Count, desired); i++)
                target[i] = sourceList[i];
        }

        var existingActiveIndex = Array.IndexOf(target, activeTab);
        if (existingActiveIndex < 0)
            target[activeIndex] = activeTab;
        else
            activeIndex = existingActiveIndex;

        var candidates = sourceList
            .Concat(Tabs)
            .Where(candidate => !ReferenceEquals(candidate, activeTab))
            .Distinct()
            .ToArray();
        var candidateIndex = 0;
        for (var slot = 0; slot < target.Length; slot++)
        {
            if (target[slot] != null)
                continue;
            while (candidateIndex < candidates.Length && target.Contains(candidates[candidateIndex]))
                candidateIndex++;
            if (candidateIndex < candidates.Length)
                target[slot] = candidates[candidateIndex++];
        }

        return target.Where(tab => tab != null).Cast<ExplorerTabViewModel>().ToList();
    }

    private void ApplyPaneState(
        IReadOnlyList<ExplorerTabViewModel> panes,
        int activeIndex,
        ExplorerTabViewModel selected)
    {
        for (var i = 0; i < panes.Count; i++)
        {
            if (i < VisiblePanes.Count)
                VisiblePanes[i] = panes[i];
            else
                VisiblePanes.Add(panes[i]);
        }

        while (VisiblePanes.Count > panes.Count)
            VisiblePanes.RemoveAt(VisiblePanes.Count - 1);

        SetActiveSlotWithoutRouting(Math.Clamp(activeIndex, 0, Math.Max(0, panes.Count - 1)));
        _suppressSelectedTabRouting = true;
        try
        {
            SelectedTab = selected;
        }
        finally
        {
            _suppressSelectedTabRouting = false;
        }
    }

    private void SetActiveSlotWithoutRouting(int value)
    {
        _suppressActiveSlotRouting = true;
        try
        {
            ActivePaneSlotIndex = value;
        }
        finally
        {
            _suppressActiveSlotRouting = false;
        }
    }

    public static int GetPaneCount(PaneLayout layout) => GetPaneLayoutDefinition(layout).Slots.Count;

    internal static PaneLayoutDefinition GetPaneLayoutDefinition(PaneLayout layout) => layout switch
    {
        PaneLayout.Single => new(1, 1, [new(0, 0)]),
        PaneLayout.TwoColumns => new(1, 2, [new(0, 0), new(0, 1)]),
        PaneLayout.TwoRows => new(2, 1, [new(0, 0), new(1, 0)]),
        PaneLayout.ThreeColumns => new(1, 3, [new(0, 0), new(0, 1), new(0, 2)]),
        PaneLayout.ThreeRows => new(3, 1, [new(0, 0), new(1, 0), new(2, 0)]),
        PaneLayout.MainLeftTwoRowsRight => new(
            2, 2,
            [new(0, 0, 2), new(0, 1), new(1, 1)],
            ColumnWeights: [2, 1]),
        PaneLayout.MainRightTwoRowsLeft => new(
            2, 2,
            [new(0, 1, 2), new(0, 0), new(1, 0)],
            ColumnWeights: [1, 2]),
        PaneLayout.FourGrid => new(2, 2, [new(0, 0), new(0, 1), new(1, 0), new(1, 1)]),
        PaneLayout.FourColumns => new(1, 4, [new(0, 0), new(0, 1), new(0, 2), new(0, 3)]),
        PaneLayout.FourRows => new(4, 1, [new(0, 0), new(1, 0), new(2, 0), new(3, 0)]),
        PaneLayout.MainLeftThreeRowsRight => new(
            3, 2,
            [new(0, 0, 3), new(0, 1), new(1, 1), new(2, 1)],
            ColumnWeights: [2, 1]),
        PaneLayout.MainRightThreeRowsLeft => new(
            3, 2,
            [new(0, 1, 3), new(0, 0), new(1, 0), new(2, 0)],
            ColumnWeights: [1, 2]),
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, null)
    };

    private void UpdateCanClose()
    {
        var canClose = Tabs.Count > 1;
        foreach (var tab in Tabs)
            tab.CanClose = canClose;
    }
}
