using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiquidGlassAvaloniaUI;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Toast")]
public sealed class ToastAppearanceTests
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, 320)]
    [InlineData(true, 320)]
    [InlineData(false, 800)]
    [InlineData(true, 800)]
    public void ToastFitsWindowAndUpdatesMaterialWithTheme(bool dark, int width)
    {
        var window = InputAppearanceTests.CreateWindow(new Border(), dark, width);
        try
        {
            var manager = AppToast.For(window);
            foreach (var type in Enum.GetValues<NotificationType>())
            {
                manager.Show(new Notification("A long notification title that wraps in narrow windows",
                    "The server connection timed out. Check the network connection and try again.", type, TimeSpan.Zero));
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var card = window.GetVisualDescendants().OfType<NotificationCard>().Last();
                var glass = Assert.Single(card.GetVisualDescendants().OfType<LiquidGlassSurface>());
                Assert.Contains("toast-glass", glass.Classes);
                Assert.Equal(0, glass.RefractionAmount);
                Assert.InRange(card.Bounds.Width, 100, width);
                var text = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).ToArray();
                Assert.Equal(2, text.Length);
                Assert.All(text, t => Assert.True(t.Bounds.Width > 0));
                if (width == 320) Assert.True(text[0].Bounds.Height > 20);
                var color = glass.SurfaceColor;
                window.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark;
                Dispatcher.UIThread.RunJobs();
                Assert.NotEqual(color, glass.SurfaceColor);
                window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task UntitledToastHasNoEmptyTitleAndClickKeepsItsAction()
    {
        var window = InputAppearanceTests.CreateWindow(new Border(), false, 600);
        var clicked = false;
        try
        {
            AppToast.For(window).Show(new Notification(null, "Notification details", expiration: TimeSpan.Zero,
                onClick: () => clicked = true));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var card = Assert.Single(window.GetVisualDescendants().OfType<NotificationCard>());
            Assert.Single(card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible));
            var point = card.TranslatePoint(new Avalonia.Point(100, 40), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.True(clicked);
            Assert.True(card.IsClosing);
            await Task.Delay(300);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.GetVisualDescendants().OfType<NotificationCard>());
        }
        finally { window.Close(); }
    }
}
