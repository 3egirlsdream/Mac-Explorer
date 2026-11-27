using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

public sealed class LocExtension : MarkupExtension
{
    private static readonly CompiledBindingPath CulturePath = PropertyBinding.Path(
        nameof(ILocalizationService.Culture), static (ILocalizationService language) => language.Culture);

    public LocExtension() { }
    public LocExtension(string key) => Key = key;
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var language = LocalizationService.Current;
        if (language is null) return LocalizationCatalog.Get(Key, CultureInfo.GetCultureInfo("zh-CN"));
        var key = Key;
        return ObserveCulture(language, new FuncValueConverter<CultureInfo, string>(_ => language[key]));
    }

    internal static CompiledBinding ObserveCulture(ILocalizationService? language, IValueConverter? converter = null) =>
        new(CulturePath) { Source = language, Mode = BindingMode.OneWay, Converter = converter };
}
