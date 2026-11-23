using Avalonia;
using Avalonia.Controls;

namespace MacExplorer.Controls;

/// <summary>
/// Arranges the vertical tab list ScrollViewer, the sticky New Tab footer right beneath it,
/// and a blank drag area filling any remaining vertical space below.
/// When tabs are few, the footer sits immediately below the last tab; when tabs overflow,
/// the list scrolls while the footer remains pinned at the bottom.
/// </summary>
public sealed class VerticalTabsLayoutPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : double.PositiveInfinity;
        if (Children.Count == 0)
            return default;

        var scroller = Children[0];
        var footer = Children.Count > 1 ? Children[1] : null;
        var blankFill = Children.Count > 2 ? Children[2] : null;

        var footerHeight = 0d;
        if (footer is { IsVisible: true })
        {
            footer.Measure(new Size(width, height));
            footerHeight = footer.DesiredSize.Height;
        }

        var scrollerAvailable = double.IsFinite(height) ? Math.Max(0, height - footerHeight) : double.PositiveInfinity;
        scroller.Measure(new Size(width, scrollerAvailable));
        var scrollerHeight = Math.Min(scroller.DesiredSize.Height, scrollerAvailable);

        var remaining = double.IsFinite(height) ? Math.Max(0, height - scrollerHeight - footerHeight) : 0;
        if (blankFill != null)
            blankFill.Measure(new Size(width, remaining));

        var totalHeight = double.IsFinite(height) ? height : scrollerHeight + footerHeight;
        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
            return finalSize;

        var scroller = Children[0];
        var footer = Children.Count > 1 ? Children[1] : null;
        var blankFill = Children.Count > 2 ? Children[2] : null;

        var footerHeight = footer is { IsVisible: true } ? Math.Min(footer.DesiredSize.Height, finalSize.Height) : 0;
        var maxScrollerHeight = Math.Max(0, finalSize.Height - footerHeight);
        var scrollerHeight = Math.Min(scroller.DesiredSize.Height, maxScrollerHeight);

        scroller.Arrange(new Rect(0, 0, finalSize.Width, scrollerHeight));
        footer?.Arrange(new Rect(0, scrollerHeight, finalSize.Width, footerHeight));

        var blankTop = scrollerHeight + footerHeight;
        var blankHeight = Math.Max(0, finalSize.Height - blankTop);
        blankFill?.Arrange(new Rect(0, blankTop, finalSize.Width, blankHeight));

        return finalSize;
    }
}
