using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "Buttons")]
public sealed class ComboBoxComponentTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllSizesKeepSelectionAndArrowVisibleAndShareThePopupSurface(bool dark)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        (string Name, double Height)[] sizes = [("", 32), ("compact", 28), ("small", 24), ("large", 36)];
        var fields = sizes.Select(size => new AppComboBox
        {
            Width = 220, ItemsSource = new[] { "第一项 Agjpqy", "Second option", "Troisième option" }, SelectedIndex = 0
        }).ToArray();
        for (var i = 0; i < fields.Length; i++)
        {
            if (sizes[i].Name.Length > 0) fields[i].Classes.Add(sizes[i].Name);
            panel.Children.Add(fields[i]);
        }
        var window = InputAppearanceTests.CreateWindow(panel, dark);
        try
        {
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                Assert.Equal(sizes[i].Height, field.Bounds.Height);
                var label = Assert.Single(field.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Height > 0));
                var origin = label.TranslatePoint(default, field)!.Value;
                Assert.True(label.TextLayout.Height <= label.Bounds.Height + 0.01);
                Assert.True(origin.Y >= 0 && origin.Y + label.Bounds.Height <= field.Bounds.Height + 0.01);
                Assert.True(Math.Abs(origin.Y + label.Bounds.Height / 2 - field.Bounds.Height / 2) <= 0.5);
                field.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                var popup = field.GetVisualDescendants().OfType<Popup>().Single();
                var surface = Assert.IsType<Border>(popup.Child);
                Assert.True(PopupGlass.GetIsEnabled(surface));
                Assert.Single(surface.GetVisualDescendants().OfType<LiquidGlassAvaloniaUI.LiquidGlassSurface>());
                field.SelectedIndex = 1;
                field.IsDropDownOpen = false;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Second option", field.SelectedItem);
            }
        }
        finally { window.Close(); }
    }
}
