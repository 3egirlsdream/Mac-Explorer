#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class ButtonComponentsSmoke
{
    internal static async Task CaptureGalleryAsync(MainWindow owner, string output)
    {
        var language = App.Services.GetRequiredService<ILocalizationService>();
        var typography = App.Services.GetRequiredService<ITypographyService>();
        var measurements = new List<object>();
        (string Name, double Height)[] sizes = [("", 32), ("compact", 28), ("small", 24), ("large", 36)];
        try
        {
            foreach (var id in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            foreach (var dark in new[] { false, true })
            foreach (var preset in new[] { FontSizePreset.Small, FontSizePreset.Standard, FontSizePreset.Large })
            {
                language.SetLanguage(id); typography.SetPreset(preset);
                var content = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(16) };
                var heading = new Grid { ColumnDefinitions = new("100,*,*,*,*"), ColumnSpacing = 12 };
                for (var i = 0; i < sizes.Length; i++)
                {
                    var label = new TextBlock { Text = (sizes[i].Name.Length == 0 ? "default" : sizes[i].Name) + " " + sizes[i].Height, TextAlignment = Avalonia.Media.TextAlignment.Center };
                    Grid.SetColumn(label, i + 1); heading.Children.Add(label);
                }
                content.Children.Add(heading);
                var buttons = new List<(AppButton Button, double Height)>();
                foreach (var variant in new[] { "primary", "secondary", "ghost", "danger" })
                for (var mixed = 0; mixed < 2; mixed++)
                {
                    var row = new Grid { ColumnDefinitions = new("100,*,*,*,*"), ColumnSpacing = 12 };
                    row.Children.Add(new TextBlock { Text = variant + (mixed == 0 ? "" : " +"), VerticalAlignment = VerticalAlignment.Center });
                    for (var i = 0; i < sizes.Length; i++)
                    {
                        var text = LocalizationText.Get("保存");
                        var button = new AppButton { Classes = { variant } };
                        if (mixed == 0) button.Content = text;
                        else button.Content = new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 6,
                            Children =
                            {
                                new PathIcon { Data = Avalonia.Media.Geometry.Parse(Assets.Icons.Close), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
                                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
                            }
                        };
                        if (sizes[i].Name.Length > 0) button.Classes.Add(sizes[i].Name);
                        Grid.SetColumn(button, i + 1); row.Children.Add(button); buttons.Add((button, sizes[i].Height));
                    }
                    content.Children.Add(row);
                }
                var window = new DialogWindow
                {
                    Title = "Button components", TitleBarContent = new TextBlock { Text = "Button components", Classes = { "dialog-window-title" } },
                    Width = 760, SizeToContent = SizeToContent.Height, Content = content,
                    RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
                };
                var pending = window.ShowDialog(owner);
                try
                {
                    await CaptureAsync(window, Path.Combine(output, $"gallery-{id}-{(dark ? "dark" : "light")}-{preset}.png"));
                    foreach (var (button, height) in buttons)
                    {
                        var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
                        var origin = label.TranslatePoint(default, button)!.Value;
                        if (Math.Abs(button.Bounds.Height - height) > 0.01 || origin.Y < 0 || origin.Y + label.Bounds.Height > height + 0.01
                            || label.TextLayout.Height > label.Bounds.Height + 0.01 || label.TextLayout.Width > label.Bounds.Width + 0.01
                            || Math.Abs(origin.Y + label.Bounds.Height / 2 - height / 2) > 0.5)
                            throw new InvalidOperationException($"Button geometry: {id}/{dark}/{preset}/{button.Classes}, {button.Bounds}, text {label.Bounds}");
                        var contentVisual = button.Content as Control ?? label;
                        var center = contentVisual.TranslatePoint(new Point(contentVisual.Bounds.Width / 2, contentVisual.Bounds.Height / 2), button)!.Value;
                        if (Math.Abs(center.X - button.Bounds.Width / 2) > 0.5)
                            throw new InvalidOperationException("Button content is not horizontally centered.");
                        foreach (var icon in button.GetVisualDescendants().OfType<PathIcon>())
                        {
                            var iconOrigin = icon.TranslatePoint(default, button)!.Value;
                            if (iconOrigin.Y < 0 || iconOrigin.Y + icon.Bounds.Height > height + 0.01
                                || Math.Abs(iconOrigin.Y + icon.Bounds.Height / 2 - height / 2) > 0.5)
                                throw new InvalidOperationException("Button icon is clipped or off center.");
                        }
                        measurements.Add(new { language = id, dark, preset = preset.ToString(), classes = string.Join(" ", button.Classes), height = button.Bounds.Height, textHeight = label.Bounds.Height });
                    }
                }
                finally { window.Close(); await pending; }
            }
            await CaptureDropdownsAsync(owner, output);
            await File.WriteAllTextAsync(Path.Combine(output, "button-sizes.json"), System.Text.Json.JsonSerializer.Serialize(measurements, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally { typography.SetPreset(FontSizePreset.Standard); }
    }

    private static async Task CaptureDropdownsAsync(MainWindow owner, string output)
    {
        var language = App.Services.GetRequiredService<ILocalizationService>();
        var typography = App.Services.GetRequiredService<ITypographyService>();
        typography.SetPreset(FontSizePreset.Large);
        foreach (var id in new[] { "zh-CN", "zh-Hant", "en", "fr" })
        foreach (var dark in new[] { false, true })
        {
            language.SetLanguage(id);
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
            var fields = new List<AppComboBox>();
            foreach (var size in new[] { "", "compact", "small", "large" })
            {
                var field = new AppComboBox
                {
                    Width = 340, SelectedIndex = 0,
                    ItemsSource = new[] { LocalizationText.Get("conflict.Skip"), LocalizationText.Get("conflict.Replace"), LocalizationText.Get("conflict.ReplaceOlder") }
                };
                if (size.Length > 0) field.Classes.Add(size);
                panel.Children.Add(field); fields.Add(field);
            }
            var window = new DialogWindow
            {
                Title = "ComboBox components", Width = 400, SizeToContent = SizeToContent.Height,
                Content = panel, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
            };
            var pending = window.ShowDialog(owner);
            try
            {
                await CaptureAsync(window, Path.Combine(output, $"combobox-{id}-{(dark ? "dark" : "light")}.png"));
                fields[1].IsDropDownOpen = true;
                var popup = fields[1].GetVisualDescendants().OfType<Popup>().Single();
                await CaptureAsync(TopLevel.GetTopLevel(popup.Child!)!, Path.Combine(output, $"combobox-popup-{id}-{(dark ? "dark" : "light")}.png"));
                fields[1].IsDropDownOpen = false;
            }
            finally { window.Close(); await pending; }
        }
    }

    internal static async Task CaptureAsync(Control control, string path)
    {
        control.UpdateLayout(); await Task.Delay(100);
        var visual = ElementComposition.GetElementVisual(control)!;
        await visual.Compositor.RequestCommitAsync();
        using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
        bitmap.Save(path);
    }
}
#endif
