using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "Buttons")]
public sealed class ButtonComponentsTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedComponentsPreserveTheGlobalAppearanceAndPointerBehavior(bool dark)
    {
        var ordinary = new Button { Content = "Save", Classes = { "secondary" } };
        var shared = new AppButton { Content = "Save", Classes = { "secondary" } };
        var toggle = new AppToggleButton { Content = "Pinned", Classes = { "ghost" } };
        var menu = new MenuFlyout(); menu.Items.Add(new MenuItem { Header = "Another choice" });
        var split = new AppSplitButton { Content = "Replace", Classes = { "secondary" }, Flyout = menu };
        var surface = new Border { Classes = { "context-submenu-surface" } };
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(16), Children = { ordinary, shared, toggle, split, surface } };
        var window = InputAppearanceTests.CreateWindow(panel, dark);
        try
        {
            Assert.Equal(ordinary.Background, shared.Background);
            Assert.Equal(ordinary.BorderBrush, shared.BorderBrush);
            Assert.Equal(ordinary.CornerRadius, shared.CornerRadius);
            Assert.Equal(ordinary.Padding, shared.Padding);
            Assert.Equal(ordinary.FontFamily, shared.FontFamily);
            Assert.Equal(ordinary.FontSize, shared.FontSize);
            Assert.Equal(ordinary.Bounds.Height, shared.Bounds.Height);
            var calls = 0; shared.Click += (_, _) => calls++;
            Click(window, shared); Assert.Equal(1, calls);
            shared.IsEnabled = false;
            Click(window, shared); Assert.Equal(1, calls);
            Assert.NotNull(toggle.Template);
            Assert.True(toggle.Bounds.Height > 0);
            Click(window, toggle);
            var togglePoint = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
            Assert.True(toggle.IsChecked, $"Toggle {toggle.Bounds}, hit {window.InputHitTest(togglePoint)?.GetType().Name}, theme {toggle.Theme?.TargetType}");
            Assert.True(window.TryFindResource("InteractionSelectedHoverBrush", window.ActualThemeVariant, out var selectedHover));
            Assert.Equal(selectedHover, toggle.Background);
            window.MouseMove(new Point(390, 590)); Dispatcher.UIThread.RunJobs();
            Assert.True(window.TryFindResource("InteractionSelectedBrush", window.ActualThemeVariant, out var selected));
            Assert.Equal(selected, toggle.Background);
            var toggleSurface = toggle.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single(presenter => presenter.Name == "PART_ContentPresenter");
            Assert.Equal(selected, toggleSurface.Background);
            var arrow = split.GetVisualDescendants().OfType<AppButton>().Single(b => b.Name == "PART_SecondaryButton");
            Click(window, arrow); Assert.True(menu.IsOpen);
            var item = (MenuItem)menu.Items[0]!;
            var presenter = item.GetVisualAncestors().OfType<MenuFlyoutPresenter>().Single();
            var dropdownSurface = presenter.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("context-submenu-surface"));
            Assert.True(PopupGlass.GetIsEnabled(dropdownSurface));
            Assert.Equal(surface.Background, dropdownSurface.Background);
            Assert.Equal(surface.BorderThickness, dropdownSurface.BorderThickness);
            Assert.Equal(surface.CornerRadius, dropdownSurface.CornerRadius);
            Assert.Equal(surface.Padding, dropdownSurface.Padding);
            Assert.Equal(surface.Margin, dropdownSurface.Margin);
            Assert.Single(dropdownSurface.GetVisualDescendants().OfType<LiquidGlassAvaloniaUI.LiquidGlassSurface>());
            Assert.True(item.Bounds.Height >= 30);
            Assert.True(item.Bounds.Width > 0);
            menu.Hide();
            var primary = split.GetVisualDescendants().OfType<AppButton>().Single(b => b.Name == "PART_PrimaryButton");
            Assert.Equal(shared.Padding, primary.Padding);
            Assert.Equal(shared.CornerRadius, primary.CornerRadius);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DefaultButtonAndEscapeKeepNativeKeyboardBehavior()
    {
        var save = new AppButton { Content = "Save", Classes = { "primary" }, IsDefault = true };
        var cancel = new AppButton { Content = "Cancel", Classes = { "secondary" }, IsCancel = true };
        var editor = new TextBox();
        var window = InputAppearanceTests.CreateWindow(new StackPanel { Children = { editor, save, cancel } }, false);
        try
        {
            var saved = 0; var cancelled = 0;
            save.Click += (_, _) => saved++;
            cancel.Click += (_, _) => cancelled++;
            editor.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Dispatcher.UIThread.RunJobs(); Assert.Equal(1, saved);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Dispatcher.UIThread.RunJobs(); Assert.Equal(1, cancelled);
        }
        finally { window.Close(); }
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
