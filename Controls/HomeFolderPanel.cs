using Avalonia;
using Avalonia.Controls;

namespace MacExplorer.Controls;

/// <summary>Packs differently sized cards into the first available space, including holes above later rows.</summary>
public sealed class HomeFolderPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        return Pack(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Pack(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Pack(double width, bool arrange)
    {
        var occupied = new List<Rect>();
        foreach (var child in Children.Where(child => child.IsVisible))
        {
            var size = child.DesiredSize;
            var ys = occupied.Select(rect => rect.Bottom).Append(0).Distinct().Order();
            var xs = occupied.Select(rect => rect.Right).Append(0).Distinct().Order().ToArray();
            Rect? slot = null;
            foreach (var y in ys)
            {
                foreach (var x in xs)
                {
                    var candidate = new Rect(x, y, size.Width, size.Height);
                    if (x > 0 && candidate.Right > width + .01) continue;
                    if (occupied.Any(rect => rect.Intersects(candidate))) continue;
                    slot = candidate;
                    break;
                }
                if (slot.HasValue) break;
            }
            var bounds = slot ?? new Rect(0, occupied.Count == 0 ? 0 : occupied.Max(rect => rect.Bottom), size.Width, size.Height);
            occupied.Add(bounds);
            if (arrange) child.Arrange(bounds);
        }
        return occupied.Count == 0 ? default : new Size(occupied.Max(rect => rect.Right), occupied.Max(rect => rect.Bottom));
    }
}
