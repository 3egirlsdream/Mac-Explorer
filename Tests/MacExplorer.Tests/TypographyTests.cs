using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "Typography")]
public sealed class TypographyTests
{
    [AvaloniaFact]
    public void PresetsAlwaysScaleFromTheStandardTokensAndPersist()
    {
        var settings = new InMemorySettingsService();
        var service = new TypographyService(settings);
        var changes = new List<FontSizePreset>();
        service.TypographyChanged += (_, args) => changes.Add(args.Preset);

        service.Initialize();
        Assert.Equal(FontSizePreset.Standard, service.CurrentPreset);
        AssertResource("FontSizeBody", 13);
        AssertResource("TypographyListRowMinHeight", 28);

        service.SetPreset(FontSizePreset.Large);
        AssertResource("FontSizeBody", 15);
        AssertResource("FontSizeLabel", 14);
        AssertResource("LineHeightBody", 23);
        AssertResource("TypographyListRowMinHeight", 32);

        service.SetPreset(FontSizePreset.Small);
        AssertResource("FontSizeBody", 12);
        AssertResource("FontSizeLabel", 11);
        AssertResource("LineHeightBody", 18);
        AssertResource("TypographyListRowMinHeight", 28);

        Assert.Equal("small", settings.Get(TypographyService.SettingKey));
        Assert.Equal([FontSizePreset.Large, FontSizePreset.Small], changes);
    }

    [AvaloniaFact]
    public void StoredPresetIsRestoredAndInvalidValueFallsBackToStandard()
    {
        var restored = new TypographyService(new InMemorySettingsService
        {
            [TypographyService.SettingKey] = "large"
        });

        restored.Initialize();

        Assert.Equal(FontSizePreset.Large, restored.CurrentPreset);
        AssertResource("FontSizeBody", 15);

        var invalid = new TypographyService(new InMemorySettingsService
        {
            [TypographyService.SettingKey] = "extra-large"
        });

        Assert.Equal(FontSizePreset.Standard, invalid.CurrentPreset);
    }

    [AvaloniaFact]
    public void ExistingFileListGroupAndSidebarTextUpdateWithoutRecreation()
    {
        var service = new TypographyService(new InMemorySettingsService());
        service.Initialize();

        var application = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(application.TryGetResource("FontFamilyUi", application.ActualThemeVariant, out var fontResource));
        Assert.Equal("System Font", Assert.IsType<FontFamily>(fontResource).Name);
        Assert.True(application.TryGetResource("FontFamilyButton", application.ActualThemeVariant, out var buttonFontResource));
        Assert.Equal("System Font", Assert.IsType<FontFamily>(buttonFontResource).Name);

        var fileList = new FileListView();
        var fileName = fileList.FindControl<MacExplorer.Controls.FastFileList>("FastList")!;

        var sidebar = new FinderSidebarView();
        var remoteServers = sidebar.FindControl<ItemsControl>("NetworkLocationsList")!;
        var remoteTemplate = Assert.IsAssignableFrom<IDataTemplate>(remoteServers.ItemTemplate);
        var remoteRow = Assert.IsAssignableFrom<Control>(remoteTemplate.Build(new NetworkLocationRow("fixture", "fixture")));
        remoteRow.DataContext = new NetworkLocationRow("198.51.100.42", "fixture", Connected: true);
        var textBox = new TextBox { Text = "动态字号" };
        var host = new StackPanel
        {
            Children = { fileList, sidebar, remoteRow, textBox }
        };
        var window = new Window { Width = 900, Height = 700, Content = host };
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sidebarTitle = sidebar.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("sidebar-section-title"));
        var sidebarItem = sidebar.FindControl<Border>("VolumeItem")!;
        var sidebarItemText = sidebarItem.GetVisualDescendants().OfType<TextBlock>().Single();
        var remoteAddress = remoteRow.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "198.51.100.42");
        var remoteAction = remoteRow.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("sidebar-item-action"));

        Assert.Equal(13, fileName.FontSize);
        Assert.Equal("System Font", fileName.FontFamily.Name);
        Assert.Equal(TextRenderingMode.Antialias, TextOptions.GetTextRenderingMode(window));
        Assert.Equal(TextHintingMode.Strong, TextOptions.GetTextHintingMode(window));
        Assert.True(window.TryFindResource("TextPrimaryBrush", window.ActualThemeVariant, out var primaryBrush));
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(primaryBrush).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(fileName.Foreground).Color);
        Assert.Equal(12, fileName.DetailFontSize);
        Assert.Equal(11, sidebarTitle.FontSize);
        Assert.Equal("System Font", sidebarTitle.FontFamily.Name);
        Assert.Equal(FontWeight.Medium, sidebarTitle.FontWeight);
        Assert.Equal(FontWeight.Normal, sidebarItemText.FontWeight);
        Assert.Equal(sidebarItemText.FontWeight, fileName.FontWeight);
        Assert.True(window.TryFindResource("TextSecondaryBrush", window.ActualThemeVariant, out var secondaryBrush));
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(secondaryBrush).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(sidebarTitle.Foreground).Color);
        Assert.Equal(sidebarItemText.FontSize, remoteAddress.FontSize);
        Assert.Single(remoteRow.GetVisualDescendants().OfType<TextBlock>());
        Assert.Equal(0, remoteAction.Opacity);
        Assert.False(remoteAction.IsHitTestVisible);

        service.SetPreset(FontSizePreset.Large);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(15, fileName.FontSize);
        Assert.Equal(14, fileName.DetailFontSize);
        Assert.Equal(13, sidebarTitle.FontSize);
        Assert.Equal(sidebarItemText.FontSize, remoteAddress.FontSize);
        Assert.Equal(32, sidebarItem.MinHeight);
        Assert.Equal(39, textBox.MinHeight);

        window.Close();
    }

    [AvaloniaFact]
    public void SettingsCopilotAndSubscriptionFollowSharedTypography()
    {
        var service = new TypographyService(new InMemorySettingsService());
        service.Initialize();
        var command = new TextBox { Text = "echo 中文 && pwd", Classes = { "settings-command-editor" } };
        var sectionTitle = new TextBlock { Text = "外观", Classes = { "settings-section-title" } };
        var toolTitle = new TextBlock { Text = "工具执行 Tool", Classes = { "copilot-tool-title" } };
        var subscription = new SubscriptionView();
        var window = new Window
        {
            Width = 440, Height = 700,
            Content = new StackPanel { Children = { sectionTitle, command, toolTitle, subscription } }
        };
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml")));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var price = subscription.FindControl<TextBlock>("PriceText")!;
            Assert.Equal(12, sectionTitle.FontSize);
            Assert.Equal(FontWeight.Normal, sectionTitle.FontWeight);
            Assert.Equal(new Thickness(10, 0, 0, 0), sectionTitle.Margin);
            Assert.Equal("Menlo", command.FontFamily.Name);
            Assert.Equal(FontWeight.Normal, command.FontWeight);
            Assert.Equal("System Font", toolTitle.FontFamily.Name);
            Assert.Equal(FontWeight.SemiBold, toolTitle.FontWeight);
            Assert.Equal(FontWeight.SemiBold, price.FontWeight);
            Assert.Equal(12, command.FontSize);
            Assert.Equal(12, toolTitle.FontSize);
            Assert.Equal(24, price.FontSize);

            service.SetPreset(FontSizePreset.Large);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(14, sectionTitle.FontSize);
            Assert.Equal(14, command.FontSize);
            Assert.Equal(14, toolTitle.FontSize);
            Assert.Equal(28, price.FontSize);
            Assert.Equal(FontWeight.SemiBold, toolTitle.FontWeight);
        }
        finally { window.Close(); }
    }

    private static void AssertResource(string key, double expected)
    {
        var application = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(application.TryGetResource(key, application.ActualThemeVariant, out var value));
        Assert.Equal(expected, Assert.IsType<double>(value));
    }

}
