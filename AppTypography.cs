using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MacExplorer;

/// <summary>
/// Binds controls created in code to the same live typography resources used by XAML.
/// </summary>
internal static class AppTypography
{
    public const string IconGlyph = "FontSizeIconGlyph";
    public const string Meta = "FontSizeMeta";
    public const string Caption = "FontSizeCaption";
    public const string Label = "FontSizeLabel";
    public const string Body = "FontSizeBody";
    public const string BodyLarge = "FontSizeBodyLarge";
    public const string Heading = "FontSizeHeading";
    public const string Title = "FontSizeTitle";
    public const string Display = "FontSizeDisplay";

    private static int _globalTextOptionsRegistered;

    /// <summary>
    /// Grayscale antialiasing with macOS CoreText font smoothing enabled so 1x displays stay crisp and solid.
    /// </summary>
    public static TextOptions DefaultTextOptions { get; } = new()
    {
        TextRenderingMode = TextRenderingMode.Antialias,
        TextHintingMode = TextHintingMode.Strong,
    };

    public static void RegisterGlobalTextOptions()
    {
        if (Interlocked.Exchange(ref _globalTextOptionsRegistered, 1) != 0) return;
        Control.LoadedEvent.AddClassHandler<TopLevel>((topLevel, _) => ApplyTextOptions(topLevel));
    }

    public static void ApplyTextOptions(Visual visual)
    {
        var current = TextOptions.GetTextOptions(visual);
        if (current.TextRenderingMode == TextRenderingMode.Unspecified
            && current.TextHintingMode == TextHintingMode.Unspecified)
            TextOptions.SetTextOptions(visual, DefaultTextOptions);
    }

    public static TextBlock BindFontSize(TextBlock control, string resourceKey)
    {
        control.Bind(TextBlock.FontSizeProperty, control.GetResourceObservable(resourceKey));
        return control;
    }

    public static T BindFontSize<T>(T control, string resourceKey) where T : TemplatedControl
    {
        control.Bind(TemplatedControl.FontSizeProperty, control.GetResourceObservable(resourceKey));
        return control;
    }
}
