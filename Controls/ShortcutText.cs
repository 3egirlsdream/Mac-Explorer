using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MacExplorer.Controls;

/// <summary>Key labels share ink height, rather than the font's unequal symbol metrics.</summary>
public sealed class ShortcutText : TextBlock
{
    private List<(Geometry Shape, Matrix Transform)>? _shapes;
    private Size _size;
    protected override Type StyleKeyOverride => typeof(TextBlock);
    private const string KeySymbols = "⌘⇧⌥⌃⌫⌦↩↑↓←→";
    internal IReadOnlyList<Rect> SymbolBounds { get; private set; } = [];

    private void EnsureShapes()
    {
        if (_shapes != null) return;
        _shapes = [];
        var symbolBounds = new List<Rect>();
        var height = FontSize * .75;
        var x = 0d;
        var value = Text ?? "";
        for (var i = 0; i < value.Length;)
        {
            var symbol = KeySymbols.Contains(value[i]);
            var start = i++;
            if (!symbol)
                while (i < value.Length && !KeySymbols.Contains(value[i])) i++;
            var text = new FormattedText(value[start..i], CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(FontFamily), FontSize, Brushes.Black);
            var geometry = text.BuildGeometry(default);
            var bounds = geometry?.Bounds ?? default;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                x += text.WidthIncludingTrailingWhitespace;
                continue;
            }
            // Preserve punctuation proportions; scale key symbols to the capital-letter height.
            var scale = symbol ? height / bounds.Height : 1;
            var width = symbol ? Math.Max(height, bounds.Width * scale) + 2 : text.WidthIncludingTrailingWhitespace;
            var left = x + (width - bounds.Width * scale) / 2;
            var top = (FontSize - bounds.Height * scale) / 2;
            if (symbol) symbolBounds.Add(new Rect(left, top, bounds.Width * scale, bounds.Height * scale));
            _shapes.Add((geometry!, new Matrix(scale, 0, 0, scale,
                left - bounds.X * scale, top - bounds.Y * scale)));
            x += width;
        }
        _size = new Size(x, FontSize);
        SymbolBounds = symbolBounds;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureShapes();
        return _size.Inflate(Padding);
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        EnsureShapes();
        using (context.PushTransform(Matrix.CreateTranslation(Padding.Left, Padding.Top)))
            foreach (var (shape, transform) in _shapes!)
                using (context.PushTransform(transform))
                    context.DrawGeometry(Foreground, null, shape);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == TextProperty || change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
            _shapes = null;
        base.OnPropertyChanged(change);
    }
}
