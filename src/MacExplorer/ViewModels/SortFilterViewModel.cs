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

    public SortFilterViewModel(
        ISettingsService? settingsService = null,
        Microsoft.Extensions.Logging.ILogger<SortFilterViewModel>? logger = null)
    {
        _settingsService = settingsService;
        _logger = logger;

        // Load persisted user preferences
        if (_settingsService != null)
        {
            ViewMode = _settingsService.Get<ViewMode>("ViewMode", ViewMode.List);
            SortField = _settingsService.Get<SortField>("SortField", SortField.Name);
            SortAscending = _settingsService.Get<bool>("SortAscending", true);
            GroupField = _settingsService.Get<GroupField>("GroupField", GroupField.None);
            HideSystemFiles = _settingsService.Get<bool>("HideSystemFiles", true);
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

    public void ApplySortAndGroup(Action<ObservableCollection<FileSystemEntry>> setEntries, Action<string> setStatus)
    {
        if (_rawEntries.Count == 0) { setEntries([]); Groups = []; return; }
        var filtered = _rawEntries.Where(e => !e.Name.EndsWith(".fkfinder-tmp"));
        if (HideSystemFiles)
            filtered = filtered.Where(e => !SystemFileNames.Contains(e.Name));
        var list = filtered.ToList();
        var sorted = SortEntries(list).ToList();
        setEntries(new ObservableCollection<FileSystemEntry>(sorted));
        Groups = GroupField == GroupField.None ? [] : new ObservableCollection<FileGroup>(BuildGroups(sorted));
        setStatus($"{sorted.Count} 项");
    }

    public void SetRawEntries(IReadOnlyList<FileSystemEntry> entries)
    {
        _rawEntries = entries;
    }

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
        GroupField.Type => sorted.GroupBy(e =>
                e.IsVirtual ? GetAiTypeLabel(e.VirtualFolderType!)
                : (e.IsDirectory ? "文件夹" : GetCategoryName(e.Extension)))
            .OrderBy(g => g.Key == "文件夹" ? 1 : 0)
            .Select(g => new FileGroup { Name = g.Key, Entries = g.ToList() }).ToList(),
        GroupField.Modified => sorted.GroupBy(e => GetDateGroup(e.LastModified))
            .OrderBy(g => Array.IndexOf(DateGroupOrder, g.Key))
            .Select(g => new FileGroup { Name = g.Key, Entries = g.ToList() }).ToList(),
        GroupField.Size => sorted.GroupBy(e => e.IsDirectory ? "文件夹" : GetSizeGroup(e.Size))
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
}