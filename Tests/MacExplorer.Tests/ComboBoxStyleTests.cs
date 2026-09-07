using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ComboBoxStyleTests
{
    [AvaloniaFact]
    public void LightThemeUsesTheSidebarActionMenuSurfaceForComboBoxes()
    {
        AssertResourceBrush("ComboBoxDropDownBackground", "#FFFFFF");
        AssertResourceBrush("ComboBoxDropDownBorderBrush", "#E1E4E9");
        AssertResourceBrush("ComboBoxItemBackgroundSelected", "Transparent");
        AssertResourceBrush("ComboBoxItemBackgroundPointerOver", "#ECEEF2");
    }

    private static void AssertResourceBrush(string key, string expected)
    {
        var application = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(application.TryGetResource(key, application.ActualThemeVariant, out var value));
        var brush = Assert.IsType<SolidColorBrush>(value);
        Assert.Equal(Color.Parse(expected), brush.Color);
    }
}
