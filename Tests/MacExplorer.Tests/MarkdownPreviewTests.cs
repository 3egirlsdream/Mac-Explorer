using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeWF.Markdown;
using CodeWF.Markdown.Controls;
using MacExplorer.Assets;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Markdown")]
public sealed class MarkdownPreviewTests
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DragCopiesOnlySelectedRenderedText(bool crossParagraphs, bool dark)
    {
        var view = new MarkdownPreviewView();
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = view,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        window.Styles.Add(new FluentTheme());
        try
        {
            window.Show();
            view.SetDocument("Alpha **bravo** charlie.\n\nFoxtrot golf hotel.", "/tmp/markdown-preview.md");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var viewer = view.FindControl<MarkdownViewer>("Viewer")!;
            var paragraphs = viewer.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .Where(block => block.Classes.Contains(MarkdownStyleKeys.Paragraph))
                .ToArray();
            Assert.Equal(2, paragraphs.Length);

            await TestTime.WaitUntilAsync(() =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                return ReferenceEquals(window.InputHitTest(AtCharacter(paragraphs[0], 6)), paragraphs[0]);
            });

            var start = AtCharacter(paragraphs[0], 6);
            var end = crossParagraphs ? AtCharacter(paragraphs[1], 7) : AtCharacter(paragraphs[0], 11);
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(crossParagraphs, viewer.HasSelection);
            if (crossParagraphs)
            {
                var highlight = Token<SolidColorBrush>(window, "InteractionTextHighlightBrush");
                Assert.All(paragraphs, block => Assert.Equal(highlight, block.Background));
            }
            else
            {
                Assert.Equal("bravo", paragraphs[0].SelectedText);
            }

            var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.C, command, PhysicalKey.C, null);
            var expected = crossParagraphs
                ? $"Alpha bravo charlie.{Environment.NewLine}{Environment.NewLine}Foxtrot golf hotel."
                : "bravo";
            var clipboard = Assert.IsAssignableFrom<IClipboard>(window.Clipboard);
            Assert.Equal(expected, await clipboard.TryGetTextAsync());

            var clear = AtCharacter(paragraphs[1], 0);
            window.MouseDown(clear, MouseButton.Left);
            window.MouseUp(clear, MouseButton.Left);
            Assert.False(viewer.HasSelection);
            Assert.True(viewer.Focus());
            await clipboard.SetTextAsync("previous clipboard text");
            window.KeyPress(Key.C, command, PhysicalKey.C, null);
            Assert.Equal("previous clipboard text", await clipboard.TryGetTextAsync());

            Point AtCharacter(SelectableTextBlock block, int position)
            {
                var glyph = block.TextLayout.HitTestTextPosition(position);
                return block.TranslatePoint(new Point(glyph.X + 0.5, glyph.Center.Y), window)!.Value;
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Explicit = true)]
    public void CodeBlockCopyButtonUsesTheAppIconButton()
    {
        var view = new MarkdownPreviewView();
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        try
        {
            window.Show();
            view.SetDocument("```csharp\nvar x = 1;\n```\n", "/tmp/markdown-preview.md");
            Dispatcher.UIThread.RunJobs();

            var copyButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Classes.Contains("markdown-copy"));

            var icon = Assert.IsType<PathIcon>(copyButton.Content);
            Assert.Equal(Geometry.Parse(Icons.Copy).Bounds, icon.Data!.Bounds);
            Assert.Equal("复制", AutomationProperties.GetName(copyButton));
            Assert.Equal("复制", ToolTip.GetTip(copyButton));
            Assert.True(copyButton.Classes.Contains("icon-action"));
            Assert.False(copyButton.Classes.Contains(MarkdownStyleKeys.CopyButton));

            // Geometry and centring follow the app's icon buttons.
            Assert.Equal(28d, copyButton.Width);
            Assert.Equal(28d, copyButton.Height);
            Assert.Equal(28d, copyButton.MinWidth);
            Assert.Equal(28d, copyButton.MinHeight);
            Assert.Equal(new Thickness(5), copyButton.Padding);
            Assert.Equal(new Thickness(0), copyButton.BorderThickness);
            Assert.Equal(HorizontalAlignment.Center, copyButton.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, copyButton.VerticalContentAlignment);

            var radius = Token<CornerRadius>(window, "InteractionCornerRadius");
            Assert.Equal(radius, copyButton.CornerRadius);

            // The renderer binds its own brushes at local priority, so the app palette
            // must still reach both the button and the template presenter.
            var presenter = copyButton.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(candidate => candidate.Name == "PART_ContentPresenter");
            Assert.Equal(default, presenter.CornerRadius);
            AssertTransparent(copyButton.Background);
            AssertTransparent(presenter.Background);

            // The glyph spans the icon's viewbox, so that viewbox is the drawn ink and
            // its centre has to sit on the button's centre on both axes.
            var viewbox = icon.GetVisualDescendants().OfType<Viewbox>().Single();
            var inkOrigin = viewbox.TranslatePoint(default, copyButton)!.Value;
            var inkCentre = new Point(
                inkOrigin.X + viewbox.Bounds.Width / 2,
                inkOrigin.Y + viewbox.Bounds.Height / 2);
            var buttonCentre = new Point(copyButton.Bounds.Width / 2, copyButton.Bounds.Height / 2);
            Assert.True(Math.Abs(inkCentre.X - buttonCentre.X) < 0.25, $"icon x {inkCentre.X} vs {buttonCentre.X}");
            Assert.True(Math.Abs(inkCentre.Y - buttonCentre.Y) < 0.25, $"icon y {inkCentre.Y} vs {buttonCentre.Y}");

            var point = copyButton.TranslatePoint(buttonCentre, window)!.Value;
            window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
            AssertTransparent(copyButton.Background);
            AssertTransparent(presenter.Background);

            window.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            AssertTransparent(presenter.Background);
            window.MouseUp(point, MouseButton.Left);
        }
        finally
        {
            window.Close();
        }
    }

    private static T Token<T>(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), $"Missing resource {key}");
        return Assert.IsType<T>(value);
    }

    private static void AssertTransparent(IBrush? actual)
    {
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
    }



}
