using MacExplorer.Services;
using MacExplorer.Tests;
using Xunit;

namespace MacExplorer.UnitTests;

[Trait("Kind", "Unit")]
[Trait("Area", "VerticalTabs")]
public sealed class VerticalTabBarPreferencesTests
{
    [Fact]
    public void DefaultsAndPersistenceRoundTrip()
    {
        var settings = new InMemorySettingsService();
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
}
