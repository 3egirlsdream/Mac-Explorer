using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MacExplorer.Controls;

/// <summary>
/// Renders a 24x24 Fluent UI System Icon geometry preserving its native 24x24 optical artboard
/// (avoiding tight-box stretching and antialiased edge clipping common with PathIcon).
/// </summary>
public sealed class FluentIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<FluentIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<FluentIcon>();

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    static FluentIcon()
    {
        ClipToBoundsProperty.OverrideDefaultValue<FluentIcon>(false);
        AffectsRender<FluentIcon>(DataProperty, ForegroundProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Data is not { } geometry || Foreground is not { } brush || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        var offsetX = (Bounds.Width - 24.0 * scale) / 2.0;
        var offsetY = (Bounds.Height - 24.0 * scale) / 2.0;

        using (context.PushTransform(Matrix.CreateTranslation(offsetX, offsetY)))
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(brush, null, geometry);
        }
    }
}
