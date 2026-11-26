using MacExplorer.Controls;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using CodeWF.Markdown;
using CodeWF.Markdown.Controls;
using Avalonia.Threading;
using MacExplorer.Assets;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Markdown")]
public sealed class MarkdownEditorChromeTests
{
    [AvaloniaFact(Explicit = true)]
    public void FormatToolbarUsesAppIconButtons()
    {
        using var view = new MarkdownEditorView();

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

        Assert.Equal(new[] { 2, 4, 3, 5 }, tools.Children.Cast<StackPanel>()
            .Select(group => group.Children.OfType<Button>().Count()));
        Assert.Equal(3, tools.GetLogicalDescendants().OfType<Separator>()
            .Count(separator => separator.Classes.Contains("toolbar-separator")));
    }

    [AvaloniaFact(Explicit = true)]
    public void ModeSwitchUsesAppSegmentedToggles()
    {
        using var view = new MarkdownEditorView();

        var split = Assert.IsType<AppToggleButton>(view.FindControl<ToggleButton>("SplitMode"));
        var preview = Assert.IsType<AppToggleButton>(view.FindControl<ToggleButton>("PreviewMode"));
        var source = Assert.IsType<AppToggleButton>(view.FindControl<ToggleButton>("SourceMode"));
        Assert.All(new[] { source, split, preview }, toggle => Assert.Contains("view-mode-toggle", toggle.Classes));
        Assert.True(source.IsChecked);
        Assert.False(split.IsChecked);

        split.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(split.IsChecked);

        preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(preview.IsChecked);
        Assert.False(split.IsChecked);
        Assert.True(Assert.IsType<Border>(view.FindControl<Border>("PreviewPane")).IsVisible);
        Assert.False(Assert.IsType<Border>(view.FindControl<Border>("SourcePane")).IsVisible);
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("%E4%BC%81%E4%B8%9A.txt", "企业.txt", false)]
    [InlineData("%E4%BC%81%E4%B8%9A.md", "企业.md", true)]
    [InlineData("100%25+%E4%BC%81%FF.txt", "100%25+%E4%BC%81%FF.txt", false)]
    public async Task HeaderDecodesDisplayOnlyAndUpdatesWhenEditingAndSaving(string fileName, string title, bool markdown)
    {
        using var theme = new FastListTestTheme();
        using var view = new MarkdownEditorView();
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, fileName);
        var window = new Window { Width = 640, Height = 420, Content = view, RequestedThemeVariant = ThemeVariant.Light };
        try
        {
            await File.WriteAllTextAsync(path, "Original");
            window.Show();
            await view.OpenAsync(path);
            window.UpdateLayout();

            Assert.Equal(path, view.FilePath);
            Assert.Equal(title, view.FindControl<TextBlock>("TitleText")!.Text);
            Assert.Equal(Path.Combine(directory.FullName, title), view.FindControl<TextBlock>("PathText")!.Text);
            Assert.Equal(markdown, view.FindControl<Border>("MarkdownToolbar")!.IsVisible);
            var textFind = view.FindControl<AppButton>("TextFindButton")!;
            Assert.Equal(!markdown, textFind.IsVisible);
            if (!markdown)
            {
                Assert.Contains(view.FindControl<Border>("FileHeader"), textFind.GetVisualAncestors());
                textFind.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(view.FindControl<Border>("SearchBar")!.IsVisible);
            }

            var save = view.FindControl<AppButton>("SaveButton")!;
            var dirty = view.FindControl<Control>("DirtyIndicator")!;
            Assert.Equal(Color.Parse("#F2F2F7"), Assert.IsAssignableFrom<ISolidColorBrush>(save.Background).Color);
            Assert.False(dirty.IsVisible);

            view.FindControl<TextEditor>("Editor")!.Text = "Updated";
            Assert.Equal(Color.Parse("#007AFF"), Assert.IsAssignableFrom<ISolidColorBrush>(save.Background).Color);
            Assert.True(dirty.IsVisible);
            Assert.Equal(title, view.FindControl<TextBlock>("TitleText")!.Text);
            Assert.True(await view.SaveAsync());
            Assert.Equal("Updated", await File.ReadAllTextAsync(path));
            Assert.Equal(Color.Parse("#F2F2F7"), Assert.IsAssignableFrom<ISolidColorBrush>(save.Background).Color);
            Assert.False(dirty.IsVisible);
        }
        finally
        {
            window.Close();
            directory.Delete(recursive: true);
        }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SplitUsesDistinctSurfacesAndPaddedSourceAndProse()
    {
        using var theme = new FastListTestTheme();
        using var view = new MarkdownEditorView();
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "企业.md");
        var window = new Window { Width = 640, Height = 420, Content = view, RequestedThemeVariant = ThemeVariant.Light };
        try
        {
            await File.WriteAllTextAsync(path, "# Title\n\nBody\n\n## Next\n\n> Quoted text");
            window.Show();
            await view.OpenAsync(path);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var source = view.FindControl<Border>("SourcePane")!;
            var preview = view.FindControl<Border>("PreviewPane")!;
            var gutter = Assert.Single(source.GetVisualDescendants().OfType<EditorLineNumberMargin>());
            var input = view.FindControl<TextEditor>("Editor")!.TextArea.TextView;
            Assert.Equal(Color.Parse("#FAFAFB"), Assert.IsAssignableFrom<ISolidColorBrush>(source.Background).Color);
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Background).Color);
            Assert.Equal(48, gutter.Bounds.Width);
            Assert.Equal(20, input.TranslatePoint(default, gutter)!.Value.X - gutter.Bounds.Width, 0);
            Assert.Equal(16, input.TranslatePoint(default, gutter)!.Value.Y, 0);
            var prose = Assert.Single(preview.GetVisualDescendants().OfType<MarkdownViewer>());
            Assert.Equal(new Thickness(40, 28), prose.Margin);
            Assert.Equal(760, prose.MaxWidth);
            Assert.Equal(15, prose.ParagraphFontSize);

            var blocks = prose.GetVisualDescendants().ToArray();
            var title = Assert.Single(blocks.OfType<Border>().Where(block => block.Classes.Contains(MarkdownStyleKeys.Heading1Border)));
            var section = Assert.Single(blocks.OfType<Border>().Where(block => block.Classes.Contains(MarkdownStyleKeys.Heading2Border)));
            var quote = Assert.Single(blocks.OfType<Border>().Where(block => block.Classes.Contains(MarkdownStyleKeys.Quote)));
            Assert.Equal(new Thickness(0, 0, 0, 16), title.Margin);
            Assert.Equal(new Thickness(0, 28, 0, 12), section.Margin);
            Assert.Equal(FontWeight.SemiBold, Assert.IsType<SelectableTextBlock>(section.Child).FontWeight);
            Assert.Equal(new Thickness(3, 0, 0, 0), quote.BorderThickness);
            Assert.Equal(1, view.FindControl<Border>("DividerLine")!.Bounds.Width);
            var divider = view.FindControl<GridSplitter>("Divider")!;
            Assert.Equal(9, divider.Bounds.Width);
            var point = divider.TranslatePoint(new Point(4.5, 80), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseMove(point + new Vector(40, 0));
            window.MouseUp(point + new Vector(40, 0), Avalonia.Input.MouseButton.Left);
            window.UpdateLayout();
            Assert.True(source.Bounds.Width > preview.Bounds.Width);
        }
        finally
        {
            window.Close();
            directory.Delete(recursive: true);
        }
    }

    [AvaloniaFact(Explicit = true)]
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
