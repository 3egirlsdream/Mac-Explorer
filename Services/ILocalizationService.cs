using System.ComponentModel;
using System.Globalization;

namespace MacExplorer.Services;

public enum AppLanguage
{
    System,
    ChineseSimplified,
    English
}

public interface ILocalizationService : INotifyPropertyChanged
{
    event Action? LanguageChanged;
    AppLanguage Language { get; }
    CultureInfo Culture { get; }
    string this[string key] { get; }
    string Get(string key, params object?[] arguments);
    void SetLanguage(AppLanguage language);
}
