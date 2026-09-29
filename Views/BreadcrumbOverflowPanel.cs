using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MacExplorer.Views;

/// <summary>Shows the trailing breadcrumb segments that fit, reserving space for an overflow button.</summary>
public sealed class BreadcrumbOverflowPanel : Panel
{
    public const double OverflowWidth = 28;
    private TextBlock? _lastLabel;
    private double _naturalLastLabelWidth;

    public int HiddenCount { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));

        var lastLabel = Children.LastOrDefault()?.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(label => label.Classes.Contains("breadcrumb-segment-label"));
        if (!ReferenceEquals(lastLabel, _lastLabel))
        {
            _lastLabel = lastLabel;
            _naturalLastLabelWidth = lastLabel?.DesiredSize.Width ?? 0;
        }

        var width = Children.Sum(child => child.DesiredSize.Width);
        if (lastLabel != null && !double.IsInfinity(availableSize.Width))
        {
            var last = Children[^1];
            var chrome = last.DesiredSize.Width - lastLabel.DesiredSize.Width;
            var naturalWidth = width + _naturalLastLabelWidth - lastLabel.DesiredSize.Width;
            var maxWidth = naturalWidth > availableSize.Width
                ? Math.Max(24, availableSize.Width - (Children.Count > 1 ? OverflowWidth : 0) - chrome)
                : double.PositiveInfinity;
            if (lastLabel.MaxWidth != maxWidth)
            {
                lastLabel.MaxWidth = maxWidth;
                last.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                width = Children.Sum(child => child.DesiredSize.Width);
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width),
            Children.Count == 0 ? 0 : Children.Max(child => child.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = Children.Select(child => child.DesiredSize.Width).ToArray();
        var hidden = 0;
        var naturalWidth = widths.Sum() + (_lastLabel == null ? 0 : _naturalLastLabelWidth - _lastLabel.DesiredSize.Width);
        if (naturalWidth > finalSize.Width && widths.Length > 1)
        {
            var remaining = Math.Max(0, finalSize.Width - OverflowWidth);
            hidden = widths.Length;
            for (var i = widths.Length - 1; i >= 0; i--)
            {
                if (i != widths.Length - 1 && widths[i] > remaining)
                    break;
                remaining -= widths[i];
                hidden = i;
            }
            hidden = Math.Max(1, hidden);
        }

        var x = hidden > 0 ? OverflowWidth : 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var visible = i >= hidden;
            child.IsHitTestVisible = visible;
            child.Arrange(visible
                ? new Rect(x, 0, Math.Max(0, Math.Min(widths[i], finalSize.Width - x)), finalSize.Height)
                : new Rect(-widths[i], 0, widths[i], finalSize.Height));
            if (visible)
                x += widths[i];
        }

        HiddenCount = hidden;
        if (_lastLabel == null && Children.LastOrDefault()?.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(label => label.Classes.Contains("breadcrumb-segment-label")) is { } label)
        {
            _lastLabel = label;
            _naturalLastLabelWidth = label.Bounds.Width;
            InvalidateMeasure();
        }
        return finalSize;
    }
}
