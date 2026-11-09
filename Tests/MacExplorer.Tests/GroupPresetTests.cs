using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "GroupPresets")]
public sealed class GroupPresetTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task MenusSaveReplaceRenameAndDeletePresetsWithoutChangingOpenGroups()
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        await using var fixture = await CreateFixture(root);
        await fixture.Model.FileList.NavigateToAsync(root.First);
        var group = fixture.Model.CreateGroup(fixture.Model.Tabs);
        group.Name = "Work";
        var settings = App.Services.GetRequiredService<ISettingsService>();
        var button = fixture.Window.FindControl<Button>("GroupPresetsButton")!;
        ContextMenu Menu() => (ContextMenu)typeof(MainWindow)
            .GetField("_tabContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window)!;
        MenuItem Item(ItemsControl menu, string key) => menu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, LocalizationText.Get(key)));
        void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        async Task SubmitName(string name)
        {
            await WaitUntil(() => fixture.Window.OwnedWindows.OfType<TabGroupNameDialog>().Any());
            var dialog = fixture.Window.OwnedWindows.OfType<TabGroupNameDialog>().Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = name;
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        try
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(button.Classes.Contains("active"));
            Assert.False(Item(Menu(), "tabs.preset.manage").IsEnabled);
            Click(Item(Menu(), "tabs.preset.save"));
            await SubmitName("Work");
            await WaitUntil(() => TabGroupPresets.Read(settings).Length == 1);
            var id = TabGroupPresets.Read(settings)[0].Id;
            Assert.False(button.Classes.Contains("active"));
            await fixture.Window.OpenGroupPresetAsync(TabGroupPresets.Read(settings)[0]);
            Assert.Same(group, Assert.Single(fixture.Model.Groups));
            Assert.Single(fixture.Model.Tabs);

            await fixture.Model.FileList.NavigateToAsync(root.Second);
            fixture.Window.ShowGroupContextMenu(button, group);
            Click(Item(Menu(), "tabs.preset.save"));
            await SubmitName("Work");
            await WaitUntil(() => fixture.Window.OwnedWindows.OfType<ConfirmDialog>().Any());
            fixture.Window.OwnedWindows.OfType<ConfirmDialog>().Single().Close(false);
            await WaitUntil(() => !fixture.Window.OwnedWindows.Any());
            Assert.Equal(root.First, TabGroupPresets.Read(settings)[0].Session.Paths[0]);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click(Item(Menu(), "tabs.preset.save"));
            await SubmitName("Work");
            await WaitUntil(() => fixture.Window.OwnedWindows.OfType<ConfirmDialog>().Any());
            fixture.Window.OwnedWindows.OfType<ConfirmDialog>().Single().Close(true);
            await WaitUntil(() => TabGroupPresets.Read(settings)[0].Session.Paths[0] == root.Second);
            Assert.Equal(id, TabGroupPresets.Read(settings)[0].Id);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var entry = Assert.IsType<MenuItem>(Item(Menu(), "tabs.preset.manage").Items[0]);
            Click(Item(entry, "tabs.preset.rename"));
            await SubmitName("Renamed");
            await WaitUntil(() => TabGroupPresets.Read(settings)[0].Name == "Renamed");
            Assert.Equal("Work", group.Name);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            entry = Assert.IsType<MenuItem>(Item(Menu(), "tabs.preset.manage").Items[0]);
            Click(Item(entry, "tabs.preset.delete"));
            await WaitUntil(() => fixture.Window.OwnedWindows.OfType<ConfirmDialog>().Any());
            fixture.Window.OwnedWindows.OfType<ConfirmDialog>().Single().Close(true);
            await WaitUntil(() => TabGroupPresets.Read(settings).Length == 0);
            Assert.Same(group, Assert.Single(fixture.Model.Groups));
            Assert.Equal(root.Second, fixture.Model.FileList.CurrentPath);
        }
        finally
        {
            foreach (var dialog in fixture.Window.OwnedWindows.ToArray()) dialog.Close();
            Menu()?.Close();
        }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task OpeningAppendsExpandedGroupAndSwitchingBackPreservesExistingWorkspace()
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        using var access = new GrantedFeatureAccess();
        await using var fixture = await CreateFixture(root);
        fixture.AddTab();
        var originalTabs = fixture.Model.Tabs.ToArray();
        var original = fixture.Model.CreateGroup(originalTabs);
        fixture.Model.SetPaneLayout(PaneLayout.TwoRows);
        fixture.Model.ActivePaneSlotIndex = 1;
        var originalPanes = fixture.Model.VisiblePanes.ToArray();
        var originalSelected = fixture.Model.SelectedTab;
        var preset = root.Preset();
        await fixture.Window.OpenGroupPresetAsync(preset);
        var opened = fixture.Model.SelectedTab!.Group!;
        Assert.NotSame(original, opened);
        Assert.False(opened.IsCollapsed);
        Assert.Equal(5, fixture.Model.Tabs.Count);
        Assert.Equal(originalTabs, fixture.Model.Tabs.Take(2));
        Assert.Equal(new[] { root.First, root.Second, root.Third }, fixture.Model.Members(opened).Select(t => t.FileList.CurrentPath));
        Assert.Equal(PaneLayout.TwoColumns, fixture.Model.PaneLayout);
        Assert.Equal(new[] { root.Third, root.First }, fixture.Model.VisiblePanes.Select(t => t.FileList.CurrentPath));
        Assert.Equal(root.Third, fixture.Model.FileList.CurrentPath);
        fixture.Model.ActivateGroup(original);
        Assert.Equal(PaneLayout.TwoRows, fixture.Model.PaneLayout);
        Assert.Equal(originalPanes, fixture.Model.VisiblePanes);
        Assert.Same(originalSelected, fixture.Model.SelectedTab);
        fixture.Model.ActivateGroup(opened);
        Assert.Equal(PaneLayout.TwoColumns, fixture.Model.PaneLayout);
        Assert.Equal(root.Third, fixture.Model.FileList.CurrentPath);
        await fixture.Model.FileList.NavigateToAsync(root.Second);
        fixture.Model.ToggleGroup(opened);
        fixture.Model.ActivateGroup(original);
        await fixture.Window.OpenGroupPresetAsync(preset);
        Assert.Equal(2, fixture.Model.Groups.Count);
        Assert.Equal(5, fixture.Model.Tabs.Count);
        Assert.Same(opened, fixture.Model.SelectedTab!.Group);
        Assert.False(opened.IsCollapsed);
        Assert.Equal(root.Second, fixture.Model.FileList.CurrentPath);
        Assert.Equal(root.Third, preset.Session.Paths[2]);
        await fixture.Window.CloseGroupAsync(opened);
        await fixture.Window.OpenGroupPresetAsync(preset);
        Assert.Equal(2, fixture.Model.Groups.Count);
        Assert.NotSame(opened, fixture.Model.SelectedTab!.Group);
        Assert.Equal(root.Third, fixture.Model.FileList.CurrentPath);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task RestoredPresetGroupIsReusedAfterRenamingAndNavigation()
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        using var access = new GrantedFeatureAccess();
        await using var first = await CreateFixture(root);
        var preset = root.Preset();
        await first.Window.OpenGroupPresetAsync(preset);
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<FolderTabSession>(
            System.Text.Json.JsonSerializer.Serialize(first.Window.CaptureFolderTabs()))!;
        await using var restored = await CreateFixture(root);
        await restored.Window.RestoreFolderTabSessionAsync(snapshot);
        var group = Assert.Single(restored.Model.Groups);
        Assert.Equal(preset.Id, group.PresetId);
        group.Name = "Changed group name";
        await restored.Model.FileList.NavigateToAsync(root.Second);
        var tabs = restored.Model.Tabs.ToArray();
        await restored.Window.OpenGroupPresetAsync(preset with { Name = "Renamed preset" });
        Assert.Same(group, Assert.Single(restored.Model.Groups));
        Assert.Equal(tabs, restored.Model.Tabs);
        Assert.Equal(root.Second, restored.Model.FileList.CurrentPath);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ToolbarPresetOpensWithOneClickEvenWhenAutomaticRecoveryIsDisabled()
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        await using var fixture = await CreateFixture(root);
        var settings = App.Services.GetRequiredService<ISettingsService>();
        settings.Set(FolderTabSession.EnabledKey, false);
        settings.Set(FolderTabSession.SessionKey, "previous session");
        TabGroupPresets.Save(settings, root.Preset());
        var button = fixture.Window.FindControl<Button>("GroupPresetsButton")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = (ContextMenu)typeof(MainWindow).GetField("_tabContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Window)!;
        var item = Assert.IsType<MenuItem>(menu.Items[0]);
        Assert.Empty(item.Items);
        Assert.Contains("Work", item.Header!.ToString());
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => fixture.Model.Tabs.Count == 4 && !fixture.Window.IsRestoringFolderTabs);
        Assert.Single(fixture.Model.Groups);
        Assert.False(settings.Get(FolderTabSession.EnabledKey, true));
        Assert.Equal("previous session", settings.Get(FolderTabSession.SessionKey));
    }

    [AvaloniaFact(Explicit = true)]
    public async Task PresetCanOpenIndependentlyInTwoWindows()
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        using var access = new GrantedFeatureAccess();
        await using var first = await CreateFixture(root);
        await using var second = await CreateFixture(root);
        var preset = root.Preset();
        await first.Window.OpenGroupPresetAsync(preset);
        Assert.Single(second.Model.Tabs);
        Assert.Empty(second.Model.Groups);
        await second.Window.OpenGroupPresetAsync(preset);
        Assert.NotEqual(Assert.Single(first.Model.Groups).Id, Assert.Single(second.Model.Groups).Id);
        await second.Model.FileList.NavigateToAsync(root.First);
        Assert.Equal(root.Third, first.Model.FileList.CurrentPath);
        Assert.Equal(4, first.Model.Tabs.Count);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task UnavailableFoldersDoNotCreateAnEmptyGroupOrModifyTheSavedPreset()
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        using var access = new GrantedFeatureAccess();
        await using var fixture = await CreateFixture(root);
        var preset = root.Preset();
        var settings = App.Services.GetRequiredService<ISettingsService>();
        TabGroupPresets.Save(settings, preset);
        var stored = settings.Get(TabGroupPresets.SettingKey);
        Directory.Delete(root.Third);
        await fixture.Window.OpenGroupPresetAsync(preset);
        Assert.Equal(3, fixture.Model.Tabs.Count);
        Assert.Equal(2, fixture.Model.VisiblePanes.Count);
        Assert.Contains(fixture.Model.SelectedTab!, fixture.Model.VisiblePanes);
        Directory.Delete(root.First);
        Directory.Delete(root.Second);
        var selected = fixture.Model.SelectedTab;
        await fixture.Window.OpenGroupPresetAsync(preset);
        Assert.Equal(3, fixture.Model.Tabs.Count);
        Assert.Single(fixture.Model.Groups);
        Assert.Same(selected, fixture.Model.SelectedTab);
        Assert.Equal(stored, settings.Get(TabGroupPresets.SettingKey));
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ConcurrentStartupAndPresetLoadsShareLimitAndRespectTabClosureAndUserFocus()
    {
        using var root = new Directories();
        using var theme = new FastListTestTheme();
        var files = new FakeFileService(root.Path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var countGate = new object();
        var active = 0;
        var maximum = 0;
        async Task Enumerate(string _, CancellationToken token)
        {
            lock (countGate)
            {
                maximum = Math.Max(maximum, ++active);
                if (active == 2) entered.TrySetResult();
            }
            try { await release.Task.WaitAsync(token); }
            finally { lock (countGate) active--; }
        }
        files.BeforeEnumerate = Enumerate;
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(files)));
        fixture.Files.BeforeEnumerate = Enumerate;
        var startup = fixture.Window.RestoreFolderTabSessionAsync(new([root.First, root.Second, root.Third], 0));
        Task opening = Task.CompletedTask;
        ExplorerTabViewModel? original = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            original = fixture.Model.Tabs[0];
            opening = fixture.Window.OpenGroupPresetAsync(root.Preset());
            await WaitUntil(() => fixture.Model.Tabs.Count == 6);
            // A double click while the first invocation is loading does not create another copy.
            await fixture.Window.OpenGroupPresetAsync(root.Preset());
            Assert.Equal(6, fixture.Model.Tabs.Count);
            var closed = fixture.Model.Tabs[^1];
            await (Task)typeof(MainWindow).GetMethod("CloseTabCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Window, [closed])!;
            fixture.Model.SelectedTab = original;
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(startup, opening);
        Assert.Equal(2, maximum);
        Assert.Equal(5, fixture.Model.Tabs.Count);
        Assert.Same(original, fixture.Model.SelectedTab);
        Assert.False(fixture.Window.IsRestoringFolderTabs);
        Assert.All(fixture.Model.Tabs, tab => Assert.False(tab.FileList.IsLoading));
    }

    private static Task<TabCacheFixture> CreateFixture(Directories root)
        => TabCacheFixture.CreateAsync(entries: 0, configureServices: services =>
            services.AddTransient<FileListViewModel>(_ => CreateViewModel(new FakeFileService(root.Path))));

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Directories : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "group-preset-" + Guid.NewGuid().ToString("N"));
        public string First => System.IO.Path.Combine(Path, "first");
        public string Second => System.IO.Path.Combine(Path, "second");
        public string Third => System.IO.Path.Combine(Path, "third");
        public Directories() { Directory.CreateDirectory(First); Directory.CreateDirectory(Second); Directory.CreateDirectory(Third); }
        public TabGroupPreset Preset() => TabGroupPreset.Capture("Work", [First, Second, Third],
            new(Guid.NewGuid(), "Source", 2, true, [0, 1, 2], PaneLayout.TwoColumns, [2, 0], 2, 0))!;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
