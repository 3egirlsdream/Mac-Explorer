using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "GroupPresets")]
[Trait("Area", "TabStartup")]
public sealed class FolderTabViewStateTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingAndRevisitingRestoredTabDoesNotOverwriteDirectoryPreferences(bool legacy)
    {
        using var root = new TestDirectory();
        var files = Files(root.Path);
        using var first = CreateViewModel(files, navigation: new NavigationViewModel(files));
        using var second = CreateViewModel(files, navigation: new NavigationViewModel(files));
        var directories = new DirectoryPreferencesStub();
        first.UseDirectoryPreferences(directories); second.UseDirectoryPreferences(directories);
        var original = first.CaptureDirectoryPreferences();
        directories.Stored[root.Path] = original;
        var firstState = new FolderTabViewState(ViewMode.Grid, GroupField.Size,
            original with { Sort = SortField.Size, Ascending = false, PreviewPane = true });
        var secondState = firstState with { Preferences = original with { Sort = SortField.Type, InfoPanel = true } };
        first.PrepareTabViewState(root.Path, legacy ? null : firstState, legacy ? firstState.Preferences : null);
        second.PrepareTabViewState(root.Path, secondState);
        await first.NavigateToAsync(root.Path); await second.NavigateToAsync(root.Path);
        var firstPreferences = first.CaptureDirectoryPreferences();
        var secondPreferences = second.CaptureDirectoryPreferences();
        var next = Directory.CreateDirectory(Path.Combine(root.Path, "next")).FullName;
        await first.NavigateToAsync(next); await first.NavigateBackAsync();
        Assert.Equal(JsonSerializer.Serialize(firstPreferences), JsonSerializer.Serialize(first.CaptureDirectoryPreferences()));
        await second.NavigateToAsync(next); await second.NavigateBackAsync();
        Assert.Equal(JsonSerializer.Serialize(secondPreferences), JsonSerializer.Serialize(second.CaptureDirectoryPreferences()));
        Assert.Empty(directories.Saves);
        Assert.Same(original, directories.Stored[root.Path]);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ExplicitPreferenceChangesAfterRestoreAreSavedWithoutNavigationWrites()
    {
        using var root = new TestDirectory();
        using var vm = CreateViewModel(Files(root.Path));
        var directories = new DirectoryPreferencesStub(); vm.UseDirectoryPreferences(directories);
        vm.PrepareTabViewState(root.Path, new(ViewMode.Grid, GroupField.Size, vm.CaptureDirectoryPreferences()));
        await vm.NavigateToAsync(root.Path);
        vm.SetSort(SortField.Size, false);
        vm.FileNameFilter = "alpha";
        vm.ColumnLayoutService.Preview(FileListColumn.Name, 360);
        vm.ColumnLayoutService.Commit(FileListColumn.Name);
        vm.IsInfoPanelVisible = !vm.IsInfoPanelVisible;
        var expected = vm.CaptureDirectoryPreferences();
        Assert.NotEmpty(directories.Saves);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(directories.Stored[root.Path]));
        var count = directories.Saves.Count;
        await vm.NavigateToAsync(Directory.CreateDirectory(Path.Combine(root.Path, "next")).FullName);
        Assert.Equal(count, directories.Saves.Count);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(directories.Stored[root.Path]));
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(ViewMode.List, GroupField.None)]
    [InlineData(ViewMode.Grid, GroupField.Size)]
    [InlineData(ViewMode.Tree, GroupField.Type)]
    public async Task RestoresBeforeEnumerationWithoutWritingGlobalOrDirectoryPreferences(ViewMode mode, GroupField group)
    {
        using var root = new TestDirectory();
        var settings = new StartupSettings();
        var files = Files(root.Path);
        using var vm = CreateViewModel(files, sortFilter: new SortFilterViewModel(settings), settingsService: settings);
        var directories = new DirectoryPreferencesStub();
        vm.UseDirectoryPreferences(directories);
        var state = new FolderTabViewState(mode, group, new(SortField.Size, false, SortPriority.Folders,
            FileListFilterState.Empty with { FileName = "a" }, [320, 180, 120, 240], false, false, false));
        vm.PrepareTabViewState(files.HomeDirectory, state);
        AssertState(state, vm.CaptureTabViewState());
        files.BeforeEnumerate = (_, _) =>
        {
            AssertState(state, vm);
            Assert.Empty(directories.Saves);
            return Task.CompletedTask;
        };
        settings.Writes.Clear();
        await vm.NavigateToAsync(files.HomeDirectory);
        Assert.Equal(1, files.EnumerateDirectoryCallCount);
        AssertState(state, vm);
        Assert.Equal(new[] { "Gamma", "alpha.txt", "beta.txt" }, vm.Entries.Select(e => e.Name));
        Assert.Equal(group != GroupField.None, vm.Groups.Count > 0);
        if (mode == ViewMode.Tree) Assert.Equal(3, vm.TreeRows.Count);
        Assert.Empty(directories.Saves);
        Assert.DoesNotContain(settings.Writes, key => key is "ViewMode" or "GroupField" or "SortField"
            or "SortAscending" or "SortPriority" or "IsPreviewPaneVisible" or "IsMetadataPanelVisible" or "IsInfoPanelVisible"
            || key.StartsWith("file_list.column.", StringComparison.Ordinal));
        Assert.Equal(ViewMode.Grid, settings.Get("ViewMode", ViewMode.List));
        Assert.Equal(GroupField.Type, settings.Get("GroupField", GroupField.None));
        files.BeforeEnumerate = null;
        await vm.RefreshAsync();
        AssertState(state, vm);
        vm.SetViewMode(mode == ViewMode.Grid ? ViewMode.List : ViewMode.Grid);
        Assert.Contains("ViewMode", settings.Writes);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task LegacySnapshotRestoresExistingFieldsAndKeepsCurrentViewAndGrouping()
    {
        using var root = new TestDirectory();
        var files = Files(root.Path);
        using var vm = CreateViewModel(files, sortFilter: new SortFilterViewModel { ViewMode = ViewMode.Tree, GroupField = GroupField.Type });
        var preferences = new DirectoryPreferences(SortField.Name, true, SortPriority.Files,
            FileListFilterState.Empty, [330, 180, 120, 240], false, true, false);
        vm.PrepareTabViewState(files.HomeDirectory, null, preferences);
        await vm.NavigateToAsync(files.HomeDirectory);
        AssertState(new(ViewMode.Tree, GroupField.Type, preferences), vm);
        Assert.Equal(new[] { "alpha.txt", "beta.txt", "Gamma" }, vm.Entries.Select(e => e.Name));
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ReopeningPresetRestoresDuplicateFolderPanesAndHiddenTabIndependently()
    {
        using var root = new TestDirectory();
        using var theme = new FastListTestTheme();
        var files = Files(root.Path);
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(files)));
        await fixture.Model.FileList.NavigateToAsync(root.Path);
        await fixture.Window.OpenNewTabAsync(root.Path);
        await fixture.Window.OpenNewTabAsync(root.Path);
        var model = fixture.Model;
        var tabs = model.Tabs.ToArray();
        var group = model.CreateGroup(tabs);
        model.SetPaneLayout(PaneLayout.TwoColumns);
        Assert.Equal(2, model.VisiblePanes.Count);
        for (var i = 0; i < tabs.Length; i++)
        {
            var vm = tabs[i].FileList;
            vm.SetViewMode((ViewMode)i);
            vm.GroupField = (GroupField)i;
            vm.SetSort((SortField)i, i != 0);
            vm.SortPriority = (SortPriority)i;
            vm.IsPreviewPaneVisible = i == 0;
            vm.IsMetadataPanelVisible = i == 1;
            vm.IsInfoPanelVisible = i == 2;
            vm.ColumnLayoutService.Preview(FileListColumn.Name, 300 + i * 20);
        }
        var captured = fixture.Window.CaptureFolderTabs();
        var preset = TabGroupPreset.Capture("Work", captured.Paths, Assert.Single(captured.Groups), viewStates: captured.ViewStates)!;
        var settings = new StartupSettings();
        TabGroupPresets.Save(settings, preset);
        await fixture.Window.CloseGroupAsync(group);
        var outside = model.SelectedTab!;
        await fixture.Window.OpenGroupPresetAsync(Assert.Single(TabGroupPresets.Read(settings)));
        var restoredGroup = Assert.Single(model.Groups);
        var restoredTabs = model.Members(restoredGroup).ToArray();
        Assert.Equal(3, restoredTabs.Length);
        Assert.Equal(2, model.VisiblePanes.Count);
        for (var i = 0; i < restoredTabs.Length; i++)
        {
            Assert.Equal(root.Path, restoredTabs[i].FileList.CurrentPath);
            AssertState(captured.ViewStates[i], restoredTabs[i].FileList);
        }
        model.SelectedTab = outside;
        model.ActivateGroup(restoredGroup);
        foreach (var tab in restoredTabs) model.SelectedTab = tab;
        Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < restoredTabs.Length; i++) AssertState(captured.ViewStates[i], restoredTabs[i].FileList);
        var unchanged = restoredTabs[1].FileList.CaptureTabViewState();
        restoredTabs[0].FileList.SetViewMode(ViewMode.Tree);
        restoredTabs[0].FileList.GroupField = GroupField.Modified;
        restoredTabs[0].FileList.SetSort(SortField.Type, false);
        AssertState(unchanged, restoredTabs[1].FileList);
        AssertState(captured.ViewStates[0], Assert.Single(TabGroupPresets.Read(settings)).Session.ViewStates[0]);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task StartupSessionRestoresCompleteStateEvenWhenReusingLoadedSamePathTab()
    {
        using var root = new TestDirectory();
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddTransient<FileListViewModel>(_ => CreateViewModel(Files(root.Path))));
        fixture.Files.Seed(new() { FullPath = Path.Combine(root.Path, "alpha.txt"), Name = "alpha.txt" });
        await fixture.Model.FileList.NavigateToAsync(root.Path);
        Assert.NotEmpty(fixture.Model.FileList.Entries);
        var preferences = fixture.Model.FileList.CaptureDirectoryPreferences();
        var first = new FolderTabViewState(ViewMode.Tree, GroupField.Size, preferences with { Sort = SortField.Size });
        var second = new FolderTabViewState(ViewMode.Grid, GroupField.Type, preferences with { Ascending = false });
        var settings = new StartupSettings();
        settings.Set(FolderTabSession.EnabledKey, true);
        FolderWindowSession.Capture([FolderTabSession.Capture([root.Path, root.Path], 1,
            viewStates: new() { [0] = first, [1] = second })], 0).Save(settings);
        var session = Assert.Single(FolderWindowSession.Read(settings, _ => true)!.Windows);
        await fixture.Window.RestoreFolderTabSessionAsync(session);
        AssertState(first, fixture.Model.Tabs[0].FileList);
        AssertState(second, fixture.Model.Tabs[1].FileList);
        Assert.Same(fixture.Model.Tabs[1], fixture.Model.SelectedTab);
    }

    private static void AssertState(FolderTabViewState expected, FolderTabViewState actual)
        => Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static void AssertState(FolderTabViewState expected, FileListViewModel actual)
        => AssertState(expected, new FolderTabViewState(actual.ViewMode, actual.GroupField, actual.CaptureDirectoryPreferences()));

    private static FakeFileService Files(string path)
    {
        var files = new FakeFileService(path);
        files.Seed(new() { FullPath = Path.Combine(path, "alpha.txt"), Name = "alpha.txt", Extension = ".txt", Size = 20 });
        files.Seed(new() { FullPath = Path.Combine(path, "beta.txt"), Name = "beta.txt", Extension = ".txt", Size = 10 });
        files.Seed(new() { FullPath = Path.Combine(path, "Gamma"), Name = "Gamma", IsDirectory = true });
        return files;
    }

    private sealed class DirectoryPreferencesStub : IDirectoryPreferencesService
    {
        internal List<DirectoryPreferences> Saves { get; } = [];
        internal Dictionary<string, DirectoryPreferences> Stored { get; } = [];
        public Task<DirectoryPreferences?> ReadAsync(string path) => Task.FromResult(Stored.GetValueOrDefault(path));
        public Task SaveAsync(string path, DirectoryPreferences preferences)
        { Saves.Add(preferences); Stored[path] = preferences; return Task.CompletedTask; }
        public Task RemoveAsync(string path) => Task.CompletedTask;
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "tab-view-state-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
