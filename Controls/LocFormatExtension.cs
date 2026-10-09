using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

public sealed class LocFormatExtension : MarkupExtension
{
    public string Text { get; set; } = "";
    public string Path { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Converter = new FormatConverter(Text) };
        binding.Bindings.Add(new Binding { Source = LocalizationService.Current, Path = "Culture" });
        binding.Bindings.Add(new Binding(Path));
        return binding;
    }
    private sealed class FormatConverter(string text) : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count < 2 || values[1] == Avalonia.AvaloniaProperty.UnsetValue)
                return BindingOperations.DoNothing;
            return LocalizationText.Get(text, values[1]);
        }
    }
}
