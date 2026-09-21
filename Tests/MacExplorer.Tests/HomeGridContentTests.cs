using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class HomeGridContentTests
{
    [Theory]
    [InlineData(0, "最近添加")]
    [InlineData(5, "5 分钟前")]
    [InlineData(120, "2 小时前")]
    [InlineData(2880, "2 天前")]
    public void AddedTimeUsesRelativeLabels(int minutes, string expected)
    {
        var now = DateTime.UtcNow;
        Assert.Equal(expected, HomeItemActions.FormatAddedTime(now.AddMinutes(-minutes), now));
    }

    [AvaloniaFact]
    public void FileAndOverflowHoverSurfacesAreSquare()
    {
        var file = new Button { Classes = { "home-grid-item" } };
        var overflow = new Button { Classes = { "home-grid-item", "home-overflow" } };
        var window = new Window { Width = 400, Height = 300,
            Content = new StackPanel { Children = { file, overflow } } };
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/HomeStyles.axaml")));
        window.Show();
        try
        {
            Assert.Equal(72, file.Bounds.Width);
            Assert.Equal(file.Bounds.Width, file.Bounds.Height);
            Assert.Equal(file.Bounds.Size, overflow.Bounds.Size);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DifferentImageShapesShareTheSameIconSlotAndLabelBaseline()
    {
        var labels = new List<TextBlock>();
        foreach (var height in new[] { 24d, 40d, 48d })
        {
            var label = new TextBlock { Text = "File", FontSize = 12 };
            var content = HomeItemActions.CreateGridContent(new Border { Width = 48, Height = height }, label);
            content.Measure(new Size(68, 68));
            content.Arrange(new Rect(0, 0, 68, 68));
            Assert.Equal(48, content.Children[0].Bounds.Height);
            labels.Add(label);
        }
        Assert.All(labels, label => Assert.Equal(labels[0].Bounds.Y, label.Bounds.Y));
    }
}
