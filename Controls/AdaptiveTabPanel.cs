using Avalonia;
using Avalonia.Controls;

namespace MacExplorer.Controls;

/// <summary>Keeps natural tab widths until the title bar runs out of space.</summary>
public sealed class AdaptiveTabPanel : Panel
{
    private double[] _preferredWidths = [];
    private double _preferredTotal;
    private int _dragIndex = -1;
    private double _dragX;
    internal int DropIndex { get; private set; } = -1;

    internal void PreviewDrag(int index, double x)
    {
        _dragIndex = index;
        _dragX = x;
        InvalidateArrange();
    }

    internal void EndDrag()
    {
        _dragIndex = DropIndex = -1;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _preferredWidths = new double[Children.Count];
        _preferredTotal = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (!child.IsVisible) continue;
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            // Match the original tab's content-based 180–280 DIP width.
            _preferredWidths[i] = Math.Clamp(child.DesiredSize.Width, 180, 280);
            _preferredTotal += _preferredWidths[i];
        }

        var width = Math.Min(_preferredTotal, availableSize.Width);
        var scale = _preferredTotal > 0 ? width / _preferredTotal : 1;
        var height = 0d;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(_preferredWidths[i] * scale, availableSize.Height));
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var scale = _preferredTotal > 0 ? Math.Min(1, finalSize.Width / _preferredTotal) : 1;
        var order = Enumerable.Range(0, Children.Count).ToList();
        var dragging = _dragIndex >= 0 && _dragIndex < Children.Count;
        if (dragging)
        {
            order.Remove(_dragIndex);
            var slotX = 0d;
            DropIndex = 0;
            foreach (var index in order)
            {
                var width = _preferredWidths[index] * scale;
                if (_dragX > slotX + width / 2) DropIndex++;
                slotX += width;
            }
            order.Insert(DropIndex, _dragIndex);
        }
        var x = 0d;
        foreach (var i in order)
        {
            var width = _preferredWidths[i] * scale;
            Children[i].Arrange(new Rect(dragging && i == _dragIndex ? _dragX : x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
