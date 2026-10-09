using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var language = LocalizationService.Current;
        if (language is null) return LocalizationCatalog.Get(Key, CultureInfo.GetCultureInfo("zh-CN"));
        // Bind a regular observable property: the host's reflection indexer binding
        // reads the initial value but does not refresh this service on notification.
        return new Binding
        {
            Source = language, Path = nameof(ILocalizationService.Culture), Mode = BindingMode.OneWay,
            Converter = new LocalizedStringConverter(language, Key)
        };
    }

    private sealed class LocalizedStringConverter(ILocalizationService language, string key) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => language[key];
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
    }
}
