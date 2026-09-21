using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiquidGlassAvaloniaUI;
using Xunit;

namespace MacExplorer.Tests;

public sealed class PopupGlassTests
{
    [AvaloniaFact]
    public void PopupCaptureIncludesPageContentWithoutCapturingMenusOrChangingSidebarCapture()
    {
        var page = new CaptureProbe();
        var menu = new CaptureProbe();
        LiquidGlassBackdrop.SetIsExcludedFromCapture(page, true);
        LiquidGlassBackdrop.SetIsExcludedFromForegroundCapture(menu, true);
        LiquidGlassBackdrop.SetIsExcludedFromCapture(menu, true);
        var root = new Grid { Children = { page, menu } };
        root.Measure(new Size(200, 200));
        root.Arrange(new Rect(0, 0, 200, 200));
        var renderer = typeof(LiquidGlassSurface).Assembly.GetType("LiquidGlassAvaloniaUI.LiquidGlassVisualRenderer")!
            .GetMethod("Render", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        using var context = new DrawingGroup().Open();
        renderer.Invoke(null, new object?[] { context, root, new Rect(root.Bounds.Size), null, false });
        Assert.Equal(0, page.RenderCount);
        renderer.Invoke(null, new object?[] { context, root, new Rect(root.Bounds.Size), null, true });
        Assert.Equal(1, page.RenderCount);
        Assert.Equal(0, menu.RenderCount);
        renderer.Invoke(null, new object?[] { context, root, new Rect(root.Bounds.Size), null, false });
        Assert.Equal(1, page.RenderCount);
    }

    private sealed class CaptureProbe : Control
    {
        public int RenderCount { get; private set; }
        public override void Render(DrawingContext context) => RenderCount++;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedMenusKeepGlassAfterReopeningAndThemeChanges(bool dark)
    {
        var target = new Button { Content = "Open" };
        var leaf = new MenuItem { Header = "Leaf" };
        var parent = new MenuItem { Header = "Parent", Items = { leaf } };
        var menu = new ContextMenu { Items = { parent } };
        target.ContextMenu = menu;
        var window = InputAppearanceTests.CreateWindow(target, dark);
        try
        {
            for (var i = 0; i < 2; i++)
            {
                menu.Open(target);
                Dispatcher.UIThread.RunJobs();
                var glass = Assert.Single(menu.GetVisualDescendants().OfType<LiquidGlassSurface>());
                Assert.True(glass.Bounds.Width > 0);
                Assert.True(glass.CaptureForeground);
                AssertShadowFitsClippingAncestors(glass);
                parent.IsSelected = true;
                var hover = parent.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_LayoutRoot");
                Assert.Equal(parent.FindResource(parent.ActualThemeVariant, "SidebarHoverBrush"), hover.Background);
                Assert.Equal(parent.FindResource(parent.ActualThemeVariant, "SidebarHoverOutline"), hover.BoxShadow);
                parent.IsSubMenuOpen = true;
                Dispatcher.UIThread.RunJobs();
                var popup = parent.GetVisualDescendants().OfType<Popup>().Single();
                var rootSurface = menu.GetVisualDescendants().OfType<Border>()
                    .Single(x => x.Classes.Contains("context-submenu-surface"));
                var childSurface = Assert.IsType<Border>(popup.Child);
                AssertSameCard(rootSurface, childSurface);
                var childGlass = Assert.Single(popup.Child!.GetVisualDescendants().OfType<LiquidGlassSurface>());
                AssertShadowFitsClippingAncestors(childGlass);
                Assert.Equal(glass.SurfaceColor, childGlass.SurfaceColor);
                Assert.True(leaf.IsAttachedToVisualTree());
                window.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark;
                Dispatcher.UIThread.RunJobs();
                AssertSameCard(rootSurface, childSurface);
                Assert.Equal(glass.SurfaceColor, childGlass.SurfaceColor);
                Assert.Equal(dark ? 6 : 0, childGlass.RefractionAmount);
                menu.Close();
                window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            }
        }
        finally { menu.Close(); window.Close(); }
    }

    private static void AssertSameCard(Border expected, Border actual)
    {
        Assert.Equal(expected.Background, actual.Background);
        Assert.Equal(expected.BorderBrush, actual.BorderBrush);
        Assert.Equal(expected.BorderThickness, actual.BorderThickness);
        Assert.Equal(expected.CornerRadius, actual.CornerRadius);
        Assert.Equal(expected.Padding, actual.Padding);
        Assert.Equal(expected.Margin, actual.Margin);
        Assert.Equal(expected.BoxShadow, actual.BoxShadow);
        Assert.Equal(expected.ClipToBounds, actual.ClipToBounds);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DropdownShadowFitsItsPopupWindow(bool dark)
    {
        var combo = new ComboBox { Width = 220, ItemsSource = new[] { "First", "Second", "Third" }, SelectedIndex = 0 };
        var window = InputAppearanceTests.CreateWindow(combo, dark);
        try
        {
            combo.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            var popup = combo.GetVisualDescendants().OfType<Popup>().Single();
            var glass = Assert.Single(popup.Child!.GetVisualDescendants().OfType<LiquidGlassSurface>());
            AssertShadowFitsClippingAncestors(glass);
        }
        finally { combo.IsDropDownOpen = false; window.Close(); }
    }

    private static void AssertShadowFitsClippingAncestors(LiquidGlassSurface glass)
    {
        var pad = Math.Ceiling(glass.ShadowRadius * 3) + 1;
        var offset = glass.ShadowOffset;
        var shadow = new Rect(glass.Bounds.Size).Inflate(new Thickness(
            pad + Math.Max(0, -offset.X), pad + Math.Max(0, -offset.Y),
            pad + Math.Max(0, offset.X), pad + Math.Max(0, offset.Y)));
        var root = TopLevel.GetTopLevel(glass)!;
        foreach (var ancestor in glass.GetVisualAncestors().Where(x => x.ClipToBounds || ReferenceEquals(x, root)))
        {
            var transform = glass.TransformToVisual(ancestor)!.Value;
            Assert.True(new Rect(ancestor.Bounds.Size).Contains(shadow.TransformToAABB(transform)),
                $"{ancestor.GetType().Name} clips the popup shadow");
        }
    }
}
