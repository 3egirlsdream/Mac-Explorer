using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

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
        Assert.Equal("Language", service["settings.language"]);
        Assert.Equal("Version 1.2", service.Get("settings.version", "1.2"));
        Assert.Equal(1, changes);

        service.SetLanguage(AppLanguage.ChineseSimplified);
        Assert.Equal("zh-CN", settings.Get("app_language"));
        Assert.Equal("应用语言", service["settings.language"]);
        Assert.Equal(2, changes);

        service.SetLanguage(AppLanguage.System);
        Assert.Equal("system", settings.Get("app_language"));
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
