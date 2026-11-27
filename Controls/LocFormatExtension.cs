using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

public sealed class LocFormatExtension : MarkupExtension
{
    public string Text { get; set; } = "";
    public BindingBase Argument { get; set; } = null!;
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Converter = new FormatConverter(Text) };
        binding.Bindings.Add(LocExtension.ObserveCulture(LocalizationService.Current));
        binding.Bindings.Add(Argument);
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
