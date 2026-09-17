using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Xunit;

namespace MacExplorer.Tests;

public sealed class GlobalSearchInputFocusTests
{
    public static TheoryData<string> EmbeddedInputClasses => new()
    {
        "search-input omnibox-input",
        "breadcrumb-input omnibox-input",
        "home-search-input omnibox-input"
    };

    private static Window CreateAppStyledWindow(Control content)
    {
        var window = new Window { Width = 1200, Height = 800, Content = content };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ThemeTokens.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Controls/SurfaceCard.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Controls/AppWindow.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        return window;
    }

    private static TextBox CreateEmbeddedInput(string inputClass)
    {
        var input = new TextBox { Name = "EmbeddedInput", FontSize = 16 };
        foreach (var name in inputClass.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            input.Classes.Add(name);
        return input;
    }

    private static Dictionary<string, string> CaptureGeometry(TextBox input)
    {
        var snapshot = new Dictionary<string, string>
        {
            ["input.bounds"] = input.Bounds.ToString(),
            ["input.thickness"] = input.BorderThickness.ToString(),
            ["input.padding"] = input.Padding.ToString(),
            ["input.background"] = input.Background?.ToString() ?? "null"
        };

        foreach (var border in input.GetVisualDescendants().OfType<Border>())
        {
            var key = $"template.border[{border.Name ?? border.GetHashCode().ToString()}]";
            snapshot[key + ".thickness"] = border.BorderThickness.ToString();
            snapshot[key + ".margin"] = border.Margin.ToString();
            snapshot[key + ".bounds"] = border.Bounds.ToString();
        }

        return snapshot;
    }

    [AvaloniaTheory]
    [MemberData(nameof(EmbeddedInputClasses))]
    public void EmbeddedInputKeepsItsGeometryWhenFocused(string inputClass)
    {
        var container = new Border
        {
            Width = 780,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1)
        };
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*") };
        var input = CreateEmbeddedInput(inputClass);
        var icon = new Border { Width = 16, Height = 16, Margin = new Thickness(1, 0, 9, 0) };
        Grid.SetColumn(input, 1);
        grid.Children.Add(icon);
        grid.Children.Add(input);
        container.Child = grid;

        var window = CreateAppStyledWindow(container);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var before = CaptureGeometry(input);

        input.Focus();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var after = CaptureGeometry(input);

        var changes = after
            .Where(kv => !before.TryGetValue(kv.Key, out var value) || value != kv.Value)
            .Select(kv => $"{kv.Key}: {before.GetValueOrDefault(kv.Key, "<missing>")} -> {kv.Value}")
            .ToList();
        Assert.True(changes.Count == 0,
            $"Focusing '{inputClass}' changed geometry:\n" + string.Join("\n", changes));
    }
}
