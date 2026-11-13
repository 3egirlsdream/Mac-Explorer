using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using MacExplorer.Services.Preview;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Preview")]
public sealed class ScrollBarAppearanceTests
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData("text", false)]
    [InlineData("text", true)]
    [InlineData("diff", false)]
    [InlineData("diff", true)]
    [InlineData("markdown", false)]
    [InlineData("markdown", true)]
    [InlineData("editor", false)]
    [InlineData("editor", true)]
    public void PreviewScrollbarsKeepTheSharedThemeSizeAndRoundedThumbs(string kind, bool dark)
    {
        using var theme = new FastListTestTheme();
        var lines = string.Join('\n', Enumerable.Range(0, 200).Select(index => "line " + index));
        Control content;
        if (kind == "diff")
        {
            var diff = new DiffPreviewView();
            diff.SetDocument(DiffPreviewDocument.Parse("--- a/file\n+++ b/file\n@@ -1,200 +1,200 @@\n"
                + string.Join('\n', Enumerable.Range(0, 200).Select(index => "-before " + index + "\n+after " + index))));
            content = diff;
        }
        else if (kind == "markdown")
        {
            var markdown = new MarkdownPreviewView(); markdown.SetDocument("```text\n" + lines + "\n```", "/tmp/scrollbar.md");
            content = markdown;
        }
        else if (kind == "editor")
        {
            var editor = new MarkdownEditorView(); editor.FindControl<TextEditor>("Editor")!.Text = lines; content = editor;
        }
        else content = new ScrollViewer { Content = new TextBlock { Text = lines } };
        var window = new Window { Width = 700, Height = 400, Content = content,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        foreach (var asset in new[] { "ComponentStyles", "ScrollBarStyles" })
            window.Styles.Add(new StyleInclude(new Uri("avares://MacExplorer/")) { Source = new Uri($"avares://MacExplorer/Assets/{asset}.axaml") });
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(window.TryFindResource("AppScrollBarTheme", window.ActualThemeVariant, out var shared));
            var bars = content.GetVisualDescendants().OfType<ScrollBar>()
                .Where(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical && bar.IsEffectivelyVisible && bar.Bounds.Height > 0).ToArray();
            Assert.NotEmpty(bars);
            foreach (var bar in bars)
            {
                Assert.Same(shared, bar.Theme);
                Assert.Equal(8, bar.Bounds.Width);
                var thumbs = bar.GetVisualDescendants().OfType<Thumb>().ToArray(); Assert.NotEmpty(thumbs);
                Assert.All(thumbs, thumb => Assert.Equal(new CornerRadius(999), thumb.CornerRadius));
                var track = bar.GetVisualDescendants().OfType<Rectangle>().Single(rectangle => rectangle.Name == "TrackRect");
                Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(track.Fill).Color);
                Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(track.Stroke).Color);
                var point = bar.TranslatePoint(new Point(4, 30), window)!.Value;
                window.MouseMove(point); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(8, bar.Bounds.Width);
            }
        }
        finally { window.Close(); (content as IDisposable)?.Dispose(); }
    }
}
