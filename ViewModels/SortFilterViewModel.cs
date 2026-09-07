using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class SortFilterViewModel : ObservableObject
{
    private readonly ISettingsService? _settingsService;
    private readonly Microsoft.Extensions.Logging.ILogger<SortFilterViewModel>? _logger;

    private IReadOnlyList<FileSystemEntry> _rawEntries = [];

    private static readonly HashSet<string> SystemFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", "desktop.ini", ".Spotlight-V100", ".Trashes", ".fseventsd", ".localized"
    };

    [ObservableProperty]
    private ViewMode _viewMode = ViewMode.List;

    [ObservableProperty]
    private SortField _sortField = SortField.Name;

    [ObservableProperty]
    private bool _sortAscending = true;

    [ObservableProperty]
    private GroupField _groupField = GroupField.None;

    [ObservableProperty]
    private ObservableCollection<FileGroup> _groups = [];

    [ObservableProperty]
    private bool _hideSystemFiles = true;

    [ObservableProperty]
    private bool _hideDotFiles = true;

    [ObservableProperty]
    private bool _hideDotFolders = true;

    public SortFilterViewModel(
        ISettingsService? settingsService = null,
        Microsoft.Extensions.Logging.ILogger<SortFilterViewModel>? logger = null)
    {
        _settingsService = settingsService;
        _logger = logger;

        // Restore fields directly: property setters persist user changes and
        // would synchronously write these same settings for every new tab.
        if (_settingsService != null)
        {
            _viewMode = _settingsService.Get<ViewMode>("ViewMode", ViewMode.List);
            _sortField = _settingsService.Get<SortField>("SortField", SortField.Name);
            _sortAscending = _settingsService.Get<bool>("SortAscending", true);
            _groupField = _settingsService.Get<GroupField>("GroupField", GroupField.None);
            _hideSystemFiles = _settingsService.Get<bool>("HideSystemFiles", true);
            _hideDotFiles = _settingsService.Get<bool>("HideDotFiles", true);
            _hideDotFolders = _settingsService.Get<bool>("HideDotFolders", true);
        }
    }

    public void SetSort(SortField field, bool? ascending = null)
    {
        if (SortField == field && ascending == null)
            SortAscending = !SortAscending;
        else
        {
            SortField = field;
            SortAscending = ascending ?? true;
        }
    }

    partial void OnViewModeChanged(ViewMode value) => _settingsService?.Set("ViewMode", value);
    partial void OnSortFieldChanged(SortField value) { _settingsService?.Set("SortField", value); }
    partial void OnSortAscendingChanged(bool value) { _settingsService?.Set("SortAscending", value); }
    partial void OnGroupFieldChanged(GroupField value) { _settingsService?.Set("GroupField", value); }
    partial void OnHideSystemFilesChanged(bool value) => _settingsService?.Set("HideSystemFiles", value);
    partial void OnHideDotFilesChanged(bool value) => _settingsService?.Set("HideDotFiles", value);
    partial void OnHideDotFoldersChanged(bool value) => _settingsService?.Set("HideDotFolders", value);

    public void ApplySortAndGroup(Action<ObservableCollection<FileSystemEntry>> setEntries)
    {
        if (_rawEntries.Count == 0) { setEntries([]); Groups = []; return; }
        var list = new List<FileSystemEntry>(_rawEntries.Count);
        foreach (var entry in _rawEntries)
        {
            if (PassesFilter(entry))
                list.Add(entry);
        }
        var sorted = SortEntries(list).ToList();
        setEntries(new ObservableCollection<FileSystemEntry>(sorted));
        if (GroupField == GroupField.None)
        {
            if (Groups.Count > 0)
                Groups = [];
        }
        else
        {
            Groups = new ObservableCollection<FileGroup>(BuildGroups(sorted));
        }
    }

    private bool PassesFilter(FileSystemEntry entry)
    {
        if (entry.Name.EndsWith(".fkfinder-tmp")) return false;
        if (HideSystemFiles && SystemFileNames.Contains(entry.Name)) return false;
        if (HideDotFiles && !entry.IsDirectory && entry.Name.StartsWith('.')) return false;
        if (HideDotFolders && entry.IsDirectory && entry.Name.StartsWith('.')) return false;
        return true;
    }

    public void SetRawEntries(IReadOnlyList<FileSystemEntry> entries)
    {
        _rawEntries = entries;
    }

    internal IComparer<FileSystemEntry> BuildEntriesComparer() => new EntriesComparer(SortField, SortAscending);

    // Inserts a streamed batch into the already sorted collection so batched directory
    // loads avoid a full re-sort and collection replacement per batch. Upper-bound
    // insertion keeps ties in arrival order, matching the stable full sort of the same
    // stream. The caller keeps _rawEntries in sync via SetRawEntries.
    public void InsertBatch(IReadOnlyList<FileSystemEntry> batch, ObservableCollection<FileSystemEntry> target)
    {
        var toInsert = new List<FileSystemEntry>(batch.Count);
        foreach (var entry in batch)
        {
            if (PassesFilter(entry))
                toInsert.Add(entry);
        }
        if (toInsert.Count == 0) return;

        var comparer = BuildEntriesComparer();
        foreach (var entry in toInsert)
            target.Insert(UpperBound(target, entry, comparer), entry);

        if (GroupField == GroupField.None)
        {
            if (Groups.Count > 0)
                Groups = [];
        }
        else
        {
            InsertIntoGroups(toInsert, comparer);
        }
    }

    private static int UpperBound(IList<FileSystemEntry> list, FileSystemEntry entry, IComparer<FileSystemEntry> comparer)
    {
        var low = 0;
        var high = list.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (comparer.Compare(list[middle], entry) <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private void InsertIntoGroups(IReadOnlyList<FileSystemEntry> entries, IComparer<FileSystemEntry> comparer)
    {
        foreach (var entry in entries)
        {
            var key = GetGroupKey(entry);
            FileGroup? group = null;
            var groupIndex = -1;
            for (var index = 0; index < Groups.Count; index++)
            {
                if (string.Equals(Groups[index].Name, key, StringComparison.Ordinal))
                {
                    group = Groups[index];
                    groupIndex = index;
                    break;
                }
            }

            if (group == null)
            {
                group = new FileGroup { Name = key, Entries = [] };
                groupIndex = GetGroupInsertIndex(group, entry, comparer);
                Groups.Insert(groupIndex, group);
            }

            var insertAt = UpperBound(group.Entries, entry, comparer);
            group.Entries.Insert(insertAt, entry);
            if (insertAt == 0)
                MoveGroupBeforeHigherHeads(groupIndex, comparer);
        }
    }

    // Groups with equal rank stay ordered by their first (smallest) entry, which is
    // also the order a full rebuild discovers them in. When an insert lands at the
    // head of a group, nudge the group left past same-rank neighbours whose head is
    // now larger so the final layout matches the full rebuild.
    private void MoveGroupBeforeHigherHeads(int groupIndex, IComparer<FileSystemEntry> comparer)
    {
        var rank = GetGroupRank(Groups[groupIndex].Name);
        var head = Groups[groupIndex].Entries[0];
        var target = groupIndex;
        for (var index = groupIndex - 1; index >= 0; index--)
        {
            if (GetGroupRank(Groups[index].Name) != rank) break;
            if (Groups[index].Entries.Count == 0)
            {
                target = index;
                continue;
            }
            if (comparer.Compare(Groups[index].Entries[0], head) > 0)
                target = index;
            else
                break;
        }
        if (target == groupIndex) return;
        var group = Groups[groupIndex];
        Groups.RemoveAt(groupIndex);
        Groups.Insert(target, group);
    }

    private int GetGroupInsertIndex(FileGroup newGroup, FileSystemEntry firstEntry, IComparer<FileSystemEntry> comparer)
    {
        var newRank = GetGroupRank(newGroup.Name);
        for (var index = 0; index < Groups.Count; index++)
        {
            var existingRank = GetGroupRank(Groups[index].Name);
            if (existingRank > newRank)
                return index;
            if (existingRank == newRank
                && Groups[index].Entries.Count > 0
                && comparer.Compare(Groups[index].Entries[0], firstEntry) > 0)
            {
                return index;
            }
        }
        return Groups.Count;
    }

    private int GetGroupRank(string name) => GroupField switch
    {
        GroupField.Type => name == "文件夹" ? 1 : 0,
        GroupField.Modified => Array.IndexOf(DateGroupOrder, name),
        GroupField.Size => Array.IndexOf(SizeGroupOrder, name),
        _ => 0
    };

    private string GetGroupKey(FileSystemEntry entry) => GroupField switch
    {
        GroupField.Type => entry.IsVirtual
            ? GetAiTypeLabel(entry.VirtualFolderType!)
            : entry.IsDirectory ? "文件夹" : GetCategoryName(entry.Extension),
        GroupField.Modified => GetDateGroup(entry.LastModified),
        GroupField.Size => entry.IsDirectory ? "文件夹" : GetSizeGroup(entry.Size),
        _ => string.Empty
    };

    private IEnumerable<FileSystemEntry> SortEntries(IReadOnlyList<FileSystemEntry> entries) => SortField switch
    {
        SortField.Name => SortAscending
            ? entries.OrderBy(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            : entries.OrderBy(e => e.IsDirectory).ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
        SortField.Modified => SortAscending
            ? entries.OrderBy(e => e.IsDirectory).ThenBy(e => e.LastModified)
            : entries.OrderBy(e => e.IsDirectory).ThenByDescending(e => e.LastModified),
        SortField.Size => SortAscending
            ? entries.OrderBy(e => e.IsDirectory).ThenBy(e => e.Size)
            : entries.OrderBy(e => e.IsDirectory).ThenByDescending(e => e.Size),
        SortField.Type => SortAscending
            ? entries.OrderBy(e => e.IsDirectory).ThenBy(e => e.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            : entries.OrderBy(e => e.IsDirectory).ThenByDescending(e => e.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
        _ => entries.OrderBy(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
    };

    private static readonly string[] DateGroupOrder = ["今天", "昨天", "最近7天", "最近30天", "最近3个月", "今年更早", "更早"];
    private static readonly string[] SizeGroupOrder = ["大于 1 GB", "100 MB-1 GB", "1-100 MB", "小于 1 MB", "小于 1 KB", "空文件", "文件夹"];

    private List<FileGroup> BuildGroups(List<FileSystemEntry> sorted) => GroupField switch
    {
        GroupField.Type => sorted.GroupBy(GetGroupKey)
            .OrderBy(g => g.Key == "文件夹" ? 1 : 0)
            .Select(g => new FileGroup { Name = g.Key, Entries = g.ToList() }).ToList(),
        GroupField.Modified => sorted.GroupBy(GetGroupKey)
            .OrderBy(g => Array.IndexOf(DateGroupOrder, g.Key))
            .Select(g => new FileGroup { Name = g.Key, Entries = g.ToList() }).ToList(),
        GroupField.Size => sorted.GroupBy(GetGroupKey)
            .OrderBy(g => Array.IndexOf(SizeGroupOrder, g.Key))
            .Select(g => new FileGroup { Name = g.Key, Entries = g.ToList() }).ToList(),
        _ => []
    };

    private static string GetCategoryName(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tiff" or ".svg" or ".webp" or ".heic" => "图像",
        ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" => "影片",
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".wma" => "音频",
        ".pdf" => "PDF 文档",
        ".doc" or ".docx" or ".txt" or ".rtf" or ".odt" or ".pages" => "文档",
        ".xls" or ".xlsx" or ".csv" or ".numbers" => "电子表格",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".dmg" or ".pkg" => "归档文件",
        ".cs" or ".js" or ".ts" or ".py" or ".java" or ".cpp" or ".h" or ".go" or ".rs" or ".swift" or ".kt" => "源代码",
        ".html" or ".css" or ".json" or ".xml" or ".yaml" or ".yml" or ".toml" or ".md" => "开发文件",
        _ => "其他"
    };

    private static string GetDateGroup(DateTime date)
    {
        var diff = DateTime.Now - date;
        if (diff.TotalDays < 1) return "今天";
        if (diff.TotalDays < 2) return "昨天";
        if (diff.TotalDays < 7) return "最近7天";
        if (diff.TotalDays < 30) return "最近30天";
        if (diff.TotalDays < 90) return "最近3个月";
        if (diff.TotalDays < 365) return "今年更早";
        return "更早";
    }

    private static string GetSizeGroup(long size) => size switch
    {
        0 => "空文件",
        < 1024 => "小于 1 KB",
        < 1024 * 1024 => "小于 1 MB",
        < 100 * 1024 * 1024 => "1-100 MB",
        < 1024 * 1024 * 1024 => "100 MB-1 GB",
        _ => "大于 1 GB"
    };

    private static string GetAiTypeLabel(string virtualFolderType) => virtualFolderType switch
    {
        "face" => "人物",
        "scene" => "场景",
        "object" => "物品",
        "animal" => "动物",
        "location" => "地点",
        "date" => "日期",
        _ => virtualFolderType
    };

    // Mirrors the stable OrderBy chain in SortEntries so incremental inserts produce
    // the same total order as a full re-sort of the same stream.
    private sealed class EntriesComparer(SortField sortField, bool ascending) : IComparer<FileSystemEntry>
    {
        public int Compare(FileSystemEntry? left, FileSystemEntry? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;

            // Files always sort before directories, matching OrderBy(e => e.IsDirectory).
            var directoryOrder = left.IsDirectory.CompareTo(right.IsDirectory);
            if (directoryOrder != 0) return directoryOrder;

            return sortField switch
            {
                SortField.Name => Ordered(StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name)),
                SortField.Modified => Ordered(left.LastModified.CompareTo(right.LastModified)),
                SortField.Size => Ordered(left.Size.CompareTo(right.Size)),
                SortField.Type => TypeOrdered(left, right),
                _ => Ordered(StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name))
            };
        }

        private int Ordered(int result) => ascending ? result : -result;

        private int TypeOrdered(FileSystemEntry left, FileSystemEntry right)
        {
            var extension = Ordered(StringComparer.OrdinalIgnoreCase.Compare(left.Extension, right.Extension));
            if (extension != 0) return extension;
            // The name tie-breaker stays ascending in both sort directions.
            return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        }
    }
}
