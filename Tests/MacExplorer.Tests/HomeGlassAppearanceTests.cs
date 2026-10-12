using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using LiquidGlassAvaloniaUI;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "Home")]
public sealed class HomeGlassAppearanceTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CardAndExpansionShareMaterialAndDarkMaterialMatchesSidebar(bool dark)
    {
        var card = new LiquidGlassSurface { Classes = { "home-glass" } };
        var expanded = new LiquidGlassSurface { Classes = { "home-glass", "home-expanded-glass" } };
        var window = new Window
        {
            Width = 400, Height = 300,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            Content = new StackPanel { Children = { card, expanded } }
        };
        window.Styles.Add((Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/HomeStyles.axaml")));
        window.Show();
        try
        {
            AssertMaterial(card, expanded);
            Assert.Equal(card.CornerRadius, expanded.CornerRadius);
            if (dark)
            {
                var sidebar = new LiquidGlassSurface();
                SidebarAppearance.Apply(sidebar, true);
                AssertMaterial(sidebar, card);
                Assert.Equal(new Avalonia.CornerRadius(10), card.CornerRadius);
            }
        }
        finally { window.Close(); }
    }

    private static void AssertMaterial(LiquidGlassSurface expected, LiquidGlassSurface actual)
    {
        Assert.Equal(expected.SurfaceColor, actual.SurfaceColor);
        Assert.Equal(expected.BackdropOpacity, actual.BackdropOpacity);
        Assert.Equal(expected.BlurRadius, actual.BlurRadius);
        Assert.Equal(expected.RefractionAmount, actual.RefractionAmount);
        Assert.Equal(expected.DepthEffect, actual.DepthEffect);
        Assert.Equal(expected.HighlightOpacity, actual.HighlightOpacity);
        Assert.Equal(expected.ShadowEnabled, actual.ShadowEnabled);
        Assert.Equal(expected.InnerShadowEnabled, actual.InnerShadowEnabled);
    }
}
