using System.ComponentModel;
using System.Globalization;

namespace MacExplorer.Services;

public static class AppLanguage
{
    public const string System = "system";
    public const string ChineseSimplified = "zh-CN";
    public const string English = "en";
}

public sealed record LanguageOption(string Id, string NativeName);

public interface ILocalizationService : INotifyPropertyChanged
{
    event Action? LanguageChanged;
    string Language { get; }
    IReadOnlyList<LanguageOption> AvailableLanguages { get; }
    CultureInfo Culture { get; }
    string this[string key] { get; }
    string Get(string key, params object?[] arguments);
    void SetLanguage(string language);
}
