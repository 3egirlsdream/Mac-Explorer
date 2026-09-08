using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FileListColumnLayoutServiceTests
{
    [Fact]
    public void DefaultsMatchFinderStyleLayout()
    {
        var service = new FileListColumnLayoutService();

        Assert.Equal(new FileListColumnWidths(420, 170, 110, 110), service.PreferredWidths);
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

        Assert.Equal(910, wide.Name);
        Assert.Equal(1300, wide.Total);
        Assert.Equal(510, preview.Name);
        Assert.Equal(preferred.Modified, wide.Modified);
        Assert.Equal(preferred.Size, wide.Size);
        Assert.Equal(preferred.Type, wide.Type);
    }

    [Fact]
    public void MetadataColumnCanGrowByBorrowingSpaceFromName()
    {
        var filled = FileListColumnLayoutService.CalculateEffective(FileListColumnLayoutService.Defaults, 1000);
        var width = FileListColumnLayoutService.ClampInteractiveWidth(FileListColumn.Modified, 220, filled, 1000);

        Assert.Equal(220, width);
        var resized = FileListColumnLayoutService.CalculateEffective(FileListColumnLayoutService.Defaults.With(FileListColumn.Modified, width), 1000);
        Assert.Equal(560, resized.Name);
        Assert.Equal(1000, resized.Total);
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
    public void InteractiveResizeUsesOnlyAvailableTrailingSpace()
    {
        var width = FileListColumnLayoutService.ClampInteractiveWidth(
            FileListColumn.Name,
            700,
            FileListColumnLayoutService.Defaults,
            850);

        Assert.Equal(460, width);
    }

    [Fact]
    public void SavedWidthsAreRestoredClampedAndResetPersistsDefault()
    {
        var settings = new MemorySettingsService();
        settings.Set(FileListColumnLayoutService.NameWidthKey, 900d);
        settings.Set(FileListColumnLayoutService.ModifiedWidthKey, 200d);

        var service = new FileListColumnLayoutService(settings);
        Assert.Equal(720, service.PreferredWidths.Name);
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
        service.PreferredWidthsChanged += (_, _) => observed = service.PreferredWidths;

        service.Preview(FileListColumn.Size, 145);

        Assert.Equal(145, observed.Size);
    }

    private sealed class MemorySettingsService : ISettingsService
    {
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
