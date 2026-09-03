using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Views;
using Xunit;
using AssetIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.Tests;

public sealed class FinderSidebarInteractionTests
{
    [AvaloniaFact]
    public void SectionHeaderEmptySpaceRevealsRightSideActionsOnlyOnHover()
    {
        var sidebar = new FinderSidebarView();
        AddApplicationStyles(sidebar);
        var window = new Window
        {
            Width = 280,
            Height = 700,
            Content = sidebar
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var aiHeader = sidebar.FindControl<Grid>("AiSectionHeader")!;
        var collectionsHeader = sidebar.FindControl<Grid>("CollectionsSectionHeader")!;
        var tagsHeader = sidebar.FindControl<Grid>("TagsSectionHeader")!;
        var aiChevron = sidebar.FindControl<PathIcon>("AiChevron")!;
        var collectionsChevron = sidebar.FindControl<PathIcon>("CollChevron")!;
        var tagsChevron = sidebar.FindControl<PathIcon>("TagsChevron")!;
        var addCollectionButton = sidebar.FindControl<Button>("AddCollectionBtn")!;

        Assert.NotNull(aiHeader.Background);
        Assert.NotNull(collectionsHeader.Background);
        Assert.NotNull(tagsHeader.Background);
        Assert.Equal(0, aiChevron.Opacity);
        Assert.Equal(0, collectionsChevron.Opacity);
        Assert.Equal(0, tagsChevron.Opacity);
        Assert.Equal(0, addCollectionButton.Opacity);

        MoveToEmptyHeaderSpace(window, collectionsHeader);

        Assert.True(collectionsHeader.IsPointerOver);
        Assert.Equal(1, collectionsChevron.Opacity);
        Assert.Equal(1, addCollectionButton.Opacity);
        Assert.Equal(0, aiChevron.Opacity);
        Assert.Equal(0, tagsChevron.Opacity);

        window.Close();
    }

    [AvaloniaFact]
    public void CollectionCreateAndRenameUseTheItemRowAsTheOnlyEditor()
    {
        var collection = new Collection { Id = 7, Name = "项目资料" };
        var sidebar = new FinderSidebarView();
        AddApplicationStyles(sidebar);
        var collectionItems = sidebar.FindControl<ItemsControl>("CollectionItems")!;
        var collectionTemplate = Assert.IsAssignableFrom<IDataTemplate>(collectionItems.ItemTemplate);
        var existingRow = Assert.IsAssignableFrom<Border>(collectionTemplate.Build(collection));
        existingRow.DataContext = collection;
        sidebar.FindControl<StackPanel>("CollectionsPanel")!.Children.Insert(1, existingRow);

        var window = new Window
        {
            Width = 280,
            Height = 700,
            Content = sidebar
        };
        window.Show();
        sidebar.FindControl<StackPanel>("CollectionsPanel")!.IsVisible = true;
        Dispatcher.UIThread.RunJobs();

        AssertCollectionContentIsVerticallyCentered(existingRow);

        var renameButton = existingRow.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => Equals(ToolTip.GetTip(button), "重命名"));

        renameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        AssertCollectionRowIsEditing(existingRow, "项目资料");
        Assert.False(sidebar.FindControl<Border>("NewCollectionEditorRow")!.IsVisible);

        sidebar.FindControl<Button>("AddCollectionBtn")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.False(existingRow.GetVisualDescendants()
            .OfType<TextBox>()
            .Single(textBox => textBox.Classes.Contains("collection-name-editor"))
            .IsVisible);
        Assert.Equal(2, existingRow.GetVisualDescendants()
            .OfType<Button>()
            .Count(button => button.Classes.Contains("collection-normal-action") && button.IsVisible));

        var newRow = sidebar.FindControl<Border>("NewCollectionEditorRow")!;
        Assert.True(newRow.IsVisible);
        Assert.Single(newRow.GetVisualDescendants().OfType<PathIcon>(),
            pathIcon => pathIcon.Classes.Contains("sidebar-icon"));
        Assert.Equal("新收藏夹", sidebar.FindControl<TextBox>("NewCollectionInput")!.Text);
        AssertCollectionEditorMatchesFileRenameStyle(newRow);
        AssertCollectionContentIsVerticallyCentered(newRow);
        AssertSingleConfirmButton(newRow);

        window.Close();
    }

    [AvaloniaFact]
    public void ExternalVolumeRowsAlignWithBuiltInLocationRows()
    {
        var application = Assert.IsAssignableFrom<Application>(Application.Current);
        var fluentTheme = new FluentTheme();
        application.Styles.Insert(0, fluentTheme);
        Window? window = null;

        try
        {
            var sidebar = new FinderSidebarView();
            sidebar.SetRailMode(true);
            sidebar.SetRailMode(false);
            AddApplicationStyles(sidebar);
            var builtInRow = sidebar.FindControl<Border>("VolumeItem")!;
            var externalVolumes = sidebar.FindControl<ItemsControl>("ExternalVolumesControl")!;
            window = new Window
            {
                Width = 280,
                Height = 700,
                Content = sidebar
            };

            window.Show();
            builtInRow.IsVisible = true;
            externalVolumes.ItemsSource = new[]
            {
                new VolumeInfo
                {
                    Path = "/Volumes/Docker",
                    DisplayName = "Docker",
                    IsExternal = true,
                    IsRemovable = true
                }
            };
            externalVolumes.IsVisible = true;
            Dispatcher.UIThread.RunJobs();

            var builtInIcon = builtInRow.GetVisualDescendants()
                .OfType<PathIcon>()
                .Single(icon => icon.Classes.Contains("sidebar-icon"));
            var builtInText = builtInRow.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single();
            var externalText = externalVolumes.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(text => text.Text == "Docker");
            var externalRow = externalText.GetVisualAncestors()
                .OfType<Border>()
                .First(border => border.Classes.Contains("sidebar-item"));
            var externalIcon = externalRow.GetVisualDescendants()
                .OfType<PathIcon>()
                .Single(icon => icon.Classes.Contains("sidebar-icon"));

            var builtInIconOrigin = builtInIcon.TranslatePoint(default, sidebar);
            var externalIconOrigin = externalIcon.TranslatePoint(default, sidebar);
            var builtInTextOrigin = builtInText.TranslatePoint(default, sidebar);
            var externalTextOrigin = externalText.TranslatePoint(default, sidebar);

            Assert.NotNull(builtInIconOrigin);
            Assert.NotNull(externalIconOrigin);
            Assert.NotNull(builtInTextOrigin);
            Assert.NotNull(externalTextOrigin);
            Assert.Equal(builtInIconOrigin.Value.X, externalIconOrigin.Value.X, 3);
            Assert.Equal(builtInTextOrigin.Value.X, externalTextOrigin.Value.X, 3);
        }
        finally
        {
            window?.Close();
            application.Styles.Remove(fluentTheme);
        }
    }

    private static void MoveToEmptyHeaderSpace(Window window, Grid header)
    {
        var point = header.TranslatePoint(
            new Point(header.Bounds.Width / 2, header.Bounds.Height / 2),
            window);
        Assert.NotNull(point);
        window.MouseMove(point.Value, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void AddApplicationStyles(FinderSidebarView sidebar)
    {
        sidebar.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        sidebar.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
    }

    private static void AssertCollectionRowIsEditing(Border row, string expectedText)
    {
        var editor = row.GetVisualDescendants()
            .OfType<TextBox>()
            .Single(textBox => textBox.Classes.Contains("collection-name-editor"));
        var label = row.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(text => text.Classes.Contains("collection-name-label"));

        Assert.True(editor.IsVisible);
        Assert.Equal(expectedText, editor.Text);
        Assert.False(label.IsVisible);
        Assert.DoesNotContain(row.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("collection-normal-action") && button.IsVisible);
        AssertCollectionEditorMatchesFileRenameStyle(row);
        AssertCollectionContentIsVerticallyCentered(row);
        AssertSingleConfirmButton(row);
    }

    private static void AssertCollectionEditorMatchesFileRenameStyle(Border row)
    {
        var editor = row.GetVisualDescendants()
            .OfType<TextBox>()
            .Single(textBox => textBox.Classes.Contains("collection-name-editor"));

        Assert.Contains("inline-rename-editor", editor.Classes);
        Assert.Equal(22, editor.Height);
        Assert.Equal(72, editor.MinWidth);
        Assert.Equal(new Thickness(1), editor.BorderThickness);
        Assert.Equal(new CornerRadius(4), editor.CornerRadius);
        Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Left, editor.HorizontalAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, editor.VerticalAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, editor.VerticalContentAlignment);
        Assert.Null(editor.FocusAdorner);
    }

    private static void AssertCollectionContentIsVerticallyCentered(Border row)
    {
        var content = row.GetVisualDescendants()
            .OfType<Grid>()
            .Single(grid => grid.Classes.Contains("collection-row-content"));
        var icon = content.Children
            .OfType<PathIcon>()
            .Single(pathIcon => pathIcon.Classes.Contains("sidebar-icon"));
        var textControl = content.Children
            .Where(control => control.IsVisible)
            .Single(control => control is TextBlock { Classes: var classes }
                                   && classes.Contains("collection-name-label")
                               || control is TextBox { Classes: var editorClasses }
                                   && editorClasses.Contains("collection-name-editor"));
        var iconCenter = icon.TranslatePoint(new Point(0, icon.Bounds.Height / 2), content);
        var textCenter = textControl.TranslatePoint(
            new Point(0, textControl.Bounds.Height / 2),
            content);

        Assert.NotNull(iconCenter);
        Assert.NotNull(textCenter);
        Assert.InRange(Math.Abs(iconCenter.Value.Y - textCenter.Value.Y), 0, 0.5);
    }

    private static void AssertSingleConfirmButton(Border row)
    {
        var visibleButtons = row.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsVisible)
            .ToArray();
        var confirmButton = Assert.Single(visibleButtons);
        Assert.Contains("collection-confirm-action", confirmButton.Classes);
        var confirmIcon = Assert.Single(confirmButton.GetVisualDescendants().OfType<PathIcon>());
        Assert.Equal(
            StreamGeometry.Parse(AssetIcons.Checkmark).Bounds,
            confirmIcon.Data?.Bounds);
        Assert.DoesNotContain(confirmButton.GetVisualDescendants().OfType<TextBlock>(),
            textBlock => textBlock.Text == "\u2713");
    }
}
