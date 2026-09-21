using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using LiquidGlassAvaloniaUI;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public sealed class HomeGlassAppearanceTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandedGlassKeepsCardMaterial(bool dark)
    {
        var card = new LiquidGlassSurface { Classes = { "home-glass" } };
        var expanded = new LiquidGlassSurface { Classes = { "home-glass", "home-expanded-glass" } };
        var window = new Window { Width = 400, Height = 300,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            Content = new StackPanel { Children = { card, expanded } } };
        window.Styles.Add((Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/HomeStyles.axaml")));
        window.Show();
        try
        {
            Assert.Equal(card.BackdropOpacity, expanded.BackdropOpacity);
            Assert.Equal(card.SurfaceColor, expanded.SurfaceColor);
            Assert.Equal(card.BlurRadius, expanded.BlurRadius);
            Assert.Equal(card.HighlightOpacity, expanded.HighlightOpacity);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DarkHomeGlassMatchesSidebarMaterialWithoutBrightEdges(bool expanded)
    {
        var glass = new LiquidGlassSurface { Classes = { "home-glass" } };
        if (expanded) glass.Classes.Add("home-expanded-glass");
        var window = new Window { Width = 400, Height = 300, RequestedThemeVariant = ThemeVariant.Dark, Content = glass };
        window.Styles.Add((Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/HomeStyles.axaml")));
        window.Show();
        try
        {
            var sidebar = new LiquidGlassSurface();
            SidebarAppearance.Apply(sidebar, true);
            Assert.Equal(sidebar.SurfaceColor, glass.SurfaceColor);
            Assert.Equal(sidebar.BackdropOpacity, glass.BackdropOpacity);
            Assert.Equal(sidebar.BlurRadius, glass.BlurRadius);
            Assert.Equal(sidebar.RefractionAmount, glass.RefractionAmount);
            Assert.Equal(sidebar.DepthEffect, glass.DepthEffect);
            Assert.Equal(sidebar.HighlightOpacity, glass.HighlightOpacity);
            Assert.Equal(sidebar.ShadowEnabled, glass.ShadowEnabled);
            Assert.Equal(sidebar.InnerShadowEnabled, glass.InnerShadowEnabled);
            Assert.Equal(new Avalonia.CornerRadius(10), glass.CornerRadius);
        }
        finally { window.Close(); }
    }
}
