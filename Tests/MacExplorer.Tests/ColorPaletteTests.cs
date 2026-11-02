using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "ColorPalette")]
public sealed class ColorPaletteTests
{
    [AvaloniaFact]
    public void SwatchesRenderWithSizeAndHexDraftsDoNotOverwriteTheCaretOrPersistPartialColors()
    {
        var palette = new ColorPalette();
        palette.Configure(["#FFFFFF", "#3B82F6", "#22C55E"], "#FFFFFF");
        var changes = new List<string>();
        palette.ColorChanged += changes.Add;
        var window = InputAppearanceTests.CreateWindow(palette, false, 480);
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var dots = palette.GetVisualDescendants().OfType<Border>().Where(dot => dot.Classes.Contains("color-palette-dot")).ToArray();
            Assert.Equal(3, dots.Length);
            Assert.All(dots, dot => { Assert.Equal(18, dot.Bounds.Width); Assert.Equal(18, dot.Bounds.Height); Assert.NotNull(dot.Background); });
            var input = palette.InputBox;
            input.Text = "#abc"; Dispatcher.UIThread.RunJobs();
            Assert.Empty(changes);
            Assert.Equal("#FFFFFF", palette.SelectedColor);
            input.Text = "#abcdef"; input.CaretIndex = 5; Dispatcher.UIThread.RunJobs();
            Assert.Equal("#abcdef", input.Text);
            Assert.Equal(5, input.CaretIndex);
            Assert.Empty(changes);
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(5, input.CaretIndex);
            Assert.Equal("#ABCDEF", Assert.Single(changes));
            input.Text = "invalid"; Dispatcher.UIThread.RunJobs();
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.True(palette.FindControl<TextBlock>("ErrorText")!.IsVisible);
            Assert.Single(changes);
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Equal("#ABCDEF", input.Text);
            Assert.False(palette.FindControl<TextBlock>("ErrorText")!.IsVisible);
            palette.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Tag, "#3B82F6"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("#3B82F6", palette.SelectedColor);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AlphaValidationAndResetAreScopedToTheConfiguredOwner()
    {
        var palette = new ColorPalette();
        palette.Configure(["#FFFFFF"], "#FFFFFF", allowAlpha: false);
        var changes = new List<string>();
        palette.ColorChanged += changes.Add;
        palette.InputBox.Text = "#22000000"; Dispatcher.UIThread.RunJobs();
        Assert.Empty(changes);
        palette.Configure(["#FFFFFF"], "#22000000", allowAlpha: true);
        Assert.Equal("#22000000", palette.SelectedColor);
        palette.ResetRequested += () => palette.SetSelection("#FFFFFF");
        palette.FindControl<Button>("ResetButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("#FFFFFF", palette.SelectedColor);
        Assert.Empty(changes);
    }
}
