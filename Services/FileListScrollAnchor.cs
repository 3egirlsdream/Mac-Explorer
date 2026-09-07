using MacExplorer.Models;

namespace MacExplorer.Services;

/// <summary>Fixed-height ordinary details rows only; grouped/grid layouts use their own geometry.</summary>
public sealed record FileListScrollAnchor(
    string ItemPath, double ItemViewportY, double AbsoluteOffsetFallback, double ContentOriginY = 0)
{
    public const double DetailsRowHeight = 30;

    public double Resolve(IReadOnlyList<FileSystemEntry> entries, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!double.IsFinite(viewportHeight) || viewportHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        var offset = AbsoluteOffsetFallback;
        for (var index = 0; index < entries.Count; index++)
        {
            if (!string.Equals(entries[index].FullPath, ItemPath, StringComparison.Ordinal)) continue;
            offset = index * DetailsRowHeight + ContentOriginY - ItemViewportY;
            break;
        }
        var max = Math.Max(0, entries.Count * DetailsRowHeight + ContentOriginY - viewportHeight);
        return Math.Clamp(double.IsFinite(offset) ? offset : 0, 0, max);
    }
}
