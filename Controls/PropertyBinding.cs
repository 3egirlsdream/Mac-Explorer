using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;

namespace MacExplorer.Controls;

internal static class PropertyBinding
{
    internal static CompiledBindingPath Path<TSource, TValue>(
        string name, Func<TSource, TValue> get, Action<TSource, TValue>? set = null)
        where TSource : INotifyPropertyChanged
    {
        var property = new ClrPropertyInfo(name, source => get((TSource)source),
            set is null ? null : (source, value) => set((TSource)source, (TValue)value!), typeof(TValue));
        return new CompiledBindingPathBuilder()
            .Property(property, PropertyInfoAccessorFactory.CreateInpcPropertyAccessor)
            .Build();
    }
}
