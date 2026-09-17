using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeWF.Markdown;
using MacExplorer.Assets;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class MarkdownPreviewTests
{
    [AvaloniaFact]
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
                .Single(button => button.Classes.Contains(MarkdownStyleKeys.CopyButton));

            var icon = Assert.IsType<PathIcon>(copyButton.Content);
            Assert.Equal(Geometry.Parse(Icons.Copy).Bounds, icon.Data!.Bounds);
            Assert.Equal("复制", AutomationProperties.GetName(copyButton));
            Assert.Equal("复制", ToolTip.GetTip(copyButton));
            Assert.True(copyButton.Classes.Contains("md-copy"));

            // Geometry and centring follow the app's icon buttons.
            Assert.Equal(28d, copyButton.Width);
            Assert.Equal(28d, copyButton.Height);
            Assert.Equal(28d, copyButton.MinWidth);
            Assert.Equal(28d, copyButton.MinHeight);
            Assert.Equal(default, copyButton.Padding);
            Assert.Equal(new Thickness(0), copyButton.BorderThickness);
            Assert.Equal(HorizontalAlignment.Center, copyButton.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, copyButton.VerticalContentAlignment);

            var radius = Token<CornerRadius>(window, "InteractionCornerRadius");
            Assert.Equal(radius, copyButton.CornerRadius);

            // The renderer binds its own brushes at local priority, so the app palette
            // must still reach both the button and the template presenter.
            var presenter = copyButton.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(candidate => candidate.Name == "PART_ContentPresenter");
            Assert.Equal(radius, presenter.CornerRadius);
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
            AssertBrush(window, "InteractionHoverBrush", copyButton.Background);
            AssertBrush(window, "InteractionHoverBrush", presenter.Background);

            window.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            AssertBrush(window, "InteractionHoverBrush", presenter.Background);
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

    private static void AssertBrush(Window window, string key, IBrush? actual)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var expected), $"Missing resource {key}");
        Assert.Equal(
            Assert.IsAssignableFrom<ISolidColorBrush>(expected).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
    }
}
