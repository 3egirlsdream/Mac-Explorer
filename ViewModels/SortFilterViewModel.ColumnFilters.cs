using MacExplorer.Models;

namespace MacExplorer.ViewModels;

public partial class SortFilterViewModel
{
    private FileListFilterState _columnFilters = FileListFilterState.Empty;

    public FileListFilterState ColumnFilters
    {
        get => _columnFilters;
        set => SetProperty(ref _columnFilters, value);
    }

    internal IReadOnlyList<FileSystemEntry> RawEntries => _rawEntries;

    private static readonly string[] NameRangeOrder = ["A–D", "E–H", "I–L", "M–P", "Q–T", "U–Z", "0–9", "中文", "其他"];

    public IReadOnlyList<FileColumnFilterOption> GetColumnFilterOptions(SortField field)
    {
        var today = DateTime.Today;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var availableTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _rawEntries)
        {
            if (!PassesVisibilityFilter(entry)) continue;
            var key = GetColumnFilterKey(entry, field, today);
            availableTypes.Add(key);
            // Ignore this column's own selection so unchecked alternatives remain available.
            if (PassesColumnFilters(entry, field, today))
                counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var selected = ColumnFilters.Selection(field);
        IEnumerable<string> keys = field switch
        {
            SortField.Name => NameRangeOrder,
            SortField.Modified => DateGroupOrder,
            SortField.Size => SizeGroupOrder.Reverse(),
            _ => availableTypes.Union(selected).OrderBy(key => key == "文件夹" ? 0 : key == "无后缀" ? 1 : 2)
                .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
        };
        return keys.Select(key => new FileColumnFilterOption(key, GetFilterLabel(key),
            counts.GetValueOrDefault(key), selected.Contains(key))).ToArray();
    }

    private bool PassesColumnFilters(FileSystemEntry entry, SortField? excluded = null, DateTime? today = null)
    {
        var filters = ColumnFilters;
        if (!string.IsNullOrWhiteSpace(filters.FileName)
            && !entry.Name.Contains(filters.FileName.Trim(), StringComparison.OrdinalIgnoreCase)
            && !entry.DisplayName.Contains(filters.FileName.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var (field, keys) in filters.Columns)
            if (field != excluded && !keys.Contains(GetColumnFilterKey(entry, field, today ?? _filterDate)))
                return false;
        return true;
    }

    private DateTime _filterDate = DateTime.Today;

    private static string GetColumnFilterKey(FileSystemEntry entry, SortField field, DateTime today) => field switch
    {
        SortField.Name => GetNameRange(entry.Name),
        SortField.Modified => GetDateGroup(entry.LastModified, today),
        SortField.Size => entry.IsDirectory ? "文件夹" : GetSizeGroup(entry.Size),
        _ => entry.IsDirectory ? "文件夹" : string.IsNullOrEmpty(entry.Extension) ? "无后缀" : entry.Extension.ToLowerInvariant()
    };

    private static string GetNameRange(string name)
    {
        var first = name.Length == 0 ? '\0' : char.ToUpperInvariant(name[0]);
        return first switch
        {
            >= 'A' and <= 'D' => "A–D",
            >= 'E' and <= 'H' => "E–H",
            >= 'I' and <= 'L' => "I–L",
            >= 'M' and <= 'P' => "M–P",
            >= 'Q' and <= 'T' => "Q–T",
            >= 'U' and <= 'Z' => "U–Z",
            >= '0' and <= '9' => "0–9",
            >= '\u3400' and <= '\u9fff' => "中文",
            _ => "其他"
        };
    }

    private static string GetFilterLabel(string key) => key switch
    {
        "小于 1 KB" => "小于 1 KB",
        "小于 1 MB" => "1 KB – 1 MB",
        "1-100 MB" => "1 MB – 100 MB",
        "100 MB-1 GB" => "100 MB – 1 GB",
        "大于 1 GB" => "1 GB 及以上",
        _ => key
    };
}
