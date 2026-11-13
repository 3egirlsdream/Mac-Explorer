using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.UnitTests;

[Trait("Kind", "Unit")]
[Trait("Area", "GroupPresets")]
public sealed class TabGroupPresetTests
{
    [Fact]
    public void PresetAndStartupRoundTripsRemapIndependentViewStatesForDuplicateFolders()
    {
        var settings = new MemorySettings();
        var preferences = new DirectoryPreferences(SortField.Size, false, SortPriority.Folders,
            FileListFilterState.Empty, [300, 160, 100, 230], true, false, true);
        var first = new FolderTabViewState(ViewMode.Grid, GroupField.Type, preferences);
        var hidden = new FolderTabViewState(ViewMode.Tree, GroupField.Size, preferences with { Sort = SortField.Name });
        var group = Group([1, 2, 3, 4]) with { PaneIndices = [1, 2], SelectedIndex = 1 };
        var preset = TabGroupPreset.Capture("Work", ["/outside", "/same", "/missing", "home://", "/same"], group,
            viewStates: new() { [0] = first, [1] = first, [2] = first, [3] = first, [4] = hidden })!;
        TabGroupPresets.Save(settings, preset);
        var opened = Assert.Single(TabGroupPresets.Read(settings)).CreateSession(path => path != "/missing")!;
        Assert.Equal(new[] { "/same", "/same" }, opened.Paths);
        Assert.Equal(2, opened.ViewStates.Count);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(opened.ViewStates[0]));
        Assert.Equal(JsonSerializer.Serialize(hidden), JsonSerializer.Serialize(opened.ViewStates[1]));
        Assert.Equal(3, preset.Session.ViewStates.Count);

        settings.Set(FolderTabSession.EnabledKey, true);
        FolderWindowSession.Capture([preset.Session], 0).Save(settings);
        var startup = Assert.Single(FolderWindowSession.Read(settings, path => path != "/missing")!.Windows);
        Assert.Equal(JsonSerializer.Serialize(opened.ViewStates), JsonSerializer.Serialize(startup.ViewStates));
        preset.Session.Save(settings);
        Assert.Equal(JsonSerializer.Serialize(opened.ViewStates),
            JsonSerializer.Serialize(FolderTabSession.Read(settings, path => path != "/missing")!.ViewStates));
    }

    [Fact]
    public void LegacyPreferencesSurvivePresetNormalizationWithoutInventingViewStates()
    {
        var settings = new MemorySettings();
        var preferences = new DirectoryPreferences(SortField.Modified, false, SortPriority.None,
            FileListFilterState.Empty, [300, 160, 100, 230], false, true, false);
        var preset = TabGroupPreset.Capture("Old", ["/a"], Group([0]), preferences: new() { [0] = preferences })!;
        settings.Set(TabGroupPresets.SettingKey, JsonSerializer.Serialize(new[] { preset }).Replace(
            ",\"ViewStates\":{}", string.Empty));
        var opened = Assert.Single(TabGroupPresets.Read(settings)).CreateSession(_ => true)!;
        Assert.Empty(opened.ViewStates);
        Assert.Equal(JsonSerializer.Serialize(preferences), JsonSerializer.Serialize(opened.Preferences[0]));
    }

    private static FolderTabGroupSession Group(int[]? members = null) => new(Guid.NewGuid(), "Source", 2, true,
        members ?? [0, 1, 2], PaneLayout.TwoColumns, [2, 0], 2, 0);

    [Fact]
    public void CapturingInactiveGroupKeepsOnlyItsFoldersAndRemapsItsOwnFocus()
    {
        var group = Group([1, 2, 3, 4]) with { PaneIndices = [4, 2], SelectedIndex = 4, ActivePaneSlotIndex = 0 };
        var preset = TabGroupPreset.Capture("  Work  ", ["/other-group", "/a", "/b", "home://", "/a", "/outside"], group)!;
        Assert.Equal("Work", preset.Name);
        Assert.Equal(new[] { "/a", "/b", "/a" }, preset.Session.Paths);
        Assert.Equal(2, preset.Session.SelectedIndex);
        var saved = Assert.Single(preset.Session.Groups);
        Assert.Equal(new[] { 0, 1, 2 }, saved.TabIndices);
        Assert.Equal(new[] { 2, 1 }, saved.PaneIndices);
        Assert.Equal(2, saved.SelectedIndex);
        Assert.Equal(PaneLayout.TwoColumns, saved.Layout);
        Assert.Equal(2, saved.ColorIndex);
    }

    [Theory]
    [InlineData("", "/folder")]
    [InlineData("Work", "home://")]
    [InlineData("Work", "relative")]
    public void EmptyNamesOrGroupsWithoutFoldersCannotBeSaved(string name, string path)
        => Assert.Null(TabGroupPreset.Capture(name, [path], Group([0])));

    [Fact]
    public void EveryOpenCreatesAnExpandedIndependentGroupAndDoesNotRewriteTheSnapshot()
    {
        var preset = TabGroupPreset.Capture("Work", ["/a", "/b", "/c"], Group())!;
        var first = preset.CreateSession(_ => true)!;
        var second = preset.CreateSession(_ => true)!;
        Assert.NotEqual(first.Groups[0].Id, second.Groups[0].Id);
        Assert.NotEqual(preset.Session.Groups[0].Id, first.Groups[0].Id);
        Assert.False(first.Groups[0].IsCollapsed);
        Assert.Equal(preset.Id, first.Groups[0].PresetId);
        Assert.Equal(preset.Id, second.Groups[0].PresetId);
        Assert.True(preset.Session.Groups[0].IsCollapsed);
        Assert.Equal("Work", first.Groups[0].Name);
        first.Paths[0] = "/changed";
        first.Groups[0].PaneIndices[0] = 1;
        Assert.Equal("/a", preset.Session.Paths[0]);
        Assert.Equal(2, preset.Session.Groups[0].PaneIndices[0]);
        Assert.Equal("/a", second.Paths[0]);
    }

    [Fact]
    public void MissingOrDeniedFoldersShrinkTheOpenedLayoutWithoutDestroyingTheSavedTemplate()
    {
        var preset = TabGroupPreset.Capture("Work", ["/missing", "/valid", "/denied"], Group())!;
        var opened = preset.CreateSession(path => path switch
        {
            "/missing" => false,
            "/denied" => throw new UnauthorizedAccessException(),
            _ => true
        })!;
        Assert.Equal(new[] { "/valid" }, opened.Paths);
        Assert.Equal(0, opened.SelectedIndex);
        Assert.Equal(PaneLayout.Single, opened.Groups[0].Layout);
        Assert.Equal(new[] { 0 }, opened.Groups[0].PaneIndices);
        Assert.Null(preset.CreateSession(_ => false));
        Assert.Equal(3, preset.CreateSession(_ => true)!.Paths.Length);
    }

    [Fact]
    public void RemovedActiveFolderFallsBackToTheSurvivingVisiblePane()
    {
        var preset = TabGroupPreset.Capture("Work", ["/hidden", "/visible", "/missing"],
            Group() with { PaneIndices = [2, 1] })!;
        var opened = preset.CreateSession(path => path != "/missing")!;
        Assert.Equal(1, opened.SelectedIndex);
        Assert.Equal(1, opened.Groups[0].SelectedIndex);
        Assert.Equal(new[] { 1, 0 }, opened.Groups[0].PaneIndices);
    }

    [Fact]
    public void SaveReplaceRenameAndDeleteLeaveOtherPresetsAndStartupSettingsUntouched()
    {
        var settings = new MemorySettings();
        settings.Set(FolderTabSession.EnabledKey, false);
        settings.Set(FolderTabSession.SessionKey, "last session");
        var first = TabGroupPreset.Capture("First", ["/a", "/b", "/c"], Group())!;
        var second = TabGroupPreset.Capture("Second", ["/d", "/e", "/f"], Group())!;
        TabGroupPresets.Save(settings, first);
        TabGroupPresets.Save(settings, second);
        TabGroupPresets.Save(settings, first with { Name = "Renamed" });
        Assert.Equal(new[] { "Renamed", "Second" }, TabGroupPresets.Read(settings).Select(p => p.Name));
        Assert.Equal("Renamed", TabGroupPresets.Read(settings)[0].CreateSession(_ => true)!.Groups[0].Name);
        TabGroupPresets.Delete(settings, first.Id);
        Assert.Equal(second.Id, Assert.Single(TabGroupPresets.Read(settings)).Id);
        Assert.False(settings.Get(FolderTabSession.EnabledKey, true));
        Assert.Equal("last session", settings.Get(FolderTabSession.SessionKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupAlwaysUsesLastSessionWhileManualPresetsIgnoreTheRestoreSwitch(bool enabled)
    {
        var settings = new MemorySettings();
        settings.Set(FolderTabSession.EnabledKey, true);
        new FolderWindowSession([new(["/last-window-one"], 0), new(["/last-window-two"], 0)], 1).Save(settings);
        var preset = TabGroupPreset.Capture("Preset", ["/template"], Group([0]))!;
        TabGroupPresets.Save(settings, preset);
        settings.Set(FolderTabSession.EnabledKey, enabled);
        var startup = FolderWindowSession.Read(settings, _ => true);
        if (enabled)
        {
            Assert.Equal(2, startup!.Windows.Length);
            Assert.Equal("/last-window-two", Assert.Single(startup.Windows[startup.SelectedWindowIndex].Paths));
        }
        else Assert.Null(startup);
        Assert.Equal("/template", Assert.Single(Assert.Single(TabGroupPresets.Read(settings)).CreateSession(_ => true)!.Paths));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null,{}]")]
    public void MalformedStorageHasNoEffectOnTheCurrentSession(string json)
    {
        var settings = new MemorySettings();
        settings.Set(TabGroupPresets.SettingKey, json);
        Assert.Empty(TabGroupPresets.Read(settings));
        Assert.Equal(json, settings.Get(TabGroupPresets.SettingKey));
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = [];
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T fallback) => Get(key) is { } value ? (T)Convert.ChangeType(value, typeof(T)) : fallback;
        public void Set(string key, string value) { _values[key] = value; SettingChanged?.Invoke(key); }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }
}
