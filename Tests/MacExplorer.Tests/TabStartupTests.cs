using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "TabStartup")]
public sealed class TabStartupViewModelTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    [Trait("Area", "Window")]
    public async Task WindowMovesAndResizeDoNotPersistOrRestorePlacement()
    {
        var ct = TestContext.Current.CancellationToken;
        using var theme = new FastListTestTheme();
        var settings = new StartupSettings();
        settings.Set("window.primary.normal_width", 1444d);
        settings.Set("window.primary.position_x", 9000);
        settings.Set("window.primary.is_maximized", true);
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddSingleton<ISettingsService>(settings));
        var placement = new WindowPlacementService();
        placement.Prepare(fixture.Window, true, null);
        Assert.Equal(1280, fixture.Window.Width);
        Assert.Equal(WindowState.Normal, fixture.Window.WindowState);
        settings.Writes.Clear();
        fixture.Window.Position = new PixelPoint(80, 90);
        fixture.Window.Width = 1200;
        fixture.Window.Height = 740;
        await Task.Delay(400, ct);
        Dispatcher.UIThread.RunJobs();
        fixture.Window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(settings.Writes, key => key.StartsWith("window.primary."));
    }

    [AvaloniaFact(Explicit = true)]
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

}

public abstract partial class FileListViewModelTestBase
{
    protected sealed class StartupSettings : ISettingsService
    {
        public event Action<string>? SettingChanged { add { } remove { } }
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
