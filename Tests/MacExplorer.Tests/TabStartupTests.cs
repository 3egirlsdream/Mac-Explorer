using Avalonia.Headless.XUnit;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public void NewTabRestoresPreferencesWithoutWritingThemBack()
    {
        var settings = new StartupSettings();
        var sort = new SortFilterViewModel(settings);
        using var fileList = CreateViewModel(new FakeFileService("/tmp/FKFinderTests"),
            sortFilter: sort, settingsService: settings);

        Assert.Equal(ViewMode.Grid, sort.ViewMode);
        Assert.Equal(SortField.Size, sort.SortField);
        Assert.False(sort.SortAscending);
        Assert.Equal(GroupField.Type, sort.GroupField);
        Assert.False(sort.HideSystemFiles);
        Assert.False(sort.HideDotFiles);
        Assert.False(sort.HideDotFolders);
        Assert.True(fileList.IsPreviewPaneVisible);
        Assert.True(fileList.IsMetadataPanelVisible);
        Assert.True(fileList.IsInfoPanelVisible);
        Assert.Empty(settings.Writes);

        sort.ViewMode = ViewMode.List;
        fileList.IsInfoPanelVisible = false;
        Assert.Equal(new[] { "ViewMode", "IsInfoPanelVisible" }, settings.Writes);
    }

    private sealed class StartupSettings : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new()
        {
            ["ViewMode"] = ViewMode.Grid,
            ["SortField"] = SortField.Size,
            ["SortAscending"] = false,
            ["GroupField"] = GroupField.Type,
            ["HideSystemFiles"] = false,
            ["HideDotFiles"] = false,
            ["HideDotFolders"] = false,
            ["IsPreviewPaneVisible"] = true,
            ["IsMetadataPanelVisible"] = true,
            ["IsInfoPanelVisible"] = true
        };

        public List<string> Writes { get; } = [];
        public string? Get(string key) => _values.GetValueOrDefault(key)?.ToString();
        public T Get<T>(string key, T defaultValue)
            => _values.TryGetValue(key, out var value) ? (T)value : defaultValue;
        public void Set(string key, string value) => Set<string>(key, value);
        public void Set<T>(string key, T value)
        {
            Writes.Add(key);
            _values[key] = value!;
        }
        public Dictionary<string, string> GetAll()
            => _values.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()!);
    }
}
