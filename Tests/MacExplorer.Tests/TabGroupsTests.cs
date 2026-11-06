using Avalonia.Headless.XUnit;
using MacExplorer.Services;
using MacExplorer.Services.Subscriptions;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "TabGroups")]
public sealed class TabGroupsTests : FileListViewModelTestBase
{
    private sealed class TabsFixture : IDisposable
    {
        internal MainWindowViewModel Model { get; } = new();
        internal ExplorerTabViewModel[] Tabs { get; }
        internal TabsFixture(int count)
        {
            var files = new FakeFileService("/tmp/tab-group-tests");
            Tabs = Enumerable.Range(0, count).Select(_ => Model.AddTab(CreateViewModel(files), false, inheritGroup: false)).ToArray();
        }
        public void Dispose()
        {
            foreach (var tab in Tabs) { tab.Dispose(); tab.FileList.Dispose(); }
        }
    }

    [AvaloniaFact]
    public void NewTabsStayOutsideTheCurrentGroupUnlessExplicitlyRequested()
    {
        using var access = new GrantedFeatureAccess();
        using var fixture = new TabsFixture(4);
        var model = fixture.Model;
        var group = model.CreateGroup(fixture.Tabs);
        model.SetPaneLayout(PaneLayout.TwoColumns);
        var panes = model.VisiblePanes.ToArray();
        using var outsideFiles = CreateViewModel(new FakeFileService("/tmp/groups-new"));
        using var outside = model.AddTab(outsideFiles, true, insertIndex: 1);
        Assert.Null(outside.Group);
        Assert.Same(outside, model.Tabs[^1]);
        Assert.Equal(fixture.Tabs, model.Members(group));
        Assert.Equal(PaneLayout.Single, model.PaneLayout);
        model.ActivateGroup(group);
        Assert.Equal(panes, model.VisiblePanes);
        using var insideFiles = CreateViewModel(new FakeFileService("/tmp/groups-explicit"));
        using var inside = model.AddTab(insideFiles, true, group: group);
        Assert.Same(group, inside.Group);
        Assert.Equal(5, model.Members(group).Count());
    }

    [AvaloniaTheory]
    [InlineData(PaneLayout.TwoColumns)]
    [InlineData(PaneLayout.ThreeRows)]
    [InlineData(PaneLayout.FourGrid)]
    public void UnlimitedMembersReplaceOnlyTheActivePane(PaneLayout layout)
    {
        using var access = new GrantedFeatureAccess();
        using var fixture = new TabsFixture(8);
        var model = fixture.Model;
        var group = model.CreateGroup(fixture.Tabs);
        model.SetPaneLayout(layout);
        Assert.Equal(8, model.Members(group).Count());
        var before = model.VisiblePanes.ToArray();
        var slot = model.PaneCount - 1;
        model.ActivePaneSlotIndex = slot;
        model.SelectedTab = fixture.Tabs[7];
        Assert.Equal(before.Take(slot), model.VisiblePanes.Take(slot));
        Assert.Same(fixture.Tabs[7], model.VisiblePanes[slot]);
        model.SelectedTab = before[0];
        Assert.Equal(0, model.ActivePaneSlotIndex);
        Assert.Same(fixture.Tabs[7], model.VisiblePanes[slot]);
    }

    [AvaloniaFact]
    public void EachGroupRestoresLayoutPanesAndFocusAndCollapseKeepsContent()
    {
        using var access = new GrantedFeatureAccess();
        using var fixture = new TabsFixture(7);
        var model = fixture.Model;
        var first = model.CreateGroup(fixture.Tabs.Take(3));
        model.SetPaneLayout(PaneLayout.TwoRows);
        model.SelectedTab = fixture.Tabs[2];
        model.ActivePaneSlotIndex = 1;
        var firstPanes = model.VisiblePanes.ToArray();
        var firstSelected = model.SelectedTab;
        var second = model.CreateGroup(fixture.Tabs.Skip(3));
        model.ActivateGroup(second);
        model.SetPaneLayout(PaneLayout.FourGrid);
        model.ActivePaneSlotIndex = 3;
        var secondPanes = model.VisiblePanes.ToArray();
        model.ActivateGroup(first);
        Assert.Equal(PaneLayout.TwoRows, model.PaneLayout);
        Assert.Equal(firstPanes, model.VisiblePanes);
        Assert.Same(firstSelected, model.SelectedTab);
        Assert.Equal(1, model.ActivePaneSlotIndex);
        model.ToggleGroup(first);
        Assert.True(first.IsCollapsed);
        Assert.Equal(firstPanes, model.VisiblePanes);
        Assert.Same(firstSelected, model.SelectedTab);
        Assert.Same(first, model.SelectedStripItem);
        Assert.DoesNotContain(fixture.Tabs[0], model.TabStripItems);
        model.ActivateGroup(second);
        Assert.Equal(PaneLayout.FourGrid, model.PaneLayout);
        Assert.Equal(secondPanes, model.VisiblePanes);
        Assert.Equal(3, model.ActivePaneSlotIndex);
    }

    [AvaloniaFact]
    public void MovingExpandedAndCollapsedGroupsOnlyChangesTheirPosition()
    {
        using var access = new GrantedFeatureAccess();
        using var fixture = new TabsFixture(7);
        var model = fixture.Model;
        var first = model.CreateGroup(fixture.Tabs.Take(3));
        model.SetPaneLayout(PaneLayout.ThreeColumns);
        var second = model.CreateGroup(fixture.Tabs.Skip(3).Take(3));
        model.ActivateGroup(second);
        model.SetPaneLayout(PaneLayout.TwoRows);
        model.SelectedTab = fixture.Tabs[5];
        model.ActivateGroup(first);
        var selected = model.SelectedTab;
        var panes = model.VisiblePanes.ToArray();
        var firstState = model.CaptureGroups().Single(g => g.Id == first.Id);
        var secondPanes = second.Panes.ToArray();
        var secondSelected = second.LastSelectedTab;
        model.MoveStripItem(second, first, false);
        Assert.Equal(fixture.Tabs.Skip(3).Take(3).Concat(fixture.Tabs.Take(3)).Append(fixture.Tabs[6]), model.Tabs);
        Assert.Same(selected, model.SelectedTab);
        Assert.Equal(panes, model.VisiblePanes);
        Assert.Equal(PaneLayout.ThreeColumns, model.PaneLayout);
        Assert.Equal(secondPanes, second.Panes);
        Assert.Same(secondSelected, second.LastSelectedTab);
        Assert.Equal(firstState.ColorIndex, first.ColorIndex);
        model.ToggleGroup(first);
        model.MoveStripItem(first, fixture.Tabs[6], true);
        Assert.True(first.IsCollapsed);
        Assert.Same(selected, model.SelectedTab);
        Assert.Equal(panes, model.VisiblePanes);
        Assert.Equal(fixture.Tabs.Take(3), model.Tabs.TakeLast(3));
    }

    [AvaloniaFact]
    public void ClosingAndMovingMembersRefillsOnlyFromTheSameGroup()
    {
        using var access = new GrantedFeatureAccess();
        using var fixture = new TabsFixture(7);
        var model = fixture.Model;
        var group = model.CreateGroup(fixture.Tabs.Take(6));
        model.SetPaneLayout(PaneLayout.FourGrid);
        Assert.True(model.RemoveTab(fixture.Tabs[1]));
        Assert.Equal(4, model.VisiblePanes.Count);
        Assert.DoesNotContain(fixture.Tabs[6], model.VisiblePanes);
        Assert.All(model.VisiblePanes, tab => Assert.Same(group, tab.Group));
        model.MoveTabToGroup(fixture.Tabs[2], null);
        Assert.Equal(4, model.VisiblePanes.Count);
        Assert.DoesNotContain(fixture.Tabs[2], model.VisiblePanes);
        model.RemoveTab(fixture.Tabs[3]);
        Assert.Equal(3, model.VisiblePanes.Count);
        Assert.Equal(PaneLayout.MainLeftTwoRowsRight, model.PaneLayout);
        model.Ungroup(group);
        Assert.Empty(model.Groups);
        Assert.Equal(PaneLayout.Single, model.PaneLayout);
        Assert.Single(model.VisiblePanes);
    }

    [AvaloniaFact]
    public void ClosingTheLastMemberActivatesTheSurvivingTab()
    {
        using var access = new GrantedFeatureAccess();
        using var fixture = new TabsFixture(3);
        var model = fixture.Model;
        var group = model.CreateGroup(fixture.Tabs.Take(2));
        model.SetPaneLayout(PaneLayout.TwoColumns);
        Assert.True(model.RemoveTab(fixture.Tabs[0]));
        Assert.True(model.RemoveTab(fixture.Tabs[1]));
        Assert.DoesNotContain(group, model.Groups);
        Assert.Same(fixture.Tabs[2], model.SelectedTab);
        Assert.Same(fixture.Tabs[2], Assert.Single(model.VisiblePanes));
        Assert.Empty(group.Panes);
        Assert.Null(group.LastSelectedTab);
    }

    [AvaloniaFact]
    public void AutoGroupingUsesFollowingUngroupedTabsAndDoesNotBorrowAnotherGroup()
    {
        using var access = new GrantedFeatureAccess();
        using var fixture = new TabsFixture(6);
        var model = fixture.Model;
        var other = model.CreateGroup(fixture.Tabs.Skip(3));
        model.SetPaneLayout(PaneLayout.ThreeColumns);
        Assert.Equal(fixture.Tabs.Take(3), model.Members(model.SelectedTab!.Group));
        Assert.Equal(fixture.Tabs.Skip(3), model.Members(other));
        model.SelectedTab = fixture.Tabs[0];
        model.SetPaneLayout(PaneLayout.Single);
        Assert.Equal(3, model.Members(model.SelectedTab.Group).Count());
    }

    [AvaloniaFact]
    public void MixedGroupOperationsKeepContiguousMembershipAndValidVisiblePanes()
    {
        using var access = new GrantedFeatureAccess();
        using var fixture = new TabsFixture(16);
        var model = fixture.Model;
        model.CreateGroup(fixture.Tabs.Take(5));
        model.CreateGroup(fixture.Tabs.Skip(5).Take(5));
        var random = new Random(7391);
        for (var step = 0; step < 180; step++)
        {
            var tab = model.Tabs[random.Next(model.Tabs.Count)];
            var group = model.Groups.Count > 0 ? model.Groups[random.Next(model.Groups.Count)] : null;
            switch (random.Next(8))
            {
                case 0: model.SelectedTab = tab; break;
                case 1: model.MoveTabToGroup(tab, group); break;
                case 2: model.CreateGroup([tab]); break;
                case 3: if (group != null) model.MoveStripItem(group, tab, random.Next(2) == 0); break;
                case 4: if (group != null) model.ToggleGroup(group); break;
                case 5: if (group != null) model.Ungroup(group); break;
                case 7: if (model.Tabs.Count > 1) model.RemoveTab(tab); break;
                case 6:
                    model.SelectedTab = tab;
                    if (tab.Group is { } owner)
                        model.SetPaneLayout(model.Members(owner).Count() >= 4 ? PaneLayout.FourGrid : PaneLayout.Single);
                    break;
            }
            Assert.Equal(model.PaneCount, model.VisiblePanes.Count);
            Assert.Equal(model.VisiblePanes.Count, model.VisiblePanes.Distinct().Count());
            Assert.Contains(model.SelectedTab!, model.VisiblePanes);
            Assert.All(model.VisiblePanes, visible => Assert.Equal(model.SelectedTab!.Group, visible.Group));
            foreach (var owner in model.Groups)
            {
                var indices = model.Members(owner).Select(member => model.Tabs.IndexOf(member)).ToArray();
                Assert.NotEmpty(indices);
                Assert.Equal(Enumerable.Range(indices[0], indices.Length), indices);
            }
        }
    }

    [AvaloniaFact]
    public void RestrictedSplitDisplayKeepsSavedLayoutUntilAccessReturns()
    {
        using var granted = new GrantedFeatureAccess();
        using var fixture = new TabsFixture(4);
        var model = fixture.Model;
        model.SetPaneLayout(PaneLayout.FourGrid);
        var group = Assert.Single(model.Groups);
        var panes = model.VisiblePanes.ToArray();
        var originalAccess = FeatureAccess.Current;
        using var denied = new FeatureAccessService(null, null, true);
        try
        {
            FeatureAccess.Current = denied;
            model.RefreshSplitAccess();
            Assert.Equal(PaneLayout.Single, model.PaneLayout);
            Assert.Equal(PaneLayout.FourGrid, group.Layout);
            Assert.Equal(panes, group.Panes);
            Assert.Equal(PaneLayout.FourGrid, Assert.Single(model.CaptureGroups()).Layout);
        }
        finally { FeatureAccess.Current = originalAccess; }
        model.RefreshSplitAccess();
        Assert.Equal(PaneLayout.FourGrid, model.PaneLayout);
        Assert.Equal(panes, model.VisiblePanes);
    }
}
