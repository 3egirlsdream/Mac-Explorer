using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using MacExplorer.Controls;
using MacExplorer.Models;
using Xunit;

namespace MacExplorer.Tests;

public sealed class HomeFolderCardTests
{
    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(100, true)]
    public void OnlyOverflowReservesTheFinalGridCell(int count, bool overflow)
    {
        var expanded = false;
        using var card = CreateCard(() => expanded = true);
        card.SetPaths(Enumerable.Range(0, count).Select(i => "/tmp/file" + i).ToArray());
        var root = Assert.IsType<Grid>(card.Content);
        var grid = Assert.IsType<UniformGrid>(Assert.IsType<Grid>(Assert.IsType<Border>(root.Children[1]).Child).Children[1]);
        Assert.Equal(8, grid.Children.Count);
        Assert.Equal(overflow, card.HasOverflow);
        Assert.Equal(overflow ? 7 : 8, card.PreviewCapacity);
        if (overflow)
        {
            var last = Assert.IsType<Button>(grid.Children[^1]);
            Assert.Contains("home-overflow", last.Classes);
            last.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(expanded);
        }
        else Assert.DoesNotContain(grid.Children.OfType<Button>(), button => button.Classes.Contains("home-overflow"));
    }

    [AvaloniaFact]
    public void ViewportResizeDoesNotPersistASmallerPreferredGrid()
    {
        using var card = CreateCard(() => { });
        var committed = 0;
        card.LayoutCommitted += _ => committed++;
        card.SetAvailableWidth(220);
        Assert.Equal(4, card.PreviewCapacity);
        Assert.Equal(new HomeFolderLayout(4, 2), card.PreferredLayout);
        card.SetAvailableWidth(120);
        Assert.Equal(2, card.PreviewCapacity);
        Assert.True(card.Width <= 120);
        card.SetAvailableWidth(1000);
        Assert.Equal(8, card.PreviewCapacity);
        Assert.Equal(0, committed);
    }

    [AvaloniaFact]
    public void FocusedResizeGripSupportsKeyboardAndCommitsOnce()
    {
        using var card = CreateCard(() => { });
        var committed = 0;
        card.LayoutCommitted += _ => committed++;
        var root = Assert.IsType<Grid>(card.Content);
        var grip = Assert.IsType<Button>(root.Children[2]);
        grip.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Assert.Equal(new HomeFolderLayout(5, 2), card.PreferredLayout);
        Assert.Equal(10, card.PreviewCapacity);
        Assert.Equal(1, committed);
    }


    [AvaloniaFact]
    public void CardBodyAndFileCellHaveSeparateActions()
    {
        Application.Current!.Styles.Insert(0, new Avalonia.Themes.Fluent.FluentTheme());
        var expanded = 0;
        var opened = 0;
        Button? item = null;
        using var card = new HomeFolderCard(new FileTag("Work", "#808080", FileTagKind.Custom), new(),
            entry => { item = new Button { Content = entry.Name }; item.Click += (_, _) => opened++; return item; },
            () => expanded++, () => { }, new ContextMenu());
        card.SetPaths(["/tmp/file"]);
        card.SetEntries([new FileSystemEntry { Name = "file", FullPath = "/tmp/file" }], default);
        var window = new Window { Content = card, Width = 600, Height = 400 };
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/HomeStyles.axaml")));
        window.Show();
        try
        {
            var point = card.TranslatePoint(new Point(42, 15), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Equal(1, expanded);
            var fileButton = Assert.IsType<Button>(item);
            point = fileButton.TranslatePoint(new Point(fileButton.Bounds.Width / 2, fileButton.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            var hit = window.InputHitTest(point);
            Assert.True(hit is Visual visual && (ReferenceEquals(visual, item) || visual.GetVisualAncestors().Contains(item)),
                $"Unexpected hit {hit}; ancestors={string.Join(",", (hit as Visual)?.GetVisualAncestors().Select(v => v.GetType().Name) ?? [])}; item={fileButton.Bounds}; point={point}");
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Equal(1, opened);
            Assert.Equal(1, expanded);
            card.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(2, expanded);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ResizeArcAppearsOnHoverAndKeyboardFocus()
    {
        Application.Current!.Styles.Insert(0, new Avalonia.Themes.Fluent.FluentTheme());
        using var card = CreateCard(() => { });
        var window = new Window { Content = card, Width = 600, Height = 400 };
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/HomeStyles.axaml")));
        window.Show();
        try
        {
            var grip = card.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("home-resize-grip"));
            window.MouseMove(new Point(500, 350));
            Assert.Equal(0, grip.Opacity);
            window.MouseMove(card.TranslatePoint(new Point(40, 15), window)!.Value);
            Assert.Equal(1, grip.Opacity);
            window.MouseMove(new Point(500, 350));
            Assert.Equal(0, grip.Opacity);
            grip.Focus();
            Assert.Equal(1, grip.Opacity);
        }
        finally { window.Close(); }
    }

    private static HomeFolderCard CreateCard(Action expand)
        => new(new FileTag("Work", "#808080", FileTagKind.Custom), new HomeFolderLayout(),
            entry => new Button { Content = entry.Name }, expand, () => { }, new ContextMenu());
}
