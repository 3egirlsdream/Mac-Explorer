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

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Sidebar")]
public sealed class FinderSidebarInteractionTests
{
    [AvaloniaFact(Explicit = true)]
    public void SidebarKeepsAiChevronAndMovesTagManagementOutOfSidebar()
    {
        var sidebar = new FinderSidebarView();
        AddApplicationStyles(sidebar);
        var window = new Window { Width = 280, Height = 700, Content = sidebar };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var header = sidebar.FindControl<Grid>("AiSectionHeader")!;
            var chevron = sidebar.FindControl<PathIcon>("AiChevron")!;
            Assert.NotNull(header.Background);
            Assert.Equal(0.65, chevron.Opacity);
            MoveToEmptyHeaderSpace(window, header);
            Assert.True(header.IsPointerOver); Assert.Equal(1, chevron.Opacity);
            Assert.Null(sidebar.FindControl<Control>("TagsSectionHeader"));
            Assert.Null(sidebar.FindControl<Control>("TagItems"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
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

}
