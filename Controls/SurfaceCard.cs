using Avalonia;
using Avalonia.Controls.Primitives;

namespace MacExplorer.Controls;

public sealed class SurfaceCard : TemplatedControl
{
    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<SurfaceCard, object?>(nameof(Content));

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }
}
