using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FKFinder.Indexing;
using FKFinder.Models;
using FKFinder.Services;

namespace FKFinder.ViewModels;

public partial class FileListViewModel : ObservableObject
{
    private readonly IFileService _fileService;
    private readonly IFileIndex _fileIndex;
    private readonly IFileIndexWriter _fileIndexWriter;
    private readonly IndexConfiguration _indexConfig;
    private readonly IContextMenuService? _contextMenuService;
    private readonly IMetadataService? _metadataService;
    private readonly IClipboardService? _clipboardService;
    private readonly ISearchService? _searchService;
    private readonly IApplicationLauncherService? _launcherService;
    private CancellationTokenSource? _searchCts;

    private IReadOnlyList<FileSystemEntry> _rawEntries = [];

    public string HomeDirectory => _fileService.HomeDirectory;

    [ObservableProperty]
    private string _currentPath = "";

    [ObservableProperty]
    private ObservableCollection<FileSystemEntry> _entries = [];

    [ObservableProperty]
    private ObservableCollection<FileSystemEntry> _selectedEntries = [];

    [ObservableProperty]
    private ObservableCollection<BreadcrumbSegment> _breadcrumbs = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

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
    private bool _isContextMenuVisible;

    [ObservableProperty]
    private double _contextMenuX;

    [ObservableProperty]
    private double _contextMenuY;

    [ObservableProperty]
    private ObservableCollection<ContextMenuAction> _contextMenuActions = [];

    [ObservableProperty]
    private FileSystemEntry? _contextMenuEntry;

    [ObservableProperty]
    private bool _isMetadataPanelVisible;

    [ObservableProperty]
    private FileMetadata? _currentMetadata;

    [ObservableProperty]
    private bool _isSearchMode;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isHomePage = true;

    // Last clicked entry for shift-range selection
    private FileSystemEntry? _lastClickedEntry;

    public FileListViewModel(
        IFileService fileService,
        IFileIndex fileIndex,
        IFileIndexWriter fileIndexWriter,
        IndexConfiguration indexConfig,
        IContextMenuService? contextMenuService = null,
        IMetadataService? metadataService = null,
        IClipboardService? clipboardService = null,
        ISearchService? searchService = null,
        IApplicationLauncherService? launcherService = null)
    {
        _fileService = fileService;
        _fileIndex = fileIndex;
        _fileIndexWriter = fileIndexWriter;
        _indexConfig = indexConfig;
        _contextMenuService = contextMenuService;
        _metadataService = metadataService;
        _clipboardService = clipboardService;
        _searchService = searchService;
        _launcherService = launcherService;
    }

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (CurrentPath == path && Entries.Count > 0) return;

        IsHomePage = false;
        IsLoading = true;
        try
        {
            CurrentPath = path;
            UpdateBreadcrumbs();
            await LoadDirectoryContentsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"无法访问: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task NavigateUpAsync()
    {
        if (IsHomePage) return;
        var parentPath = _fileService.GetParentPath(CurrentPath);
        if (parentPath != CurrentPath)
            await NavigateToAsync(parentPath);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsHomePage) return;
        IsLoading = true;
        try { await LoadDirectoryContentsAsync(forceRefresh: true); }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public void GoHome()
    {
        IsHomePage = true;
        CurrentPath = "";
        Entries.Clear();
        Breadcrumbs.Clear();
        Groups.Clear();
        StatusText = "";
        SelectedEntries.Clear();
    }

    [RelayCommand]
    public async Task OpenEntryAsync(FileSystemEntry entry)
    {
        if (entry.IsDirectory)
            await NavigateToAsync(entry.FullPath);
        else if (_launcherService != null)
            await _launcherService.OpenFileAsync(entry.FullPath);
    }

    /// <summary>
    /// Select a single entry, deselecting all others.
    /// With cmdKey: toggle this entry in multi-selection.
    /// With shiftKey: range-select from last clicked to this entry.
    /// </summary>
    public void SelectEntry(FileSystemEntry entry, bool cmdKey = false, bool shiftKey = false)
    {
        if (shiftKey && _lastClickedEntry != null)
        {
            // Range select
            var list = Entries.ToList();
            var startIdx = list.IndexOf(_lastClickedEntry);
            var endIdx = list.IndexOf(entry);
            if (startIdx < 0 || endIdx < 0) return;

            if (startIdx > endIdx) (startIdx, endIdx) = (endIdx, startIdx);

            if (!cmdKey) SelectedEntries.Clear();

            for (int i = startIdx; i <= endIdx; i++)
            {
                if (!SelectedEntries.Contains(list[i]))
                    SelectedEntries.Add(list[i]);
            }
        }
        else if (cmdKey)
        {
            // Toggle this entry
            if (SelectedEntries.Contains(entry))
                SelectedEntries.Remove(entry);
            else
                SelectedEntries.Add(entry);
            _lastClickedEntry = entry;
        }
        else
        {
            // Single select
            SelectedEntries.Clear();
            SelectedEntries.Add(entry);
            _lastClickedEntry = entry;
        }
    }

    public void SelectAll()
    {
        SelectedEntries.Clear();
        foreach (var entry in Entries)
            SelectedEntries.Add(entry);
    }

    [RelayCommand]
    public void ClearSelection()
    {
        SelectedEntries.Clear();
        _lastClickedEntry = null;
    }

    [RelayCommand]
    public void ToggleViewMode()
    {
        ViewMode = ViewMode == ViewMode.Grid ? ViewMode.List : ViewMode.Grid;
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
        ApplySortAndGroup();
    }

    partial void OnSortFieldChanged(SortField value) => ApplySortAndGroup();
    partial void OnSortAscendingChanged(bool value) => ApplySortAndGroup();
    partial void OnGroupFieldChanged(GroupField value) => ApplySortAndGroup();

    public async Task ShowFileContextMenuAsync(FileSystemEntry entry, double x, double y)
    {
        ContextMenuEntry = entry;
        ContextMenuX = x;
        ContextMenuY = y;

        if (_contextMenuService != null)
        {
            var actions = await _contextMenuService.GetFileContextMenuActionsAsync(entry);
            // Wire up ViewModel commands for context menu actions
            actions = WireUpContextMenuActions(actions, entry);
            ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
        }

        IsContextMenuVisible = true;
    }

    public async Task ShowBackgroundContextMenuAsync(double x, double y)
    {
        ContextMenuEntry = null;
        ContextMenuX = x;
        ContextMenuY = y;

        if (_contextMenuService != null)
        {
            var actions = await _contextMenuService.GetBackgroundContextMenuActionsAsync(CurrentPath);
            actions = WireUpBackgroundContextMenuActions(actions);
            ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
        }

        IsContextMenuVisible = true;
    }

    private List<ContextMenuAction> WireUpContextMenuActions(IReadOnlyList<ContextMenuAction> actions, FileSystemEntry entry)
    {
        var result = new List<ContextMenuAction>();
        foreach (var action in actions)
        {
            if (action.IsSeparator)
            {
                result.Add(action);
                continue;
            }
            // Override "查看文件信息" to call ShowMetadata
            if (action.Label == "查看文件信息")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    IsEnabled = action.IsEnabled,
                    Execute = () => ShowMetadataCommand.ExecuteAsync(entry)
                });
            }
            else if (action.Label == "拷贝")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    Execute = () => { CopySelected(); return Task.CompletedTask; }
                });
            }
            else if (action.Label == "剪切")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    Execute = () => { CutSelected(); return Task.CompletedTask; }
                });
            }
            else if (action.Label == "粘贴")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    IsEnabled = _clipboardService?.HasClipboardFiles ?? false,
                    Execute = () => PasteCommand.ExecuteAsync(null)
                });
            }
            else if (action.Label == "移到废纸篓")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    Execute = async () =>
                    {
                        SelectedEntries.Clear();
                        SelectedEntries.Add(entry);
                        await DeleteSelectedCommand.ExecuteAsync(null);
                    }
                });
            }
            else if (action.Label == "重命名")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    Execute = () =>
                    {
                        RequestRename(entry);
                        return Task.CompletedTask;
                    }
                });
            }
            else
            {
                result.Add(action);
            }
        }
        return result;
    }

    private List<ContextMenuAction> WireUpBackgroundContextMenuActions(IReadOnlyList<ContextMenuAction> actions)
    {
        var result = new List<ContextMenuAction>();
        foreach (var action in actions)
        {
            if (action.IsSeparator) { result.Add(action); continue; }

            if (action.Label == "新建文件夹")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    Execute = () => CreateNewFolderCommand.ExecuteAsync(null)
                });
            }
            else if (action.Label == "新建文件")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    Execute = () => CreateNewFileCommand.ExecuteAsync(null)
                });
            }
            else if (action.Label == "粘贴")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    IsEnabled = _clipboardService?.HasClipboardFiles ?? false,
                    Execute = () => PasteCommand.ExecuteAsync(null)
                });
            }
            else if (action.Label == "刷新")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    Execute = () => RefreshCommand.ExecuteAsync(null)
                });
            }
            else
            {
                result.Add(action);
            }
        }
        return result;
    }

    [RelayCommand]
    public void CloseContextMenu()
    {
        IsContextMenuVisible = false;
        ContextMenuActions.Clear();
        ContextMenuEntry = null;
    }

    [RelayCommand]
    public async Task ShowMetadataAsync(FileSystemEntry entry)
    {
        if (_metadataService == null) return;
        try
        {
            CurrentMetadata = await _metadataService.GetMetadataAsync(entry.FullPath);
            IsMetadataPanelVisible = true;
        }
        catch (Exception ex)
        {
            StatusText = $"获取元数据失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void CloseMetadata()
    {
        IsMetadataPanelVisible = false;
        CurrentMetadata = null;
    }

    [RelayCommand]
    public void CopySelected()
    {
        if (_clipboardService == null || SelectedEntries.Count == 0) return;
        _clipboardService.CopyFiles(SelectedEntries.Select(e => e.FullPath).ToArray());
        StatusText = $"已拷贝 {SelectedEntries.Count} 项";
    }

    [RelayCommand]
    public void CutSelected()
    {
        if (_clipboardService == null || SelectedEntries.Count == 0) return;
        _clipboardService.CutFiles(SelectedEntries.Select(e => e.FullPath).ToArray());
        StatusText = $"已剪切 {SelectedEntries.Count} 项";
    }

    [RelayCommand]
    public async Task PasteAsync()
    {
        if (_clipboardService == null || !_clipboardService.HasClipboardFiles) return;
        var entry = _clipboardService.GetClipboardEntry();
        if (entry == null) return;

        try
        {
            foreach (var sourcePath in entry.SourcePaths)
            {
                if (entry.Operation == ClipboardOperation.Copy)
                    await _fileService.CopyAsync(sourcePath, CurrentPath);
                else
                    await _fileService.MoveAsync(sourcePath, CurrentPath);
            }
            if (entry.Operation == ClipboardOperation.Cut) _clipboardService.Clear();
            await LoadDirectoryContentsAsync(forceRefresh: true);
            StatusText = $"已粘贴 {entry.SourcePaths.Count} 项";
        }
        catch (Exception ex) { StatusText = $"粘贴失败: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (SelectedEntries.Count == 0) return;
        try
        {
            foreach (var entry in SelectedEntries.ToList())
                await _fileService.DeleteAsync(entry.FullPath, moveToTrash: true);
            await LoadDirectoryContentsAsync(forceRefresh: true);
        }
        catch (Exception ex) { StatusText = $"删除失败: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) { ExitSearch(); return; }
        if (_searchService == null) return;

        _searchCts?.Cancel(); _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        IsSearchMode = true; SearchQuery = query; IsLoading = true;

        try
        {
            var results = new ObservableCollection<FileSystemEntry>();
            await foreach (var entry in _searchService.SearchAsync(CurrentPath, query, _searchCts.Token))
                results.Add(entry);
            Entries = results; SelectedEntries.Clear();
            StatusText = $"搜索 \"{query}\" — 找到 {Entries.Count} 项";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"搜索失败: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    public void ExitSearch()
    {
        _searchCts?.Cancel();
        IsSearchMode = false; SearchQuery = string.Empty;
    }

    [RelayCommand]
    public async Task CreateNewFolderAsync()
    {
        try
        {
            var name = "未命名文件夹";
            var fullPath = await _fileService.CreateFolderAsync(CurrentPath, name);
            await LoadDirectoryContentsAsync(forceRefresh: true);
            var newEntry = Entries.FirstOrDefault(e => e.FullPath == fullPath);
            if (newEntry != null)
            {
                SelectedEntries.Clear();
                SelectedEntries.Add(newEntry);
            }
        }
        catch (Exception ex) { StatusText = $"创建文件夹失败: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task CreateNewFileAsync(string? extension = null)
    {
        try
        {
            var ext = extension ?? ".txt";
            var name = ext == ".txt" ? "未命名.txt" : $"未命名{ext}";
            var fullPath = await _fileService.CreateFileAsync(CurrentPath, name);
            await LoadDirectoryContentsAsync(forceRefresh: true);
            var newEntry = Entries.FirstOrDefault(e => e.FullPath == fullPath);
            if (newEntry != null)
            {
                SelectedEntries.Clear();
                SelectedEntries.Add(newEntry);
            }
        }
        catch (Exception ex) { StatusText = $"创建文件失败: {ex.Message}"; }
    }

    public async Task MoveEntryAsync(FileSystemEntry source, FileSystemEntry targetFolder)
    {
        if (!targetFolder.IsDirectory) return;
        try
        {
            await _fileService.MoveAsync(source.FullPath, targetFolder.FullPath);
            await LoadDirectoryContentsAsync(forceRefresh: true);
        }
        catch (Exception ex) { StatusText = $"移动失败: {ex.Message}"; }
    }

    // Rename support: event to notify the view to start inline rename
    public event Action<FileSystemEntry>? RenameRequested;

    public void RequestRename(FileSystemEntry entry)
    {
        RenameRequested?.Invoke(entry);
    }

    public async Task RenameEntryAsync(FileSystemEntry entry, string newName)
    {
        try
        {
            await _fileService.RenameAsync(entry.FullPath, newName);
            await LoadDirectoryContentsAsync(forceRefresh: true);
            // Re-select the renamed entry
            var renamed = Entries.FirstOrDefault(e => e.Name == newName);
            if (renamed != null)
            {
                SelectedEntries.Clear();
                SelectedEntries.Add(renamed);
            }
        }
        catch (Exception ex) { StatusText = $"重命名失败: {ex.Message}"; }
    }

    private async Task LoadDirectoryContentsAsync(bool forceRefresh = false)
    {
        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            if (!forceRefresh && _fileIndex != null && _indexConfig.ShouldIndex(CurrentPath))
            {
                var isFresh = await _fileIndex.IsDirectoryFreshAsync(CurrentPath, _indexConfig.FreshnessThreshold);
                if (isFresh)
                {
                    entries = await _fileIndex.GetDirectoryContentsAsync(CurrentPath);
                    if (entries.Count > 0) { ApplyEntries(entries); return; }
                }
            }
        }
        catch { }

        entries = await _fileService.GetDirectoryContentsAsync(CurrentPath);

        try
        {
            if (_fileIndexWriter != null && _indexConfig.ShouldIndex(CurrentPath) && entries.Count > 0)
                await _fileIndexWriter.UpdateDirectoryAsync(CurrentPath, entries);
        }
        catch { }

        ApplyEntries(entries);
    }

    private void ApplyEntries(IReadOnlyList<FileSystemEntry> entries)
    {
        _rawEntries = entries;
        ApplySortAndGroup();
        SelectedEntries.Clear();
        _lastClickedEntry = null;
        StatusText = $"{Entries.Count} 项";
    }

    private void ApplySortAndGroup()
    {
        if (_rawEntries.Count == 0) { Entries = []; Groups = []; return; }
        var sorted = SortEntries(_rawEntries).ToList();
        Entries = new ObservableCollection<FileSystemEntry>(sorted);
        Groups = GroupField == GroupField.None ? [] : new ObservableCollection<FileGroup>(BuildGroups(sorted));
    }

    private IEnumerable<FileSystemEntry> SortEntries(IReadOnlyList<FileSystemEntry> entries) => SortField switch
    {
        SortField.Name => SortAscending
            ? entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            : entries.OrderBy(e => !e.IsDirectory).ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
        SortField.Modified => SortAscending
            ? entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.LastModified)
            : entries.OrderBy(e => !e.IsDirectory).ThenByDescending(e => e.LastModified),
        SortField.Size => SortAscending
            ? entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Size)
            : entries.OrderBy(e => !e.IsDirectory).ThenByDescending(e => e.Size),
        SortField.Type => SortAscending
            ? entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            : entries.OrderBy(e => !e.IsDirectory).ThenByDescending(e => e.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
        _ => entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
    };

    private List<FileGroup> BuildGroups(List<FileSystemEntry> sorted) => GroupField switch
    {
        GroupField.Type => sorted.GroupBy(e => e.IsDirectory ? "文件夹" : GetCategoryName(e.Extension)).Select(g => new FileGroup { Name = g.Key, Entries = g.ToList() }).ToList(),
        GroupField.Modified => sorted.GroupBy(e => GetDateGroup(e.LastModified)).Select(g => new FileGroup { Name = g.Key, Entries = g.ToList() }).ToList(),
        GroupField.Size => sorted.GroupBy(e => e.IsDirectory ? "文件夹" : GetSizeGroup(e.Size)).Select(g => new FileGroup { Name = g.Key, Entries = g.ToList() }).ToList(),
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

    private void UpdateBreadcrumbs()
    {
        var segments = new List<BreadcrumbSegment>();
        if (CurrentPath == "/")
        {
            segments.Add(new BreadcrumbSegment { Name = "/", FullPath = "/", HasDropdown = false });
        }
        else
        {
            segments.Add(new BreadcrumbSegment { Name = "/", FullPath = "/", HasDropdown = true });
            var parts = CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var buildPath = "";
            for (int i = 0; i < parts.Length; i++)
            {
                buildPath += "/" + parts[i];
                segments.Add(new BreadcrumbSegment { Name = parts[i], FullPath = buildPath, HasDropdown = i < parts.Length - 1 });
            }
        }
        Breadcrumbs = new ObservableCollection<BreadcrumbSegment>(segments);
    }
}

public enum ViewMode { Grid, List }
public enum SortField { Name, Modified, Size, Type }
public enum GroupField { None, Type, Modified, Size }

public class FileGroup
{
    public string Name { get; init; } = "";
    public IReadOnlyList<FileSystemEntry> Entries { get; init; } = [];
}
