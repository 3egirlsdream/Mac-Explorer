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

[Trait("Area", "Localization")]
public sealed class LocalizationRegressionTests
{
    [Theory]
    [InlineData("en-GB", AppLanguage.English)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("zh-Hans-CN", AppLanguage.ChineseSimplified)]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hant-HK", "zh-Hant")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-CA", "fr")]
    [InlineData("fr-BE", "fr")]
    [InlineData("zz-ZZ", AppLanguage.ChineseSimplified)]
    [InlineData(null, AppLanguage.ChineseSimplified)]
    [Trait("Kind", "Unit")]
    public void SystemPreferenceMappingUsesTheSupportedLanguages(string? preferred, string expected)
        => Assert.Equal(expected, LocalizationService.MapSystemLanguage(preferred));

    [Fact]
    [Trait("Kind", "Unit")]
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
        Assert.Throws<ArgumentOutOfRangeException>(() => reopened.SetLanguage("unsupported-language"));
        Assert.Equal("unknown.key", reopened["unknown.key"]);
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
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
        Assert.Equal("显示语言", first.Title);
        language.SetLanguage(AppLanguage.English);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Display language", first.Title);
        Assert.Equal("Display language", second.Title);
        Assert.Equal("Display language", Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.Equal(BindingMode.OneWay, ((Binding)extension.ProvideValue(null!)).Mode);
        Assert.Equal("en-US", I18nManager.Instance.Culture.Name);
        language.SetLanguage(AppLanguage.ChineseSimplified);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("显示语言", second.Title);
        Assert.Equal("zh-CN", I18nManager.Instance.Culture.Name);
        first.Close();
        second.Close();
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("fr", "Copier")]
    [InlineData("zh-Hant", "複製")]
    [Trait("Kind", "UI")]
    public void MarkdownRendererUsesTheSelectedNewLanguage(string code, string expectedCopy)
    {
        using var service = new LocalizationService(new MemorySettings(), () => AppLanguage.ChineseSimplified);
        service.SetLanguage(code);
        Assert.Equal(expectedCopy, I18nManager.Instance.GetResource("CodeWF.MarkdownL.Copy"));
    }

    [Fact]
    [Trait("Kind", "Unit")]
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

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
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
    [Trait("Kind", "Unit")]
    public void EveryResourceHasMatchingPlaceholderIndicesAndValidCompositeFormat()
    {
        var chinese = LocalizationCatalog.GetPack(AppLanguage.ChineseSimplified).Strings;
        var english = LocalizationCatalog.GetPack(AppLanguage.English).Strings;
        Assert.Equal(chinese.Keys.Order(), english.Keys.Order());
        foreach (var option in LocalizationCatalog.Languages)
        {
            var pack = LocalizationCatalog.GetPack(option.Id);
            Assert.Equal(pack.CultureName, pack.Culture.Name);
            foreach (var entry in pack.Strings)
                VerifyFormats(chinese[entry.Key], entry.Value);
        }
    }

    private static void VerifyFormats(string chinese, string english)
    {
        Assert.Equal(CompositeFormat.Parse(chinese).MinimumArgumentCount, CompositeFormat.Parse(english).MinimumArgumentCount);
        static string[] Indices(string format) => Regex.Matches(format, @"\{(\d+)(?:[,:][^}]*)?\}")
            .Select(match => match.Groups[1].Value).Order().ToArray();
        Assert.Equal(Indices(chinese), Indices(english));
    }

    [Fact]
    [Trait("Kind", "Unit")]
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
    [Trait("Kind", "Unit")]
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

    [Theory]
    [InlineData("fr-FR", "Sélectionnez au moins un fichier local.", "Le document Word ne contient pas de corps.")]
    [InlineData("zh-Hant", "請至少選擇一個本地檔案。", "Word 文件缺少正文。")]
    [Trait("Kind", "Unit")]
    public async Task NewConversionLanguagesKeepInvocationCultureAndRestoreTheCaller(string culture, string expectedError, string expectedWarning)
    {
        var previous = CultureInfo.CurrentUICulture;
        var invocation = new PluginInvocation("language", "to-docx", [], "/tmp") { Culture = culture };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ConversionPlugin().PrepareAsync(invocation, TestContext.Current.CancellationToken));
        Assert.Equal(expectedError, error.Message);
        using (MacExplorer.Services.Impl.ConversionLocalization.Use(culture))
            Assert.Equal(expectedWarning, MacExplorer.Services.Impl.ConversionLocalization.Get("Word 文档缺少正文。"));
        Assert.Equal(previous, CultureInfo.CurrentUICulture);
    }

    [Theory(Explicit = true)]
    [InlineData("en-US", "Unable to read the image file", "Unable to decode the WebP image")]
    [InlineData("fr-FR", "Impossible de lire le fichier image", "Impossible de décoder l’image WebP")]
    [InlineData("zh-Hant", "無法讀取圖像檔案", "無法解碼 WebP 圖像")]
    [Trait("Kind", "Integration")]
    public async Task NativeConversionErrorsUseInvocationLanguage(string culture, string readError, string decodeError)
    {
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-native-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "corrupt.webp");
            await File.WriteAllBytesAsync(source, [0, 1, 2]);
            var invocation = new PluginInvocation("en", "to-png", [new(source)], root) { Culture = culture };
            var error = await Assert.ThrowsAsync<IOException>(() => new ConversionPlugin().PrepareAsync(invocation, TestContext.Current.CancellationToken));
            Assert.Contains(error.Message, new[] { readError, decodeError });
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
