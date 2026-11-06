using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Subscriptions;

namespace MacExplorer.ViewModels;

public sealed partial class TabGroupViewModel : ObservableObject
{
    private static readonly string[] Colors =
        ["#286BF5", "#D90083", "#287B59", "#884DB3", "#AF512C", "#7B652C", "#36747F", "#5E5DB9"];
    public Guid Id { get; }
    public int ColorIndex { get; }
    public IBrush ColorBrush { get; }
    internal static int ColorCount => Colors.Length;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isCollapsed;
    [ObservableProperty] private bool _isActive;
    internal PaneLayout Layout { get; set; } = PaneLayout.Single;
    internal List<ExplorerTabViewModel> Panes { get; set; } = [];
    internal ExplorerTabViewModel? LastSelectedTab { get; set; }
    internal int ActiveSlot { get; set; }

    internal TabGroupViewModel(string name, int colorIndex, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        _name = name;
        ColorIndex = Math.Clamp(colorIndex, 0, Colors.Length - 1);
        var color = Color.Parse(Colors[ColorIndex]);
        ColorBrush = new SolidColorBrush(color);
    }
}

public partial class MainWindowViewModel
{
    private TabGroupViewModel? _activeGroup;
    private bool _restrictedGroupDisplay;
    private bool _refreshingStrip;
    private bool _deferStrip;
    public ObservableCollection<TabGroupViewModel> Groups { get; } = [];
    public ObservableCollection<object> TabStripItems { get; } = [];
    [ObservableProperty] private object? _selectedStripItem;

    partial void OnSelectedStripItemChanged(object? value)
    {
        if (_refreshingStrip) return;
        if (value is ExplorerTabViewModel tab && Tabs.Contains(tab)) SelectedTab = tab;
        else if (value is TabGroupViewModel group) ActivateGroup(group);
    }

    internal IEnumerable<ExplorerTabViewModel> Members(TabGroupViewModel? group)
        => Tabs.Where(tab => tab.Group == group);
    private int GroupStart(TabGroupViewModel group)
        => Tabs.FirstOrDefault(tab => tab.Group == group) is { } first ? Tabs.IndexOf(first) : Tabs.Count;
    private int GroupEnd(TabGroupViewModel group)
        => Tabs.LastOrDefault(tab => tab.Group == group) is { } last ? Tabs.IndexOf(last) + 1 : Tabs.Count;

    internal ExplorerTabViewModel[] GetSplitCandidates(int desired)
    {
        if (SelectedTab is not { } selected) return [];
        if (selected.Group != null) return Members(selected.Group).ToArray();
        var candidates = new List<ExplorerTabViewModel> { selected };
        for (var i = Tabs.IndexOf(selected) + 1; i < Tabs.Count && candidates.Count < desired; i++)
        {
            if (Tabs[i].Group != null) break;
            candidates.Add(Tabs[i]);
        }
        return candidates.ToArray();
    }

    public TabGroupViewModel CreateGroup(IEnumerable<ExplorerTabViewModel> tabs)
    {
        var members = tabs.Where(Tabs.Contains).Distinct().ToArray();
        if (members.Length == 0) throw new ArgumentException("A group needs at least one tab.", nameof(tabs));
        SaveActiveGroupState();
        var adjacentColors = new[] { members[0].Group,
                Tabs.ElementAtOrDefault(Tabs.IndexOf(members[0]) - 1)?.Group,
                Tabs.ElementAtOrDefault(Tabs.IndexOf(members[^1]) + 1)?.Group }
            .OfType<TabGroupViewModel>().Select(group => group.ColorIndex).ToHashSet();
        var color = Enumerable.Range(0, TabGroupViewModel.ColorCount)
            .OrderBy(index => adjacentColors.Contains(index))
            .ThenBy(index => Groups.Count(group => group.ColorIndex == index))
            .First();
        var number = 1;
        string name;
        do { name = LocalizationText.Get("tabs.group.default", number++); }
        while (Groups.Any(group => group.Name == name));
        var group = new TabGroupViewModel(name, color);
        Groups.Add(group);
        var index = members[0].Group is { } owner ? GroupEnd(owner) : Tabs.IndexOf(members[0]);
        var owners = members.Select(tab => tab.Group).OfType<TabGroupViewModel>().Distinct().ToArray();
        index -= members.Count(tab => Tabs.IndexOf(tab) < index);
        _deferStrip = true;
        try
        {
            foreach (var tab in members) { Tabs.Remove(tab); tab.Group = group; }
            for (var i = 0; i < members.Length; i++) Tabs.Insert(index + i, members[i]);
            foreach (var previous in owners) RepairGroup(previous, index);
            group.LastSelectedTab = members.Contains(SelectedTab) ? SelectedTab : members[0];
            if (SelectedTab != null) RestoreContext(SelectedTab);
        }
        finally { _deferStrip = false; }
        RefreshTabStrip();
        return group;
    }

    public void ToggleGroup(TabGroupViewModel group)
    {
        if (!Groups.Contains(group)) return;
        group.IsCollapsed = !group.IsCollapsed;
        RefreshTabStrip();
    }

    public void ActivateGroup(TabGroupViewModel group)
    {
        if (!Groups.Contains(group) || group == _activeGroup) return;
        var selected = group.LastSelectedTab ?? Members(group).FirstOrDefault();
        if (selected != null) SelectedTab = selected;
    }

    public void MoveTabToGroup(ExplorerTabViewModel tab, TabGroupViewModel? group, int? destination = null)
    {
        if (!Tabs.Contains(tab) || group != null && !Groups.Contains(group)) return;
        if (tab.Group == group)
        {
            if (destination is { } target)
            {
                var low = group == null ? 0 : GroupStart(group);
                var high = group == null ? Tabs.Count - 1 : GroupEnd(group) - 1;
                Tabs.Move(Tabs.IndexOf(tab), Math.Clamp(target, low, high));
            }
            return;
        }
        SaveActiveGroupState();
        var owner = tab.Group;
        var oldIndex = Tabs.IndexOf(tab);
        var ungroupedIndex = owner != null ? GroupEnd(owner) - 1 : oldIndex;
        Tabs.Remove(tab);
        tab.Group = group;
        var index = group == null ? destination ?? ungroupedIndex
            : Math.Clamp(destination ?? GroupEnd(group), GroupStart(group), GroupEnd(group));
        Tabs.Insert(Math.Clamp(index, 0, Tabs.Count), tab);
        RepairGroup(owner, oldIndex);
        if (group != null && group.LastSelectedTab == null) group.LastSelectedTab = tab;
        if (SelectedTab != null) RestoreContext(SelectedTab);
        RefreshTabStrip();
    }

    public void Ungroup(TabGroupViewModel group)
    {
        SaveActiveGroupState();
        foreach (var tab in Members(group).ToArray()) tab.Group = null;
        Groups.Remove(group);
        if (SelectedTab != null) RestoreContext(SelectedTab);
        RefreshTabStrip();
    }

    internal void MoveStripItem(object item, object? anchor, bool after, bool ungroup = false)
    {
        if (item == anchor) return;
        if (item is TabGroupViewModel group)
        {
            var block = Members(group).ToArray();
            if (block.Length == 0 || anchor is ExplorerTabViewModel member && member.Group == group) return;
            var targetGroup = anchor as TabGroupViewModel ?? (anchor as ExplorerTabViewModel)?.Group;
            var target = targetGroup != null ? (after ? GroupEnd(targetGroup) : GroupStart(targetGroup))
                : anchor is ExplorerTabViewModel tab ? Tabs.IndexOf(tab) + (after ? 1 : 0) : after ? Tabs.Count : 0;
            var order = Tabs.ToList();
            target -= block.Count(tab => Tabs.IndexOf(tab) < target);
            order.RemoveAll(block.Contains);
            order.InsertRange(Math.Clamp(target, 0, order.Count), block);
            _deferStrip = true;
            try
            {
                for (var i = 0; i < order.Count; i++)
                    if (Tabs[i] != order[i]) Tabs.Move(Tabs.IndexOf(order[i]), i);
            }
            finally { _deferStrip = false; }
            RefreshTabStrip();
        }
        else if (item is ExplorerTabViewModel moving)
        {
            var anchorGroup = anchor as TabGroupViewModel ?? (anchor as ExplorerTabViewModel)?.Group;
            var targetGroup = ungroup ? null : anchorGroup;
            var destination = ungroup && anchorGroup != null
                ? (after ? GroupEnd(anchorGroup) : GroupStart(anchorGroup))
                : anchor is ExplorerTabViewModel tab ? Tabs.IndexOf(tab) + (after ? 1 : 0)
                : targetGroup != null ? (after ? GroupEnd(targetGroup) : GroupStart(targetGroup)) : after ? Tabs.Count : 0;
            if (Tabs.IndexOf(moving) < destination) destination--;
            MoveTabToGroup(moving, targetGroup, destination);
        }
    }

    private void SaveActiveGroupState()
    {
        if (_activeGroup == null || !Groups.Contains(_activeGroup)) return;
        _activeGroup.LastSelectedTab = SelectedTab;
        if (_restrictedGroupDisplay) return;
        _activeGroup.Layout = PaneLayout;
        _activeGroup.Panes = VisiblePanes.ToList();
        _activeGroup.ActiveSlot = ActivePaneSlotIndex;
    }

    private void RepairGroup(TabGroupViewModel? group, int removedIndex)
    {
        if (group == null) return;
        var members = Members(group).ToArray();
        if (members.Length == 0)
        {
            Groups.Remove(group);
            group.Panes.Clear();
            group.LastSelectedTab = null;
            return;
        }
        group.Layout = FitLayout(group.Layout, members.Length);
        var count = GetPaneCount(group.Layout);
        var oldPanes = group.Panes.ToArray();
        var candidates = members.OrderBy(tab => Tabs.IndexOf(tab) < removedIndex ? Tabs.Count - Tabs.IndexOf(tab) : Tabs.IndexOf(tab) - removedIndex).ToArray();
        var panes = new ExplorerTabViewModel?[count];
        for (var i = 0; i < Math.Min(count, oldPanes.Length); i++)
            if (members.Contains(oldPanes[i])) panes[i] = oldPanes[i];
        for (var i = 0; i < panes.Length; i++)
            panes[i] ??= candidates.First(tab => !panes.Contains(tab));
        group.Panes = panes.OfType<ExplorerTabViewModel>().ToList();
        group.ActiveSlot = Math.Clamp(group.ActiveSlot, 0, count - 1);
        if (group.LastSelectedTab == null || !members.Contains(group.LastSelectedTab))
            group.LastSelectedTab = group.Panes[group.ActiveSlot];
    }

    internal static PaneLayout FitLayout(PaneLayout layout, int memberCount)
        => GetPaneCount(layout) <= memberCount ? layout : memberCount switch
        {
            <= 1 => PaneLayout.Single,
            2 => PaneLayout.TwoColumns,
            _ => PaneLayout.MainLeftTwoRowsRight
        };

    private void RestoreContext(ExplorerTabViewModel selected)
    {
        _activeGroup = selected.Group;
        var group = _activeGroup;
        var savedLayout = group == null ? PaneLayout.Single : FitLayout(group.Layout, Members(group).Count());
        _restrictedGroupDisplay = savedLayout != PaneLayout.Single && !FeatureAccess.CanUse(PremiumFeature.SplitView);
        PaneLayout = _restrictedGroupDisplay ? PaneLayout.Single : savedLayout;
        var panes = BuildNormalizedPanes(group?.Panes ?? [], PaneCount, selected, group?.ActiveSlot ?? 0, true);
        ApplyPaneState(panes, Math.Max(0, panes.IndexOf(selected)), selected);
        FileList = selected.FileList;
        SaveActiveGroupState();
        UpdateStripSelection();
        OnPropertyChanged(nameof(PaneCount));
        OnPropertyChanged(nameof(IsMultiPane));
    }

    internal void RefreshSplitAccess()
    {
        SaveActiveGroupState();
        if (SelectedTab != null) RestoreContext(SelectedTab);
    }

    private void RefreshTabStrip()
    {
        if (_deferStrip) return;
        var items = new List<object>();
        var seen = new HashSet<TabGroupViewModel>();
        foreach (var tab in Tabs)
        {
            if (tab.Group is { } group)
            {
                if (seen.Add(group)) items.Add(group);
                if (group.IsCollapsed) continue;
            }
            items.Add(tab);
        }
        _refreshingStrip = true;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (i < TabStripItems.Count && ReferenceEquals(TabStripItems[i], items[i])) continue;
                var existing = TabStripItems.IndexOf(items[i]);
                if (existing >= 0) TabStripItems.Move(existing, i);
                else TabStripItems.Insert(i, items[i]);
            }
            while (TabStripItems.Count > items.Count) TabStripItems.RemoveAt(TabStripItems.Count - 1);
            UpdateStripSelection();
        }
        finally { _refreshingStrip = false; }
    }

    private void UpdateStripSelection()
    {
        foreach (var group in Groups) group.IsActive = group == SelectedTab?.Group;
        var wasRefreshing = _refreshingStrip;
        _refreshingStrip = true;
        try { SelectedStripItem = SelectedTab?.Group is { IsCollapsed: true } group ? group : SelectedTab; }
        finally { _refreshingStrip = wasRefreshing; }
    }

    internal FolderTabGroupSession[] CaptureGroups()
    {
        SaveActiveGroupState();
        return Tabs.Where(tab => tab.Group != null).Select(tab => tab.Group!).Distinct()
            .Select(group => new FolderTabGroupSession(group.Id, group.Name, group.ColorIndex, group.IsCollapsed,
                Members(group).Select(tab => Tabs.IndexOf(tab)).ToArray(), group.Layout,
                group.Panes.Select(tab => Tabs.IndexOf(tab)).ToArray(),
                group.LastSelectedTab == null ? -1 : Tabs.IndexOf(group.LastSelectedTab), group.ActiveSlot)).ToArray();
    }

    internal void RestoreGroups(FolderTabGroupSession[] groups, IReadOnlyDictionary<int, ExplorerTabViewModel> restored)
    {
        foreach (var saved in groups)
        {
            var members = saved.TabIndices.Where(restored.ContainsKey).Select(i => restored[i]).ToArray();
            if (members.Length == 0) continue;
            var group = new TabGroupViewModel(string.IsNullOrWhiteSpace(saved.Name)
                ? LocalizationText.Get("tabs.group.default", Groups.Count + 1) : saved.Name, saved.ColorIndex, saved.Id)
            {
                IsCollapsed = saved.IsCollapsed,
                Layout = FitLayout(saved.Layout, members.Length),
                Panes = saved.PaneIndices.Where(restored.ContainsKey).Select(i => restored[i]).Where(members.Contains).ToList(),
                LastSelectedTab = restored.GetValueOrDefault(saved.SelectedIndex),
                ActiveSlot = saved.ActivePaneSlotIndex
            };
            Groups.Add(group);
            foreach (var tab in members) tab.Group = group;
            RepairGroup(group, 0);
        }
        RefreshTabStrip();
    }
}
