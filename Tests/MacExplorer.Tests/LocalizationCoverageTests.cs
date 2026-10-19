using Avalonia.LogicalTree;
using System.Text.Json;
using System.Text.RegularExpressions;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Localization")]
public sealed class LocalizationCoverageTests
{
    [Fact]
    public void ChineseLiteralLookupsHaveEnglishTranslations()
    {
        var failures = new List<string>();
        var english = LocalizationCatalog.GetPack(AppLanguage.English);
        var root = PluginTestEnvironment.Repository;
        var calls = new Regex("LocalizationText\\.Get\\((\"(?:\\\\.|[^\"\\\\])*\")");
        foreach (var directory in new[] { "Views", "Controls", "Services", "Models", "ViewModels" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
        foreach (Match call in calls.Matches(File.ReadAllText(file)))
        {
            var text = JsonSerializer.Deserialize<string>(call.Groups[1].Value)!;
            if (!Regex.IsMatch(text, "[\\u4e00-\\u9fff]")) continue;
            if (!LocalizationCatalog.LiteralKeys.TryGetValue(text, out var key)
                || LocalizationCatalog.Get(key, english) == text)
                failures.Add($"{Path.GetRelativePath(root, file)}: {text}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void BuiltInOpenWithLabelsTranslateWithoutChangingSavedOrCustomNames()
    {
        var finder = new OpenWithApp { BundleId = BuiltInOpenWithActions.RevealInFinderBundleId, Label = "在 Finder 中显示" };
        var terminal = new OpenWithApp { BundleId = BuiltInOpenWithActions.OpenInTerminalBundleId, Label = "在终端中打开" };
        var custom = new OpenWithApp { BundleId = "custom", Label = "我的应用" };
        using var language = new LocalizationService(new PluginTestEnvironment.MemorySettings(), () => AppLanguage.English);
        Assert.Equal("Show in Finder", finder.DisplayLabel);
        Assert.Equal("Open in Terminal", terminal.DisplayLabel);
        Assert.Equal("我的应用", custom.DisplayLabel);
        Assert.Equal("在 Finder 中显示", finder.Label);
        language.SetLanguage(AppLanguage.ChineseSimplified);
        Assert.Equal(finder.Label, finder.DisplayLabel);
        Assert.Equal(terminal.Label, terminal.DisplayLabel);
    }

    [Fact]
    public void ShortcutDefinitionsContextsAndValidationFollowTheSelectedLanguage()
    {
        using var service = new ShortcutService(new PluginTestEnvironment.MemorySettings());
        using var language = new LocalizationService(new PluginTestEnvironment.MemorySettings(), () => AppLanguage.English);
        foreach (var definition in service.Definitions)
        {
            Assert.DoesNotMatch("[\\u4e00-\\u9fff]", definition.DisplayName);
            Assert.DoesNotMatch("[\\u4e00-\\u9fff]", definition.DisplayGroup);
            Assert.DoesNotMatch("[\\u4e00-\\u9fff]", definition.DisplayContext ?? "");
        }
        Assert.False(service.TrySet(ShortcutIds.Copy, new(Avalonia.Input.Key.C, Avalonia.Input.KeyModifiers.None), out var error));
        Assert.Equal("The shortcut must include Cmd", error);
        language.SetLanguage(AppLanguage.ChineseSimplified);
        Assert.Equal("复制文件", service.Definitions.Single(d => d.Id == ShortcutIds.Copy).DisplayName);
        Assert.False(service.TrySet(ShortcutIds.Copy, new(Avalonia.Input.Key.C, Avalonia.Input.KeyModifiers.None), out error));
        Assert.Equal("组合必须包含 Cmd", error);
    }
}

[Trait("Kind", "UI")]
[Trait("Area", "Localization")]
public sealed class ShortcutLocalizationTests
{
    [Avalonia.Headless.XUnit.AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortcutPageRefreshesWithoutResettingBindingsOrRecording(bool dark)
    {
        using var theme = new FastListTestTheme();
        using var language = new LocalizationService(new PluginTestEnvironment.MemorySettings(), () => AppLanguage.English);
        using var shortcuts = new ShortcutService(new PluginTestEnvironment.MemorySettings());
        Assert.True(shortcuts.TrySet(ShortcutIds.PageSearch, new(Avalonia.Input.Key.J, Avalonia.Input.KeyModifiers.Meta), out _));
        var view = new MacExplorer.Controls.ShortcutSettingsView(shortcuts);
        var window = new MacExplorer.Controls.DialogWindow
        {
            Content = view, Width = 780, Height = 600,
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light
        };
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        window.Styles.Add((Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml")));
        window.Show();
        try
        {
            var button = view.GetLogicalDescendants().OfType<Avalonia.Controls.Button>()
                .Single(x => x.Name == "ShortcutBinding_search_page");
            Assert.Equal("J", button.Content);
            Assert.Contains(view.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>(), x => x.Text == "Search and tabs");
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.True(shortcuts.IsRecording);
            foreach (var code in new[] { AppLanguage.ChineseSimplified, "zh-Hant", "fr", AppLanguage.English })
            {
                language.SetLanguage(code);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.True(shortcuts.IsRecording);
                Assert.Equal(LocalizationText.Get("按键…"), button.Content);
                Assert.Equal(Avalonia.Input.Key.J, shortcuts.GetBindings(ShortcutIds.PageSearch)[0].Key);
                if (code == AppLanguage.English)
                {
                    var text = view.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Select(x => x.Text ?? "");
                    Assert.All(text, value => Assert.DoesNotMatch("[\\u4e00-\\u9fff]", value));
                    Assert.Contains(view.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>(), x => x.Text == "Copy files");
                }
            }
        }
        finally { window.Close(); }
        Assert.False(shortcuts.IsRecording);
    }
}
