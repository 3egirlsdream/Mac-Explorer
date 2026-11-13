using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "Buttons")]
public sealed class FixedButtonSizeTests
{
    [AvaloniaTheory]
    [InlineData(false, "zh-CN")]
    [InlineData(false, "zh-Hant")]
    [InlineData(false, "en")]
    [InlineData(false, "fr")]
    [InlineData(true, "zh-CN")]
    [InlineData(true, "zh-Hant")]
    [InlineData(true, "en")]
    [InlineData(true, "fr")]
    public void EverySizeKeepsVariantsEqualAndContentsCenteredWithoutClipping(bool dark, string locale)
    {
        var settings = new MemorySettings();
        using var language = new LocalizationService(settings);
        language.SetLanguage(locale);
        var typography = new TypographyService(settings);
        var grid = new Grid
        {
            ColumnDefinitions = new("*,*,*,*"), RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", 13))),
            ColumnSpacing = 8, RowSpacing = 6, Margin = new Thickness(16)
        };
        var candidates = new List<(Control Button, double Height)>();
        (string Name, double Height)[] sizes = [("", 32), ("compact", 28), ("small", 24), ("large", 36)];
        var variants = new[] { "primary", "secondary", "ghost", "danger" };
        var text = LocalizationText.Get("保存") + " Agjpqy";
        for (var sizeIndex = 0; sizeIndex < sizes.Length; sizeIndex++)
        {
            var size = sizes[sizeIndex];
            for (var variantIndex = 0; variantIndex < variants.Length; variantIndex++)
            {
                for (var mixed = 0; mixed < 2; mixed++)
                {
                    var button = new AppButton { Content = mixed == 0 ? text : MixedContent(text), Classes = { variants[variantIndex] } };
                    Add(button, size, sizeIndex * 2 + mixed, variantIndex);
                }
                Add(new AppToggleButton { Content = text, Classes = { variants[variantIndex] } }, size, 8 + sizeIndex, variantIndex);
            }
            Add(new AppSplitButton { Content = text, Classes = { "secondary" } }, size, 12, sizeIndex);
        }
        var window = InputAppearanceTests.CreateWindow(grid, dark, 1600);
        try
        {
            typography.Initialize();
            foreach (var preset in new[] { FontSizePreset.Small, FontSizePreset.Standard, FontSizePreset.Large })
            {
                typography.SetPreset(preset); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                foreach (var (button, height) in candidates)
                {
                    var description = $"{locale}, {preset}, {button.GetType().Name}, {string.Join(" ", button.Classes)}";
                    Assert.True(Math.Abs(button.Bounds.Height - height) < 0.01, $"{description}: height {button.Bounds.Height}, expected {height}");
                    var label = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>());
                    Assert.True(label.Bounds.Width + 0.01 >= label.TextLayout.Width, $"{description}: text width is clipped: button {button.Bounds}, label {label.Bounds}, layout {label.TextLayout.Width} x {label.TextLayout.Height}, font {label.FontSize}");
                    Assert.True(label.Bounds.Height + 0.01 >= label.TextLayout.Height, $"{description}: text layout is clipped");
                    CheckVerticalBounds(button, label, height, description);
                    if (button is not SplitButton)
                    {
                        var content = (button as ContentControl)!.Content as Control ?? label;
                        var center = content.TranslatePoint(new Point(content.Bounds.Width / 2, content.Bounds.Height / 2), button)!.Value;
                        Assert.True(Math.Abs(center.X - button.Bounds.Width / 2) <= 0.5, $"{description}: content is not horizontally centered");
                        foreach (var icon in button.GetVisualDescendants().OfType<PathIcon>())
                            CheckVerticalBounds(button, icon, height, description);
                    }
                    if (button is SplitButton split)
                    {
                        var primary = split.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_PrimaryButton");
                        Assert.Equal(height, primary.Bounds.Height);
                        foreach (var icon in split.GetVisualDescendants().OfType<PathIcon>())
                            CheckVerticalBounds(button, icon, height, description);
                    }
                }
            }
        }
        finally { typography.SetPreset(FontSizePreset.Standard); window.Close(); }

        void Add(Control button, (string Name, double Height) size, int row, int column)
        {
            if (size.Name.Length > 0) button.Classes.Add(size.Name);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(button, row); Grid.SetColumn(button, column); grid.Children.Add(button);
            candidates.Add((button, size.Height));
        }
    }

    private static StackPanel MixedContent(string text) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children =
        {
            new PathIcon { Data = Geometry.Parse(MacExplorer.Assets.Icons.Close), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
        }
    };

    private static void CheckVerticalBounds(Control button, Control content, double height, string description)
    {
        var origin = content.TranslatePoint(default, button)!.Value;
        Assert.True(origin.Y >= -0.01 && origin.Y + content.Bounds.Height <= height + 0.01, $"{description}: content exceeds button bounds");
        Assert.True(Math.Abs(origin.Y + content.Bounds.Height / 2 - height / 2) <= 0.5, $"{description}: content is not vertically centered: origin {origin}, size {content.Bounds}, target {height}");
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = [];
        public event Action<string>? SettingChanged { add { } remove { } }
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T defaultValue) => defaultValue;
        public void Set(string key, string value) => _values[key] = value;
        public void Set<T>(string key, T value) => _values[key] = value?.ToString() ?? "";
        public Dictionary<string, string> GetAll() => new(_values);
    }
}
