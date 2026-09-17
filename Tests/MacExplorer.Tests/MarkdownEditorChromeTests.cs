using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Assets;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class MarkdownEditorChromeTests
{
    [AvaloniaFact]
    public void FormatToolbarUsesAppIconButtons()
    {
        var view = new MarkdownEditorView();

        var tools = Assert.IsType<WrapPanel>(view.FindControl<WrapPanel>("FormatTools"));
        var buttons = tools.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Equal(14, buttons.Count);
        foreach (var button in buttons)
        {
            Assert.Contains("ghost", button.Classes);
            Assert.Contains("toolbar-btn", button.Classes);
            Assert.Contains("w32", button.Classes);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string));
            Assert.NotNull(Assert.IsType<PathIcon>(button.Content).Data);
        }

        Assert.Equal(3, tools.GetLogicalDescendants().OfType<Separator>()
            .Count(separator => separator.Classes.Contains("toolbar-separator")));
    }

    [AvaloniaFact]
    public void ModeSwitchUsesAppSegmentedToggles()
    {
        var view = new MarkdownEditorView();

        var split = Assert.IsType<ToggleButton>(view.FindControl<ToggleButton>("SplitMode"));
        var preview = Assert.IsType<ToggleButton>(view.FindControl<ToggleButton>("PreviewMode"));
        var source = Assert.IsType<ToggleButton>(view.FindControl<ToggleButton>("SourceMode"));
        Assert.All(new[] { source, split, preview }, toggle => Assert.Contains("view-mode-toggle", toggle.Classes));
        Assert.True(split.IsChecked);
        Assert.False(source.IsChecked);

        preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(preview.IsChecked);
        Assert.False(split.IsChecked);
        Assert.True(Assert.IsType<Border>(view.FindControl<Border>("PreviewPane")).IsVisible);
        Assert.False(Assert.IsType<Border>(view.FindControl<Border>("SourcePane")).IsVisible);
    }

    [AvaloniaFact]
    public void EveryIconConstantParsesToGeometry()
    {
        var constants = typeof(Icons)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, FieldType: var type } && type == typeof(string))
            .ToList();
        Assert.NotEmpty(constants);
        foreach (var constant in constants)
        {
            var data = Assert.IsType<string>(constant.GetRawConstantValue());
            var bounds = Geometry.Parse(data).Bounds;
            Assert.True(bounds.Width > 0 && bounds.Height > 0, constant.Name);
            Assert.True(bounds.X >= -1 && bounds.Y >= -1 && bounds.Right <= 25 && bounds.Bottom <= 25, constant.Name);
        }
    }
}
