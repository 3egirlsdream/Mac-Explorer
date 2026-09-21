using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public sealed class HomeFolderTransitionTests
{
    [Theory]
    [InlineData(1200, 800)]
    [InlineData(600, 900)]
    [InlineData(1000, 400)]
    public void ExpandedSheetKeepsAspectRatioWithinViewport(double width, double height)
    {
        var size = HomeFolderTransition.GetSheetSize(new Size(width, height));
        Assert.Equal(1.5, size.Width / size.Height, 6);
        Assert.True(size.Width <= width - 64);
        Assert.True(size.Height <= height - 64);
    }

    [AvaloniaFact]
    public async Task ClosingDuringExpansionRestoresSourceAndCompletes()
    {
        var source = new Border { Width = 220, Height = 180, Opacity = .8 };
        var root = new Grid { Children = { source } };
        var window = new Window { Content = root, Width = 800, Height = 600 };
        window.Show();
        using var transition = new HomeFolderTransition(source, new TextBox());
        root.Children.Add(transition);
        root.UpdateLayout();
        try
        {
            Assert.Equal(0, transition.Opacity);
            Assert.Equal(.8, source.Opacity);
            transition.Open();
            Assert.Equal(1, transition.Opacity);
            Assert.Equal(0, source.Opacity);
            transition.Close(); transition.Close();
            await transition.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(.8, source.Opacity);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RepeatedOpenCloseAndInterruptedTransitionsReleaseOverlay()
    {
        var source = new Border { Width = 220, Height = 180, Opacity = .8 };
        var root = new Grid { Children = { source } };
        var window = new Window { Content = root, Width = 800, Height = 600 };
        window.Show();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                using var transition = new HomeFolderTransition(source, new Border());
                root.Children.Add(transition);
                root.UpdateLayout();
                transition.Open();
                transition.Open(); // Repeated activation must not allocate another capture.
                if (i % 3 == 0) await Task.Delay(450);
                if (i % 3 == 1) transition.Dispose();
                else { transition.Close(); transition.Close(); }
                await transition.Completion.WaitAsync(TimeSpan.FromSeconds(3));
                root.Children.Remove(transition);
                Assert.Equal(.8, source.Opacity);
                Assert.Single(root.Children);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CancellationBeforeFirstFrameDoesNotHideSource()
    {
        var source = new Border { Opacity = .7 };
        using var transition = new HomeFolderTransition(source, new TextBox());
        transition.Dispose(); transition.Open();
        Assert.Equal(.7, source.Opacity);
        Assert.True(transition.Completion.IsCompletedSuccessfully);
    }
}
