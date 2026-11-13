using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

/// <summary>Compact group labels and content-sized tabs, with a minimum usable tab width.</summary>
public sealed class AdaptiveTabPanel : Panel
{
    public static readonly StyledProperty<double> ViewportWidthProperty =
        AvaloniaProperty.Register<AdaptiveTabPanel, double>(nameof(ViewportWidth));
    public double ViewportWidth { get => GetValue(ViewportWidthProperty); set => SetValue(ViewportWidthProperty, value); }
    private double[] _widths = [];
    private int _dragIndex = -1;
    private double _dragX;
    private double _pointerX;
    private bool _forceUngroup;
    internal int ArrangeVersion { get; private set; }
    internal int DropIndex { get; private set; } = -1;
    internal object? DropAnchor { get; private set; }
    internal bool DropAfter { get; private set; }
    internal bool DropUngroup { get; private set; }

    static AdaptiveTabPanel() => AffectsMeasure<AdaptiveTabPanel>(ViewportWidthProperty);

    internal void PreviewDrag(int index, double x, double pointerX, bool forceUngroup)
    {
        _dragIndex = index;
        _dragX = x;
        _pointerX = pointerX;
        _forceUngroup = forceUngroup;
        InvalidateArrange();
    }

    internal void EndDrag()
    {
        _dragIndex = DropIndex = -1;
        DropAnchor = null;
        DropUngroup = false;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _widths = new double[Children.Count];
        var labelsWidth = 0d;
        var tabsWidth = 0d;
        var tabCount = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var group = GroupOf(child.DataContext);
            var previousGroup = i > 0 ? GroupOf(Children[i - 1].DataContext) : null;
            child.Classes.Set("group-separated", i > 0 && group != previousGroup);
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (child.DataContext is TabGroupViewModel)
                labelsWidth += _widths[i] = Math.Clamp(child.DesiredSize.Width, 40 + child.Margin.Left, 148 + child.Margin.Left);
            else
            {
                tabsWidth += _widths[i] = Math.Clamp(child.DesiredSize.Width, 180, 280);
                tabCount++;
            }
        }
        var limit = ViewportWidth > 0 ? Math.Max(0, ViewportWidth - 40) : availableSize.Width;
        var width = Math.Max(labelsWidth + tabCount * 112, Math.Min(labelsWidth + tabsWidth, limit));
        var scale = tabsWidth > 0 ? Math.Min(1, Math.Max(0, width - labelsWidth) / tabsWidth) : 1;
        var height = 0d;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (child.DataContext is not TabGroupViewModel)
                _widths[i] = Math.Max(112, _widths[i] * scale);
            child.Measure(new Size(_widths[i], availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(_widths.Sum(), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var order = Enumerable.Range(0, Children.Count).ToList();
        var dragged = new List<int>();
        TabGroupViewModel? previewGroup = null;
        if (_dragIndex >= 0 && _dragIndex < Children.Count)
        {
            var group = Children[_dragIndex].DataContext as TabGroupViewModel;
            dragged = order.Where(i => i == _dragIndex || group != null
                && Children[i].DataContext is ExplorerTabViewModel tab && tab.Group == group).ToList();
            order.RemoveAll(dragged.Contains);
            var blocks = new List<List<int>>();
            foreach (var i in order)
            {
                if (group != null && Children[i].DataContext is ExplorerTabViewModel { Group: { } owner }
                    && blocks.Count > 0 && Children[blocks[^1][0]].DataContext == owner)
                    blocks[^1].Add(i);
                else blocks.Add([i]);
            }
            var x = 0d;
            var insertion = 0;
            DropAnchor = blocks.Count > 0 ? Children[blocks[0][0]].DataContext : null;
            DropAfter = false;
            foreach (var block in blocks)
            {
                var width = block.Sum(i => _widths[i]);
                if (_dragX > x + width / 2)
                {
                    insertion += block.Count;
                    DropAnchor = Children[block[0]].DataContext;
                    DropAfter = true;
                }
                x += width;
            }
            DropUngroup = false;
            if (Children[_dragIndex].DataContext is ExplorerTabViewModel moving)
            {
                var ranges = GroupRanges();
                previewGroup = _forceUngroup ? null : GroupAtPointer(moving.Group, ranges);
                DropUngroup = previewGroup == null;
                var anchorGroup = GroupOf(DropAnchor);
                if (previewGroup != null && previewGroup != anchorGroup)
                {
                    DropAnchor = previewGroup;
                    var range = ranges[previewGroup];
                    DropAfter = _pointerX > (range.Start + range.End) / 2;
                }
                else if (DropUngroup && anchorGroup != null)
                {
                    DropAnchor = anchorGroup;
                    var range = ranges[anchorGroup];
                    DropAfter = _pointerX >= (range.Start + range.End) / 2;
                }
                insertion = InsertionIndex(order);
            }
            DropIndex = insertion;
            order.InsertRange(insertion, dragged);
        }
        var dragX = _dragX;
        if (previewGroup != null)
        {
            var header = order.FindIndex(i => Children[i].DataContext == previewGroup);
            if (header >= 0) dragX = Math.Max(dragX, order.Take(header + 1).Sum(i => _widths[i]));
        }
        var position = 0d;
        var dragOffset = 0d;
        foreach (var i in order)
        {
            var width = _widths.ElementAtOrDefault(i);
            var moving = dragged.Contains(i);
            Children[i].Arrange(new Rect(moving ? dragX + dragOffset : position, 0, width, finalSize.Height));
            if (moving) dragOffset += width;
            position += width;
            var owner = i == _dragIndex && Children[i].DataContext is ExplorerTabViewModel
                ? previewGroup : GroupOf(Children[i].DataContext);
            Children[i].GetVisualDescendants().OfType<TabGroupLine>().FirstOrDefault()
                ?.Update(owner);
        }
        ArrangeVersion++;
        return finalSize;
    }

    private static TabGroupViewModel? GroupOf(object? item)
        => item as TabGroupViewModel ?? (item as ExplorerTabViewModel)?.Group;

    private Dictionary<TabGroupViewModel, (double Start, double End)> GroupRanges()
    {
        var ranges = new Dictionary<TabGroupViewModel, (double Start, double End)>();
        var position = 0d;
        for (var i = 0; i < Children.Count; i++)
        {
            if (GroupOf(Children[i].DataContext) is { } group)
                ranges[group] = (ranges.TryGetValue(group, out var range) ? range.Start : position,
                    position + _widths[i]);
            position += _widths[i];
        }
        return ranges;
    }

    private TabGroupViewModel? GroupAtPointer(TabGroupViewModel? source,
        IReadOnlyDictionary<TabGroupViewModel, (double Start, double End)> ranges)
    {
        if (source != null && ranges.TryGetValue(source, out var original)
            && _pointerX >= original.Start && _pointerX <= original.End) return source;
        foreach (var (group, range) in ranges)
            if (_pointerX >= range.Start + 8 && _pointerX <= range.End - 8) return group;
        return null;
    }

    private int InsertionIndex(List<int> order)
    {
        if (DropAnchor == null) return DropAfter ? order.Count : 0;
        var group = GroupOf(DropAnchor);
        if (group != null && (DropUngroup || DropAnchor is TabGroupViewModel))
        {
            if (DropAfter) return order.FindLastIndex(i => GroupOf(Children[i].DataContext) == group) + 1;
            var header = order.FindIndex(i => Children[i].DataContext == group);
            return Math.Max(0, header + (DropUngroup ? 0 : 1));
        }
        var anchor = order.FindIndex(i => Children[i].DataContext == DropAnchor);
        return Math.Clamp(anchor + (DropAfter ? 1 : 0), 0, order.Count);
    }
}
