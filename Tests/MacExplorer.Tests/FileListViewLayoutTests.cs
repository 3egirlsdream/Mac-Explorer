using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Controls;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using System.Reflection;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FileListViewLayoutTests
{
    [AvaloniaFact]
    public void PaneLayoutPickerIconsRenderAllTwelveLayouts()
    {
        var panel = new WrapPanel();
        foreach (var layout in Enum.GetValues<PaneLayout>())
            panel.Children.Add(new PaneLayoutIcon
            {
                Layout = layout,
                Width = 30,
                Height = 21,
                Foreground = Brushes.White
            });
        var window = new Window { Width = 180, Height = 100, Content = panel };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(12, panel.Children.Count);
        Assert.All(panel.Children.OfType<PaneLayoutIcon>(), icon =>
        {
            Assert.Equal(30, icon.Bounds.Width);
            Assert.Equal(21, icon.Bounds.Height);
        });
        window.Close();
    }

    [AvaloniaFact]
    public void GridCellGapIsCanvasButVisibleCardIsAnEntryTarget()
    {
        var entry = File("gap-test.txt");
        var view = new FileListView();
        var template = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["GridEntryTemplate"]);
        var card = Assert.IsAssignableFrom<Border>(template.Build(entry));
        card.DataContext = entry;
        var cell = new ListBoxItem { DataContext = entry, Content = card };
        var window = new Window { Width = 300, Height = 240, Content = cell };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(100, card.Width);
        Assert.Equal(new Thickness(10, 6), card.Margin);
        Assert.Null(FileListView.FindEntryContentInAncestors(cell));
        Assert.Null(FileListView.FindEntryContentInAncestors(card));
        var hitTargets = card.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("entry-content"))
            .ToArray();
        Assert.Equal(2, hitTargets.Length);
        Assert.All(hitTargets, target =>
            Assert.Same(entry, FileListView.FindEntryContentInAncestors(target)));

        window.Close();
    }

    [AvaloniaFact]
    public void SelectedGridContainerDoesNotPaintAFullCellBackground()
    {
        var entry = File("selected.zip");
        var view = new FileListView();
        var template = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["GridEntryTemplate"]);
        var card = Assert.IsAssignableFrom<Control>(template.Build(entry));
        card.DataContext = entry;
        var itemTheme = Assert.IsAssignableFrom<ControlTheme>(view.Resources["FileGridItemTheme"]);
        var item = new ListBoxItem
        {
            Theme = itemTheme,
            DataContext = entry,
            Content = card,
            IsSelected = true
        };
        var window = new Window { Width = 300, Height = 240, Content = item };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var background = Assert.IsAssignableFrom<ISolidColorBrush>(item.Background);
        Assert.Equal(0, background.Color.A);
        Assert.Equal(120, item.Width);
        Assert.Equal(new Thickness(0), item.Margin);

        window.Close();
    }

    [AvaloniaFact]
    public void GridEntryDisplaysTheMiddleAbbreviatedFileName()
    {
        var entry = File("a-very-long-file-name.csproj");
        var view = new FileListView();
        var template = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["GridEntryTemplate"]);
        var card = Assert.IsAssignableFrom<Control>(template.Build(entry));
        card.DataContext = entry;
        var window = new Window { Width = 300, Height = 240, Content = card };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var fileName = card.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Classes.Contains("entry-name-text"));
        Assert.Equal("a-very…csproj", fileName.Text);

        window.Close();
    }

    [AvaloniaFact]
    public void ListRowWhitespaceIsCanvasButVisibleColumnContentIsAnEntryTarget()
    {
        var entry = File("list-row.txt");
        var view = new FileListView();
        var row = Assert.IsAssignableFrom<Border>(BuildRow(view, entry));
        var window = new Window { Width = 760, Height = 240, Content = row };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(FileListView.FindEntryContentInAncestors(row));
        var hitTargets = row.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Classes.Contains("list-entry-hit"))
            .ToArray();
        Assert.Equal(5, hitTargets.Length);
        Assert.All(hitTargets, target =>
            Assert.Same(entry, FileListView.FindEntryContentInAncestors(target)));

        window.Close();
    }

    [AvaloniaFact]
    public void RealizedAndNewRowsUseTheSameEffectiveWidthsAsHeader()
    {
        var view = new FileListView();
        var surface = view.FindControl<Grid>("InteractionSurface")!;
        surface.Children.Add(BuildRow(view, File("first.txt")));
        var window = new Window { Width = 760, Height = 480, Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        AssertRowsMatchHeader(view, expectedRowCount: 1);

        surface.Children.Add(BuildRow(view, File("second.txt")));
        Dispatcher.UIThread.RunJobs();

        AssertRowsMatchHeader(view, expectedRowCount: 2);
        window.Close();
    }

    [AvaloniaFact]
    public void LongListFilenameCannotOverlapOrHitTheDateColumn()
    {
        var entry = File("VoHive-R106-VoWiFi-SMS-Transport-Priority-Fix-BuildFix2-OneRun-with-an-extra-long-name.zip");
        var view = new FileListView();
        var row = Assert.IsAssignableFrom<Border>(BuildRow(view, entry));
        view.FindControl<Grid>("InteractionSurface")!.Children.Add(row);
        var window = new Window { Width = 760, Height = 480, Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rowGrid = row.GetVisualDescendants().OfType<Grid>()
            .Single(grid => grid.Classes.Contains("file-list-row-grid"));
        var nameTarget = row.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("list-name-hit"));
        var dateTarget = row.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("list-entry-hit") && Grid.GetColumn(border) == 2);
        var nameOrigin = nameTarget.TranslatePoint(default, window)!.Value;
        var dateOrigin = dateTarget.TranslatePoint(default, window)!.Value;

        Assert.Equal(rowGrid.ColumnDefinitions[1].Width.Value - 16, nameTarget.MaxWidth, precision: 5);
        Assert.True(nameOrigin.X + nameTarget.Bounds.Width <= dateOrigin.X,
            $"Name ends at {nameOrigin.X + nameTarget.Bounds.Width}, date starts at {dateOrigin.X}");
        Assert.True(nameTarget.ClipToBounds);

        window.Close();
    }

    [AvaloniaFact]
    public void ShortListNameTargetLeavesColumnWhitespaceForMarquee()
    {
        var view = new FileListView();
        var row = Assert.IsAssignableFrom<Border>(BuildRow(view, File("IMG_2930.JPG")));
        var window = new Window { Width = 1900, Height = 480, Content = row };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var nameTarget = row.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("list-name-hit"));
        var rowGrid = row.GetVisualDescendants().OfType<Grid>()
            .Single(grid => grid.Classes.Contains("file-list-row-grid"));

        Assert.True(nameTarget.Bounds.Width < rowGrid.ColumnDefinitions[1].Width.Value - 16,
            $"Short name target filled the whole Name column: {nameTarget.Bounds.Width}");
        window.Close();
    }

    [AvaloniaFact]
    public void InlineRenameEditorIsCompactAndDoesNotStretchAcrossTheNameColumn()
    {
        var entry = File("short.txt");
        var view = new FileListView();
        view.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        view.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        var surface = view.FindControl<Grid>("InteractionSurface")!;
        surface.Children.Add(BuildRow(view, entry));
        var window = new Window { Width = 760, Height = 480, Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var renameMethod = typeof(FileListView).GetMethod(
            "OnRenameRequested",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(renameMethod);
        renameMethod.Invoke(view, [entry]);
        Dispatcher.UIThread.RunJobs();

        var editor = Assert.Single(
            view.GetVisualDescendants().OfType<TextBox>(),
            textBox => textBox.Classes.Contains("inline-rename-editor"));
        Assert.Equal(HorizontalAlignment.Left, editor.HorizontalAlignment);
        Assert.Equal(22, editor.Height);
        Assert.InRange(editor.Width, 72, 140);
        Assert.True(editor.Width < 420);
        Assert.Equal(new Thickness(1), editor.BorderThickness);
        Assert.Equal(new CornerRadius(4), editor.CornerRadius);
        Assert.Null(editor.FocusAdorner);

        window.Close();
    }

    private static void AssertRowsMatchHeader(FileListView view, int expectedRowCount)
    {
        var header = view.FindControl<Grid>("ListHeaderGrid")!;
        var expected = GetDataColumnWidths(header);
        var rows = view.GetVisualDescendants()
            .OfType<Grid>()
            .Where(grid => grid.Classes.Contains("file-list-row-grid"))
            .ToArray();

        Assert.Equal(expectedRowCount, rows.Length);
        Assert.All(rows, row => Assert.Equal(expected, GetDataColumnWidths(row)));
    }

    private static double[] GetDataColumnWidths(Grid grid) => grid.ColumnDefinitions
        .Skip(1)
        .Take(4)
        .Select(column => column.Width.Value)
        .ToArray();

    private static Control BuildRow(FileListView view, FileSystemEntry entry)
    {
        var template = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["ListEntryTemplate"]);
        var row = Assert.IsAssignableFrom<Control>(template.Build(entry));
        row.DataContext = entry;
        return row;
    }

    private static FileSystemEntry File(string name) => new()
    {
        FullPath = Path.Combine("/tmp", name),
        Name = name,
        Extension = ".txt"
    };

}
