using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MacExplorer.Services.Markdown;
using MacExplorer.Views;

namespace MacExplorer.Tests;

public sealed class TestApplication : Application
{
    public TestApplication()
    {
        // Mirror App startup so Markdown rendering resolves its translations.
        MarkdownLocalization.Register();
        Resources["BoolNotConverter"] = new BoolNotConverter();
        Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/TypographyTokens.axaml")));
        Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ThemeTokens.axaml")));
        Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Controls/AppWindow.axaml")));
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<TestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
