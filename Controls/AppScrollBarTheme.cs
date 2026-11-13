using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MacExplorer.Controls;

/// <summary>Keeps preview libraries from substituting their own scrollbar templates.</summary>
public sealed class AppScrollBarTheme : ControlTheme
{
    private static readonly FluentTheme Source = new();

    public AppScrollBarTheme()
    {
        TargetType = typeof(ScrollBar);
        Source.TryGetResource(typeof(ScrollBar), ThemeVariant.Default, out var theme);
        BasedOn = (ControlTheme)theme!;
    }
}
