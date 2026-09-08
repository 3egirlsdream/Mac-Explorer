using Avalonia;

namespace MacExplorer.Controls;

internal readonly record struct FastFileListGroup(string Name, int Count);

/// <summary>One section per group; file positions are calculated without per-file layout objects.</summary>
internal sealed class FastFileListLayout
{
    internal const double HeaderHeight = 34;
    internal const double CellWidth = 120;
    internal const double GridInset = 0;
    internal readonly record struct Section(int First, int Count, double Top, double ContentTop, double Bottom, string? Name, double[] RowOffsets);

    private Section[] _sections = [];
    public int Columns { get; private set; } = 1;
    public double ItemHeight { get; private set; } = FastFileList.RowHeight;
    public double Height { get; private set; }
    public double Width { get; private set; }
    public bool IsGrid { get; private set; }
    public int Count { get; private set; }

    public void Build(int count, IReadOnlyList<FastFileListGroup> groups, bool grid, double width, bool hasVirtualRows, Func<int, double>? gridHeight = null, double headerHeight = HeaderHeight)
    {
        Count = count;
        Width = width;
        IsGrid = grid;
        Columns = grid ? Math.Max(1, (int)Math.Floor(Math.Max(CellWidth, width - 16) / CellWidth)) : 1;
        ItemHeight = grid ? hasVirtualRows ? 136 : 116 : FastFileList.RowHeight;
        var sections = new List<Section>(Math.Max(1, groups.Count));
        var first = 0;
        double top = 0;
        if (groups.Count == 0) Add(null, count);
        else foreach (var group in groups) Add(group.Name, group.Count);
        _sections = sections.ToArray();
        Height = top;

        void Add(string? name, int size)
        {
            var contentTop = top + (name == null ? 0 : headerHeight);
            var rowCount = (size + Columns - 1) / Columns;
            var offsets = grid && gridHeight != null ? new double[rowCount + 1] : [];
            for (var row = 0; row + 1 < offsets.Length; row++)
            {
                double height = 0;
                for (var column = 0; column < Columns && row * Columns + column < size; column++)
                    height = Math.Max(height, gridHeight!(first + row * Columns + column));
                offsets[row + 1] = offsets[row] + height;
            }
            var bottom = contentTop + (offsets.Length > 0 ? offsets[^1] : rowCount * ItemHeight);
            sections.Add(new Section(first, size, top, contentTop, bottom, name, offsets));
            first += size;
            top = bottom;
        }
    }

    private int SectionAt(double y)
    {
        var low = 0;
        var high = _sections.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_sections[middle].Bottom <= y) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private int SectionForEntry(int index)
    {
        var low = 0;
        var high = _sections.Length - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_sections[middle].First + _sections[middle].Count <= index) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    public Rect Bounds(int index)
    {
        var section = _sections[SectionForEntry(index)];
        var local = index - section.First;
        return new Rect(IsGrid ? GridInset + local % Columns * CellWidth : 0,
            section.ContentTop + RowTop(section, local / Columns), IsGrid ? CellWidth : Width,
            RowTop(section, local / Columns + 1) - RowTop(section, local / Columns));
    }

    public int IndexAt(Point point)
    {
        if (point.Y < 0 || point.X < 0 || point.X >= Width) return -1;
        var sectionIndex = SectionAt(point.Y);
        if (sectionIndex >= _sections.Length) return -1;
        var section = _sections[sectionIndex];
        if (point.Y < section.ContentTop) return -1;
        var column = IsGrid ? (int)Math.Floor((point.X - GridInset) / CellWidth) : 0;
        if (column < 0 || column >= Columns) return -1;
        var index = section.First + RowAt(section, point.Y - section.ContentTop) * Columns + column;
        return index < section.First + section.Count ? index : -1;
    }

    public (int First, int End) VisibleRange(double top, double bottom)
    {
        var firstSection = SectionAt(top);
        if (firstSection >= _sections.Length || bottom <= 0) return (Count, Count);
        var lastSection = Math.Min(_sections.Length - 1, SectionAt(Math.Max(top, bottom - 0.0001)));
        var start = _sections[firstSection];
        var end = _sections[lastSection];
        var first = start.First + Math.Clamp(RowAt(start, top - start.ContentTop) * Columns, 0, start.Count);
        var last = end.First + Math.Clamp((RowAt(end, bottom - end.ContentTop - 0.0001) + 1) * Columns, 0, end.Count);
        return (first, Math.Max(first, last));
    }

    public IEnumerable<Section> VisibleHeaders(double top, double bottom)
    {
        for (var i = SectionAt(top); i < _sections.Length && _sections[i].Top < bottom; i++)
            if (_sections[i].Name != null && _sections[i].ContentTop > top)
                yield return _sections[i];
    }

    private double RowTop(Section section, int row)
        => section.RowOffsets.Length > 0 ? section.RowOffsets[row] : row * ItemHeight;

    private int RowAt(Section section, double y)
    {
        if (section.RowOffsets.Length == 0) return (int)Math.Floor(y / ItemHeight);
        if (y < 0) return -1;
        var index = Array.BinarySearch(section.RowOffsets, y);
        return index >= 0 ? index : ~index - 1;
    }

    public int MoveVertical(int index, int delta)
    {
        if (!IsGrid) return Math.Clamp(index + delta, 0, Math.Max(0, Count - 1));
        var sectionIndex = SectionForEntry(index);
        var section = _sections[sectionIndex];
        var column = (index - section.First) % Columns;
        var row = (index - section.First) / Columns + delta;
        while (row < 0 && sectionIndex > 0)
        {
            section = _sections[--sectionIndex];
            row += (section.Count + Columns - 1) / Columns;
        }
        while (row >= (section.Count + Columns - 1) / Columns && sectionIndex < _sections.Length - 1)
        {
            row -= (section.Count + Columns - 1) / Columns;
            section = _sections[++sectionIndex];
        }
        row = Math.Clamp(row, 0, Math.Max(0, (section.Count + Columns - 1) / Columns - 1));
        return Math.Clamp(section.First + row * Columns + column, section.First, Math.Max(section.First, section.First + section.Count - 1));
    }
}
