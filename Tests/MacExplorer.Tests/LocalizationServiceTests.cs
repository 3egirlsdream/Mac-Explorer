using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Localization")]
public sealed class LocalizationServiceTests
{
    [Fact]
    public void LanguageSelectionPersistsAndChangesLocalizedValuesImmediately()
    {
        var settings = new MemorySettings();
        using var service = new LocalizationService(settings);
        var changes = 0;
        service.LanguageChanged += () => changes++;

        service.SetLanguage(AppLanguage.English);
        Assert.Equal("en", settings.Get("app_language"));
        Assert.Equal("Display language", service["settings.language"]);
        Assert.Equal("Version 1.2", service.Get("settings.version", "1.2"));
        Assert.Equal(1, changes);

        service.SetLanguage(AppLanguage.ChineseSimplified);
        Assert.Equal("zh-CN", settings.Get("app_language"));
        Assert.Equal("显示语言", service["settings.language"]);
        Assert.Equal(2, changes);

        service.SetLanguage(AppLanguage.System);
        Assert.Equal("system", settings.Get("app_language"));
    }

    [Fact]
    public void EveryDiscoveredLanguageCanBeSelectedAndRestoredWithoutAnEnum()
    {
        var settings = new MemorySettings();
        using var service = new LocalizationService(settings, () => AppLanguage.ChineseSimplified);
        foreach (var option in service.AvailableLanguages)
        {
            service.SetLanguage(option.Id);
            var pack = LocalizationCatalog.GetPack(option.Id);
            Assert.Equal(option.Id, service.Language);
            Assert.Same(pack.Culture, service.Culture);
            Assert.Equal(option.Id, LocalizationService.MapSystemLanguage(pack.CultureName));
            foreach (var entry in pack.Strings)
                Assert.Equal(entry.Value, service[entry.Key]);
            using var reopened = new LocalizationService(settings);
            Assert.Equal(option.Id, reopened.Language);
            Assert.Equal(service["settings.language"], reopened["settings.language"]);
        }
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("EN", "en")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-cn", "zh-CN")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-Hant-HK", "zh-Hant")]
    [InlineData("fr-CA", "fr")]
    [InlineData("removed", "system")]
    public void ExistingSettingsAndRemovedLanguagesAreHandled(string saved, string expected)
    {
        var settings = new MemorySettings();
        settings.Set(LocalizationService.SettingKey, saved);
        using var service = new LocalizationService(settings, () => AppLanguage.ChineseSimplified);
        Assert.Equal(expected, service.Language);
        Assert.Equal(saved, settings.Get(LocalizationService.SettingKey));
    }

    [Theory]
    [InlineData("zh-Hant", "顯示語言", "儲存", "zh-Hant")]
    [InlineData("fr", "Langue d’affichage", "Enregistrer", "fr-FR")]
    public void NewLanguagesHaveCompleteCatalogsAndPersistedSelections(string code, string displayLanguage, string save, string culture)
    {
        var settings = new MemorySettings();
        using var service = new LocalizationService(settings, () => AppLanguage.ChineseSimplified);
        service.SetLanguage(code);
        Assert.Equal(displayLanguage, service["settings.language"]);
        Assert.Equal(save, LocalizationText.Get("保存"));
        Assert.Equal(culture, service.Culture.Name);
        Assert.Equal(code, settings.Get(LocalizationService.SettingKey));
        Assert.Equal(LocalizationCatalog.GetPack(AppLanguage.ChineseSimplified).Strings.Keys.Order(),
            LocalizationCatalog.GetPack(code).Strings.Keys.Order());
        using var reopened = new LocalizationService(settings);
        Assert.Equal(code, reopened.Language);
        Assert.Equal(displayLanguage, reopened["settings.language"]);
    }

    [Fact]
    public void ListingLanguagesDoesNotLoadTheirTranslationsAndFallbackIsPerKey()
    {
        var loads = 0;
        var pack = new LocalizationCatalog.LanguagePack("fr", "Français", "fr-FR", "en", () =>
        {
            loads++;
            return new Dictionary<string, string> { ["settings.language"] = "Langue" };
        });
        Assert.Equal("Français", pack.Option.NativeName);
        Assert.Equal("fr-FR", pack.CultureName);
        Assert.Equal(0, loads);
        Assert.Equal("Langue", LocalizationCatalog.Get("settings.language", pack));
        Assert.Equal("Save", LocalizationCatalog.Get("settings.save", pack));
        Assert.Equal("unknown.key", LocalizationCatalog.Get("unknown.key", pack));
        Assert.Equal(1, loads);
    }

    [Fact]
    public void WarmLookupsAndLanguageListAccessDoNotAllocate()
    {
        using var service = new LocalizationService(new MemorySettings(), () => AppLanguage.English);
        for (var i = 0; i < 10_000; i++)
        {
            _ = service["settings.language"];
            _ = LocalizationText.Get("保存");
            _ = service.AvailableLanguages;
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
        {
            _ = service["settings.language"];
            _ = LocalizationText.Get("保存");
            _ = service.AvailableLanguages;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T defaultValue) => defaultValue;
        public void Set(string key, string value)
        {
            _values[key] = value;
            SettingChanged?.Invoke(key);
        }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }

}
