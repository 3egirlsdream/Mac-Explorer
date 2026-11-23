using MacExplorer.Services;
using Xunit;

namespace MacExplorer.UnitTests;

[Trait("Kind", "Unit")]
[Trait("Area", "VerticalTabs")]
public sealed class VerticalTabBarPreferencesTests
{
    [Fact]
    public void DefaultsAndPersistenceRoundTrip()
    {
        var settings = new InMemorySettings();
        Assert.False(VerticalTabBarPreferences.IsEnabled(settings));
        Assert.True(VerticalTabBarPreferences.IsPinned(settings));
        Assert.Equal(VerticalTabBarPreferences.DefaultExpandedWidth, VerticalTabBarPreferences.GetWidth(settings));

        VerticalTabBarPreferences.SetEnabled(settings, true);
        VerticalTabBarPreferences.SetPinned(settings, false);
        VerticalTabBarPreferences.SetWidth(settings, 286);

        Assert.True(VerticalTabBarPreferences.IsEnabled(settings));
        Assert.False(VerticalTabBarPreferences.IsPinned(settings));
        Assert.Equal(286, VerticalTabBarPreferences.GetWidth(settings));
    }

    [Theory]
    [InlineData(0, double.PositiveInfinity, VerticalTabBarPreferences.DefaultExpandedWidth)]
    [InlineData(120, double.PositiveInfinity, VerticalTabBarPreferences.MinExpandedWidth)]
    [InlineData(500, double.PositiveInfinity, VerticalTabBarPreferences.MaxExpandedWidth)]
    [InlineData(340, 640, 280)]
    [InlineData(260, 500, VerticalTabBarPreferences.MinExpandedWidth)]
    public void ClampWidthRespectsBoundsAndWindowSpace(double requested, double windowWidth, double expected)
    {
        Assert.Equal(expected, VerticalTabBarPreferences.ClampWidth(requested, windowWidth));
    }

    [Fact]
    public void RelatedSettingDetectionMatchesExactKeys()
    {
        Assert.True(VerticalTabBarPreferences.IsRelatedSetting(VerticalTabBarPreferences.EnabledKey));
        Assert.True(VerticalTabBarPreferences.IsRelatedSetting(VerticalTabBarPreferences.PinnedKey));
        Assert.True(VerticalTabBarPreferences.IsRelatedSetting(VerticalTabBarPreferences.WidthKey));
        Assert.False(VerticalTabBarPreferences.IsRelatedSetting("workspace_sidebar_width"));
    }

    private sealed class InMemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = [];
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T defaultValue)
        {
            if (!_values.TryGetValue(key, out var raw)) return defaultValue;
            if (typeof(T) == typeof(bool) && bool.TryParse(raw, out var b)) return (T)(object)b;
            if (typeof(T) == typeof(int) && int.TryParse(raw, out var i)) return (T)(object)i;
            if (typeof(T) == typeof(double) && double.TryParse(raw, out var d)) return (T)(object)d;
            if (typeof(T) == typeof(string)) return (T)(object)raw;
            return defaultValue;
        }
        public void Set(string key, string value) { _values[key] = value; SettingChanged?.Invoke(key); }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }
}
