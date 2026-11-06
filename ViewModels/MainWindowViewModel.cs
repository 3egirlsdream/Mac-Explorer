using MacExplorer.Services.Subscriptions;
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
    internal MacExplorer.Services.FileListTabViewState? CachedListState { get; set; }

    [ObservableProperty]
    private TabGroupViewModel? _group;

    public Guid? GroupId => Group?.Id;

    partial void OnGroupChanged(TabGroupViewModel? value) => OnPropertyChanged(nameof(GroupId));

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
        return string.IsNullOrWhiteSpace(title) ? MacExplorer.Services.Impl.LocalizationText.Get("首页") : title;
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
        Tabs.CollectionChanged += (_, _) => RefreshTabStrip();
    }

    public MainWindowViewModel(FileListViewModel fileList) : this()
    {
        _fileList = fileList;
        AddTab(fileList, select: true);
    }

    public ExplorerTabViewModel AddTab(FileListViewModel fileList, bool select, int? insertIndex = null,
        TabGroupViewModel? group = null, bool inheritGroup = false)
    {
        group ??= inheritGroup ? SelectedTab?.Group : null;
        var tab = new ExplorerTabViewModel(fileList) { Group = group };
        var index = group == null ? insertIndex ?? Tabs.Count
            : Math.Clamp(insertIndex ?? GroupEnd(group), GroupStart(group), GroupEnd(group));
        if (group == null && index > 0 && index < Tabs.Count
            && Tabs[index].Group is { } neighbor && Tabs[index - 1].Group == neighbor)
            index = GroupEnd(neighbor);
        Tabs.Insert(Math.Clamp(index, 0, Tabs.Count), tab);
        UpdateCanClose();
        if (select || SelectedTab == null) SelectedTab = tab;
        RefreshTabStrip();
        return tab;
    }

    public void MoveTab(ExplorerTabViewModel tab, int destination)
    {
        if (!Tabs.Contains(tab) || destination < 0 || destination >= Tabs.Count) return;
        var target = Tabs[destination];
        MoveTabToGroup(tab, target.Group, destination);
    }

    public ExplorerTabViewModel? SelectRelativeTab(int offset)
    {
        if (Tabs.Count < 2 || SelectedTab == null) return SelectedTab;
        var index = (Tabs.IndexOf(SelectedTab) + offset) % Tabs.Count;
        SelectedTab = Tabs[index < 0 ? index + Tabs.Count : index];
        return SelectedTab;
    }

    public bool ActivatePane(ExplorerTabViewModel tab)
    {
        var slot = VisiblePanes.IndexOf(tab);
        if (slot < 0) return false;
        ActivePaneSlotIndex = slot;
        if (!ReferenceEquals(SelectedTab, tab)) SelectedTab = tab;
        return true;
    }

    public bool RemoveTab(ExplorerTabViewModel tab)
    {
        if (Tabs.Count <= 1 || !Tabs.Contains(tab)) return false;
        SaveActiveGroupState();
        var index = Tabs.IndexOf(tab);
        var owner = tab.Group;
        var wasSelected = ReferenceEquals(SelectedTab, tab);
        Tabs.Remove(tab);
        RepairGroup(owner, index);
        var selected = wasSelected
            ? owner?.LastSelectedTab ?? Tabs.ElementAtOrDefault(Math.Min(index, Tabs.Count - 1))
            : SelectedTab;
        tab.Group = null;
        if (selected != null) RestoreContext(selected);
        UpdateCanClose();
        RefreshTabStrip();
        return true;
    }

    partial void OnSelectedTabChanging(ExplorerTabViewModel? value)
    {
        if (!_suppressSelectedTabRouting && value != null && value.Group != _activeGroup)
            SaveActiveGroupState();
    }

    partial void OnSelectedTabChanged(ExplorerTabViewModel? value)
    {
        foreach (var tab in Tabs) tab.IsActive = ReferenceEquals(tab, value);
        if (value == null) return;
        if (!_suppressSelectedTabRouting)
        {
            if (value.Group != _activeGroup || VisiblePanes.Count == 0)
                RestoreContext(value);
            else if (value.Group == null)
                ApplyPaneState([value], 0, value);
            else
            {
                var slot = VisiblePanes.IndexOf(value);
                if (slot < 0)
                {
                    slot = Math.Clamp(ActivePaneSlotIndex, 0, VisiblePanes.Count - 1);
                    VisiblePanes[slot] = value;
                }
                SetActiveSlotWithoutRouting(slot);
                SaveActiveGroupState();
            }
        }
        if (!ReferenceEquals(FileList, value.FileList)) FileList = value.FileList;
        UpdateStripSelection();
    }

    partial void OnActivePaneSlotIndexChanged(int value)
    {
        if (_suppressActiveSlotRouting || VisiblePanes.Count == 0) return;
        var slot = Math.Clamp(value, 0, VisiblePanes.Count - 1);
        if (slot != value) SetActiveSlotWithoutRouting(slot);
        if (!ReferenceEquals(SelectedTab, VisiblePanes[slot])) SelectedTab = VisiblePanes[slot];
        SaveActiveGroupState();
    }

    partial void OnPaneLayoutChanging(PaneLayout value)
    {
        if (GetPaneCount(value) > 1) FeatureAccess.Require(PremiumFeature.SplitView);
    }

    public void SetPaneLayout(PaneLayout layout)
    {
        if (GetPaneCount(layout) > 1 && !FeatureAccess.TryUse(PremiumFeature.SplitView)) return;
        if (SelectedTab == null) return;
        if (layout != PaneLayout.Single && SelectedTab.Group == null)
        {
            var candidates = GetSplitCandidates(GetPaneCount(layout));
            if (candidates.Length < GetPaneCount(layout)) return;
            CreateGroup(candidates);
        }
        if (SelectedTab.Group != null && Members(SelectedTab.Group).Count() < GetPaneCount(layout)) return;
        var active = SelectedTab;
        var oldCount = PaneCount;
        var oldSlot = ActivePaneSlotIndex;
        var oldPanes = VisiblePanes.ToArray();
        _restrictedGroupDisplay = false;
        PaneLayout = layout;
        var panes = BuildNormalizedPanes(oldPanes, PaneCount, active,
            Math.Min(oldSlot, PaneCount - 1), PaneCount >= oldCount);
        ApplyPaneState(panes, Math.Max(0, panes.IndexOf(active)), active);
        SaveActiveGroupState();
        OnPropertyChanged(nameof(PaneCount));
        OnPropertyChanged(nameof(IsMultiPane));
    }

    private List<ExplorerTabViewModel> BuildNormalizedPanes(IEnumerable<ExplorerTabViewModel> source,
        int desired, ExplorerTabViewModel activeTab, int requestedActiveIndex, bool preserveSlots)
    {
        var members = Members(activeTab.Group).ToArray();
        var target = new ExplorerTabViewModel?[desired];
        var activeIndex = Math.Clamp(requestedActiveIndex, 0, desired - 1);
        var sourceList = source.Where(members.Contains).Distinct().ToList();
        if (preserveSlots)
            for (var i = 0; i < Math.Min(sourceList.Count, desired); i++) target[i] = sourceList[i];
        if (!target.Contains(activeTab)) target[activeIndex] = activeTab;
        var candidates = sourceList.Concat(members).Distinct().Where(tab => tab != activeTab).ToArray();
        var candidateIndex = 0;
        for (var slot = 0; slot < target.Length; slot++)
        {
            if (target[slot] != null) continue;
            while (candidateIndex < candidates.Length && target.Contains(candidates[candidateIndex])) candidateIndex++;
            if (candidateIndex < candidates.Length) target[slot] = candidates[candidateIndex++];
        }
        return target.OfType<ExplorerTabViewModel>().ToList();
    }

    private void ApplyPaneState(IReadOnlyList<ExplorerTabViewModel> panes, int activeIndex, ExplorerTabViewModel selected)
    {
        for (var i = 0; i < panes.Count; i++)
        {
            if (i < VisiblePanes.Count) VisiblePanes[i] = panes[i];
            else VisiblePanes.Add(panes[i]);
        }
        while (VisiblePanes.Count > panes.Count) VisiblePanes.RemoveAt(VisiblePanes.Count - 1);
        SetActiveSlotWithoutRouting(Math.Clamp(activeIndex, 0, Math.Max(0, panes.Count - 1)));
        _suppressSelectedTabRouting = true;
        try { SelectedTab = selected; }
        finally { _suppressSelectedTabRouting = false; }
    }

    private void SetActiveSlotWithoutRouting(int value)
    {
        _suppressActiveSlotRouting = true;
        try { ActivePaneSlotIndex = value; }
        finally { _suppressActiveSlotRouting = false; }
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
