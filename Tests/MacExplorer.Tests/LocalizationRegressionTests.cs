using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lang.Avalonia;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.PluginSdk;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.FileConversion.Plugin;
using Xunit;

namespace MacExplorer.Tests;

public sealed class LocalizationRegressionTests
{
    [Theory]
    [InlineData("en-GB", AppLanguage.English)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("zh-Hans-CN", AppLanguage.ChineseSimplified)]
    [InlineData("zh-Hant", AppLanguage.ChineseSimplified)]
    [InlineData("fr-FR", AppLanguage.ChineseSimplified)]
    [InlineData(null, AppLanguage.ChineseSimplified)]
    public void SystemPreferenceMappingUsesTheSupportedLanguages(string? preferred, AppLanguage expected)
        => Assert.Equal(expected, LocalizationService.MapSystemLanguage(preferred));

    [Fact]
    public void FirstLaunchFollowsSystemAndPersistedOverrideSurvivesRecreation()
    {
        var settings = new MemorySettings();
        using (var first = new LocalizationService(settings, () => AppLanguage.English))
        {
            Assert.Equal(AppLanguage.System, first.Language);
            Assert.Equal("en-US", first.Culture.Name);
            Assert.Null(settings.Get(LocalizationService.SettingKey));
            first.SetLanguage(AppLanguage.ChineseSimplified);
        }
        using var reopened = new LocalizationService(settings, () => AppLanguage.English);
        Assert.Equal(AppLanguage.ChineseSimplified, reopened.Language);
        Assert.Equal("zh-CN", reopened.Culture.Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => reopened.SetLanguage((AppLanguage)99));
        Assert.Equal("unknown.key", reopened["unknown.key"]);
    }

    [AvaloniaFact]
    public void MarkupBindingUpdatesMultipleWindowsAndAccessibilityWithoutDataContext()
    {
        using var language = new LocalizationService(new MemorySettings(), () => AppLanguage.ChineseSimplified);
        var extension = new LocExtension("settings.language");
        var first = new Window();
        var second = new Window();
        var button = new Button();
        first.Content = button;
        first.Show();
        second.Show();
        first.Bind(Window.TitleProperty, (Binding)extension.ProvideValue(null!));
        second.Bind(Window.TitleProperty, (Binding)extension.ProvideValue(null!));
        button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, (Binding)extension.ProvideValue(null!));
        Assert.Equal("应用语言", first.Title);
        language.SetLanguage(AppLanguage.English);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Language", first.Title);
        Assert.Equal("Language", second.Title);
        Assert.Equal("Language", Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.Equal(BindingMode.OneWay, ((Binding)extension.ProvideValue(null!)).Mode);
        Assert.Equal("en-US", I18nManager.Instance.Culture.Name);
        language.SetLanguage(AppLanguage.ChineseSimplified);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("应用语言", second.Title);
        Assert.Equal("zh-CN", I18nManager.Instance.Culture.Name);
        first.Close();
        second.Close();
    }

    [Fact]
    public void DatesAndStatusUseInterfaceCultureEvenInAnOlderExecutionContext()
    {
        using var language = new LocalizationService(new MemorySettings(), () => AppLanguage.ChineseSimplified);
        var file = new FileSystemEntry { Name = "sample.txt", LastModified = new DateTime(2026, 10, 9, 16, 30, 0) };
        var chinese = file.ModifiedText;
        var oldContext = ExecutionContext.Capture()!;
        language.SetLanguage(AppLanguage.English);
        ExecutionContext.Run(oldContext, _ =>
        {
            Assert.Equal(file.LastModified.ToString("g", language.Culture), file.ModifiedText);
            Assert.NotEqual(chinese, file.ModifiedText);
            Assert.Equal("1 items", FileListStatusFormatter.FormatSelectionSummary([file], []));
        }, null);
    }

    [AvaloniaFact]
    public void AlreadyDrawnFileRowsDiscardOldCultureText()
    {
        using var language = new LocalizationService(new MemorySettings(), () => AppLanguage.ChineseSimplified);
        var list = new FastFileList();
        list.SetRows([FastFileListTests.Entry(1)]);
        var window = new Window { Width = 900, Height = 300, Content = list };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using (var drawing = new Avalonia.Media.DrawingGroup().Open()) list.Render(drawing);
            Assert.True(list.CachedTextCount > 0);
            language.SetLanguage(AppLanguage.English);
            Assert.Equal(0, list.CachedTextCount);
            using (var drawing = new Avalonia.Media.DrawingGroup().Open()) list.Render(drawing);
            Assert.True(list.CachedTextCount > 0);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void EveryResourceHasMatchingPlaceholderIndicesAndValidCompositeFormat()
    {
        Assert.Equal(LocalizationCatalog.Zh.Keys.Order(), LocalizationCatalog.En.Keys.Order());
        foreach (var key in LocalizationCatalog.Zh.Keys)
            VerifyFormats(LocalizationCatalog.Zh[key], LocalizationCatalog.En[key]);
        foreach (var entry in LocalizationCatalog.Ui.Values)
            VerifyFormats(entry.Chinese, entry.English);
    }

    private static void VerifyFormats(string chinese, string english)
    {
        Assert.Equal(CompositeFormat.Parse(chinese).MinimumArgumentCount, CompositeFormat.Parse(english).MinimumArgumentCount);
        static string[] Indices(string format) => Regex.Matches(format, @"\{(\d+)(?:[,:][^}]*)?\}")
            .Select(match => match.Groups[1].Value).Order().ToArray();
        Assert.Equal(Indices(chinese), Indices(english));
    }

    [Fact]
    public void PluginCultureIsAdditiveToApiV1AndOldRequestsDefaultToChinese()
    {
        var old = JsonSerializer.Deserialize<PluginInvocation>("""
            {"invocationId":"id","commandId":"to-docx","files":[],"workDirectory":"/tmp"}
            """, PluginProtocol.Json)!;
        Assert.Equal("zh-CN", old.Culture);
        Assert.NotNull(typeof(PluginInvocation).GetConstructor([typeof(string), typeof(string), typeof(PluginFile[]), typeof(string), typeof(Dictionary<string, JsonElement>)]));
        var (id, command, files, work, parameters) = old;
        Assert.Equal("id", id);
        var roundtrip = JsonSerializer.Deserialize<PluginInvocation>(JsonSerializer.Serialize(old with { Culture = "en-US" }, PluginProtocol.Json), PluginProtocol.Json)!;
        Assert.Equal("en-US", roundtrip.Culture);
    }

    [Fact]
    public async Task ConcurrentConversionInvocationsKeepTheirOwnWarningsAndRestoreCulture()
    {
        var original = CultureInfo.CurrentUICulture;
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-localization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source.md");
            await File.WriteAllTextAsync(path, "![sample](missing.png)");
            var plugin = new ConversionPlugin();
            var english = new PluginInvocation("en", "to-docx", [new(path)], Path.Combine(root, "en")) { Culture = "en-US" };
            var chinese = english with { InvocationId = "zh", WorkDirectory = Path.Combine(root, "zh"), Culture = "zh-CN" };
            var results = await Task.WhenAll(
                plugin.ExecuteAsync(english, new Progress<PluginProgress>(), TestContext.Current.CancellationToken),
                plugin.ExecuteAsync(chinese, new Progress<PluginProgress>(), TestContext.Current.CancellationToken));
            Assert.Contains(results[0].Warnings, warning => warning.StartsWith("Image not loaded:", StringComparison.Ordinal));
            Assert.Contains(results[1].Warnings, warning => warning.StartsWith("图片未加载：", StringComparison.Ordinal));
            Assert.Equal(original, CultureInfo.CurrentUICulture);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.PrepareAsync(
                english with { Files = [] }, TestContext.Current.CancellationToken));
            Assert.Equal("Select at least one local file.", failure.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NativeConversionErrorsUseInvocationLanguage()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-native-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "corrupt.webp");
            await File.WriteAllBytesAsync(source, [0, 1, 2]);
            var invocation = new PluginInvocation("en", "to-png", [new(source)], root) { Culture = "en-US" };
            var error = await Assert.ThrowsAsync<IOException>(() => new ConversionPlugin().PrepareAsync(invocation, TestContext.Current.CancellationToken));
            Assert.Contains(error.Message, new[] { "Unable to read the image file", "Unable to decode the WebP image" });
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        public void Set(string key, string value) { _values[key] = value; SettingChanged?.Invoke(key); }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }
}
