using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileList")]
public sealed class FileListColumnLayoutServiceTests
{
    [Fact]
    public void DefaultsMatchFinderStyleLayout()
    {
        var service = new FileListColumnLayoutService();

        Assert.Equal(new FileListColumnWidths(420, 170, 110, 230), service.PreferredWidths);
    }

    [Fact]
    public void WideLayoutKeepsExplicitNameWidth()
    {
        var preferred = new FileListColumnWidths(500, 180, 120, 130);

        var effective = FileListColumnLayoutService.CalculateEffective(preferred, 1200);

        Assert.Equal(preferred, effective);
        Assert.Equal(930, effective.Total);
    }

    [Fact]
    public void DefaultNameFillsWideWindowsAndGivesSpaceBackToPreview()
    {
        var preferred = FileListColumnLayoutService.Defaults;
        var wide = FileListColumnLayoutService.CalculateEffective(preferred, 1300);
        var preview = FileListColumnLayoutService.CalculateEffective(preferred, 900);

        Assert.Equal(790, wide.Name);
        Assert.Equal(1300, wide.Total);
        Assert.Equal(390, preview.Name);
        Assert.Equal(preferred.Modified, wide.Modified);
        Assert.Equal(preferred.Size, wide.Size);
        Assert.Equal(preferred.Type, wide.Type);
    }

    [Theory]
    [InlineData((int)FileListColumn.Name, 440, 620)]
    [InlineData((int)FileListColumn.Modified, 145, 220)]
    [InlineData((int)FileListColumn.Size, 90, 145)]
    [InlineData((int)FileListColumn.Type, 190, 280)]
    public void ManualResizeMovesItsDividerInBothDirections(int columnIndex, double narrow, double wide)
    {
        var column = (FileListColumn)columnIndex;
        var service = new FileListColumnLayoutService();
        const double available = 1050;
        var initial = FileListColumnLayoutService.CalculateEffective(service.PreferredWidths, available);
        Assert.True(narrow < initial[column]);
        Assert.True(wide > initial[column]);

        foreach (var width in new[] { narrow, wide })
        {
            service.Preview(column, width);
            var effective = FileListColumnLayoutService.CalculateEffective(service.PreferredWidths, available,
                service.HiddenColumns, service.ResizedColumns);
            Assert.Equal(initial.With(column, width), effective);
        }
    }

    [Fact]
    public void NarrowLayoutShrinksNameThenTypeThenSize()
    {
        var effective = FileListColumnLayoutService.CalculateEffective(
            FileListColumnLayoutService.Defaults,
            560);

        Assert.Equal(new FileListColumnWidths(220, 170, 90, 80), effective);
        Assert.Equal(560, effective.Total);
    }

    [Fact]
    public void WidthBelowMinimumNeverProducesNegativeContentWidth()
    {
        var effective = FileListColumnLayoutService.CalculateEffective(
            FileListColumnLayoutService.Defaults,
            400);

        Assert.Equal(FileListColumnLayoutService.Minimums, effective);
        Assert.Equal(520, effective.Total);
    }

    [Fact]
    public void HiddenColumnsDoNotUseSpaceAndRestoreTheirPreferredWidths()
    {
        var service = new FileListColumnLayoutService();
        service.Preview(FileListColumn.Type, 270);
        service.SetVisible(FileListColumn.Modified, false);
        service.SetVisible(FileListColumn.Type, false);
        service.SetVisible(FileListColumn.Name, false);

        Assert.Equal(FileListColumns.Modified | FileListColumns.Type, service.HiddenColumns);
        var effective = FileListColumnLayoutService.CalculateEffective(service.PreferredWidths, 900,
            service.HiddenColumns, service.ResizedColumns);
        Assert.Equal(new FileListColumnWidths(790, 0, 110, 0), effective);

        service.SetVisible(FileListColumn.Type, true);
        var restored = FileListColumnLayoutService.CalculateEffective(service.PreferredWidths, 900,
            service.HiddenColumns, service.ResizedColumns);
        Assert.Equal(270, restored.Type);
        Assert.Equal(0, restored.Modified);
    }

    [Fact]
    public void ResizeHonorsMinimumAndDefaultWidthCanBeExplicit()
    {
        var service = new FileListColumnLayoutService();
        service.Preview(FileListColumn.Size, 4);
        Assert.Equal(FileListColumnLayoutService.Minimums.Size, service.PreferredWidths.Size);
        service.Reset(FileListColumn.Size);

        service.Preview(FileListColumn.Name, FileListColumnLayoutService.Defaults.Name);
        var restored = new FileListColumnLayoutService();
        restored.Restore([420, 170, 110, 230], FileListColumns.None, service.ResizedColumnsOverride);
        Assert.Equal(420, FileListColumnLayoutService.CalculateEffective(restored.PreferredWidths, 1200,
            restored.HiddenColumns, restored.ResizedColumns).Name);
        restored.Reset(FileListColumn.Name);
        Assert.Equal(690, FileListColumnLayoutService.CalculateEffective(restored.PreferredWidths, 1200,
            restored.HiddenColumns, restored.ResizedColumns).Name);
    }

    [Fact]
    public void SavedWidthsAreRestoredClampedAndResetPersistsDefault()
    {
        var settings = new MemorySettingsService();
        settings.Set(FileListColumnLayoutService.NameWidthKey, 9000d);
        settings.Set(FileListColumnLayoutService.ModifiedWidthKey, 200d);

        var service = new FileListColumnLayoutService(settings);
        Assert.Equal(8192, service.PreferredWidths.Name);
        Assert.Equal(200, service.PreferredWidths.Modified);

        service.Preview(FileListColumn.Modified, 230);
        service.Commit(FileListColumn.Modified);
        Assert.Equal(230, settings.Get(FileListColumnLayoutService.ModifiedWidthKey, 0d));

        service.Reset(FileListColumn.Modified);
        Assert.Equal(170, service.PreferredWidths.Modified);
        Assert.Equal(170, settings.Get(FileListColumnLayoutService.ModifiedWidthKey, 0d));
    }

    [Fact]
    public void PreviewBroadcastsLiveWidthChangesForAllOpenViews()
    {
        var service = new FileListColumnLayoutService();
        var observed = FileListColumnLayoutService.Defaults;
        service.LayoutChanged += (_, _) => observed = service.PreferredWidths;

        service.Preview(FileListColumn.Size, 145);

        Assert.Equal(145, observed.Size);
    }

    private sealed class MemorySettingsService : ISettingsService
    {
        public event Action<string>? SettingChanged { add { } remove { } }
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value?.ToString() : null;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public void Set(string key, string value) => _values[key] = value;

        public void Set<T>(string key, T value) => _values[key] = value;

        public Dictionary<string, string> GetAll() => _values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.ToString() ?? string.Empty,
            StringComparer.Ordinal);
    }

}
