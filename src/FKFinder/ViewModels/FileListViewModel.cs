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
    private readonly IThumbnailService? _thumbnailService;
    private readonly ICollectionService? _collectionService;
    private readonly IRatingService? _ratingService;
    private readonly ISettingsService? _settingsService;
    private readonly IFrequentFolderService? _frequentFolderService;
    private CancellationTokenSource? _searchCts;

    private IReadOnlyList<FileSystemEntry> _rawEntries = [];

    // Navigation history
    private readonly List<string> _historyStack = [];
    private int _historyIndex = -1;
    private bool _isNavigatingHistory;
    private readonly Dictionary<string, string?> _pathSelectedEntries = new();

    // Flag to indicate back/forward navigation for scroll restoration
    public bool IsRestoringNavigation { get; private set; }

    public string HomeDirectory => _fileService.HomeDirectory;

    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex < _historyStack.Count - 1;

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

    // Preview pane
    [ObservableProperty]
    private bool _isPreviewPaneVisible;

    // Hide system files like .DS_Store
    [ObservableProperty]
    private bool _hideSystemFiles = true;

    private static readonly HashSet<string> SystemFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", "desktop.ini", ".Spotlight-V100", ".Trashes", ".fseventsd"
    };

    // Collections
    [ObservableProperty]
    private ObservableCollection<Collection> _collections = [];

    [ObservableProperty]
    private bool _isCollectionView;

    [ObservableProperty]
    private int? _currentCollectionId;

    [ObservableProperty]
    private string? _currentCollectionName;

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
        IApplicationLauncherService? launcherService = null,
        IThumbnailService? thumbnailService = null,
        ICollectionService? collectionService = null,
        IRatingService? ratingService = null,
        ISettingsService? settingsService = null,
        IFrequentFolderService? frequentFolderService = null)
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
        _thumbnailService = thumbnailService;
        _collectionService = collectionService;
        _ratingService = ratingService;
        _settingsService = settingsService;
        _frequentFolderService = frequentFolderService;

        // Load persisted user preferences
        if (_settingsService != null)
        {
            ViewMode = _settingsService.Get<ViewMode>("ViewMode", ViewMode.List);
            SortField = _settingsService.Get<SortField>("SortField", SortField.Name);
            SortAscending = _settingsService.Get<bool>("SortAscending", true);
            GroupField = _settingsService.Get<GroupField>("GroupField", GroupField.None);
            IsPreviewPaneVisible = _settingsService.Get<bool>("IsPreviewPaneVisible", false);
            HideSystemFiles = _settingsService.Get<bool>("HideSystemFiles", true);
        }

        _ = LoadCollectionsAsync();
    }

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Validate that the path exists on the filesystem
        // Skip validation for trash directory (macOS SIP blocks .NET Directory.Exists)
        if (path != _fileService.TrashDirectory && !Directory.Exists(path))
        {
            StatusText = $"\u8def\u5f84\u4e0d\u5b58\u5728: {path}";
            return;
        }

        if (CurrentPath == path && Entries.Count > 0) return;

        // Show loading indicator FIRST for immediate visual feedback
        IsLoading = true;

        // Auto-close metadata panel when navigating
        if (IsMetadataPanelVisible)
            CloseMetadata();

        IsHomePage = false;
        IsCollectionView = false;
        CurrentCollectionId = null;
        CurrentCollectionName = null;
        try
        {
            // Save selected entry for current path before navigating away
            if (!string.IsNullOrEmpty(CurrentPath) && SelectedEntries.Count > 0)
                _pathSelectedEntries[CurrentPath] = SelectedEntries.First().Name;

            // Record history for back/forward navigation
            if (!_isNavigatingHistory)
            {
                // Trim forward history when navigating to a new path
                if (_historyIndex < _historyStack.Count - 1)
                    _historyStack.RemoveRange(_historyIndex + 1, _historyStack.Count - _historyIndex - 1);
                _historyStack.Add(path);
                _historyIndex = _historyStack.Count - 1;
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoForward));
            }

            CurrentPath = path;
            UpdateBreadcrumbs();
            await LoadDirectoryContentsAsync();

            // Record folder visit for frequent folders ranking
            _ = _frequentFolderService?.RecordVisitAsync(path);
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
    public async Task NavigateBackAsync()
    {
        if (!CanGoBack) return;
        _historyIndex--;
        _isNavigatingHistory = true;
        IsRestoringNavigation = true;
        try
        {
            await NavigateToAsync(_historyStack[_historyIndex]);
        }
        finally
        {
            _isNavigatingHistory = false;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
    }

    [RelayCommand]
    public async Task NavigateForwardAsync()
    {
        if (!CanGoForward) return;
        _historyIndex++;
        _isNavigatingHistory = true;
        IsRestoringNavigation = true;
        try
        {
            await NavigateToAsync(_historyStack[_historyIndex]);
        }
        finally
        {
            _isNavigatingHistory = false;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
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
        {
            // .app bundles should be launched as applications, not navigated into
            if (entry.IconKey == "app-bundle" && _launcherService != null)
                await _launcherService.OpenFileAsync(entry.FullPath);
            else
                await NavigateToAsync(entry.FullPath);
        }
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

    partial void OnViewModeChanged(ViewMode value) => _settingsService?.Set("ViewMode", value);

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

    partial void OnSortFieldChanged(SortField value) { _settingsService?.Set("SortField", value); ApplySortAndGroup(); }
    partial void OnSortAscendingChanged(bool value) { _settingsService?.Set("SortAscending", value); ApplySortAndGroup(); }
    partial void OnGroupFieldChanged(GroupField value) { _settingsService?.Set("GroupField", value); ApplySortAndGroup(); }

    public bool IsInTrash => CurrentPath == _fileService.TrashDirectory;

    public async Task ShowFileContextMenuAsync(FileSystemEntry entry, double x, double y)
    {
        ContextMenuEntry = entry;
        ContextMenuX = x;
        ContextMenuY = y;

        if (_contextMenuService != null)
        {
            if (IsInTrash)
            {
                var actions = await _contextMenuService.GetTrashFileContextMenuActionsAsync(entry);
                actions = WireUpTrashFileContextMenuActions(actions, entry);
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
            }
            else
            {
                var actions = await _contextMenuService.GetFileContextMenuActionsAsync(entry);
                actions = WireUpContextMenuActions(actions, entry);
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
            }
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
            if (IsCollectionView)
            {
                // Collection view: only show refresh, no new file/folder/paste
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(new[]
                {
                    new ContextMenuAction
                    {
                        Label = "刷新",
                        IconSvg = Icons.Refresh,
                        ShortcutText = "⌘R",
                        Execute = () => CurrentCollectionId != null
                            ? NavigateToCollectionAsync(CurrentCollectionId.Value)
                            : Task.CompletedTask
                    }
                });
            }
            else if (IsInTrash)
            {
                var actions = await _contextMenuService.GetTrashBackgroundContextMenuActionsAsync();
                actions = WireUpTrashBackgroundContextMenuActions(actions);
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
            }
            else
            {
                var actions = await _contextMenuService.GetBackgroundContextMenuActionsAsync(CurrentPath);
                actions = WireUpBackgroundContextMenuActions(actions);
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
            }
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
            // Override "打开" to open within FKFinder instead of system default
            if (action.Label == "打开")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    IsEnabled = action.IsEnabled,
                    Execute = () => OpenEntryCommand.ExecuteAsync(entry)
                });
            }
            // "显示包内容" navigates into the .app bundle directory
            else if (action.Label == "显示包内容")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    IsEnabled = action.IsEnabled,
                    Execute = () => NavigateToAsync(entry.FullPath)
                });
            }
            // Override "查看文件信息" to call ShowMetadata
            else if (action.Label == "查看文件信息")
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

        // Insert "添加到收藏夹" submenu before the last separator+info block
        if (_collectionService != null && Collections.Count > 0)
        {
            var insertIdx = result.FindLastIndex(a => a.IsSeparator);
            if (insertIdx < 0) insertIdx = result.Count;
            result.Insert(insertIdx, new ContextMenuAction
            {
                Label = "添加到收藏夹",
                IconSvg = Icons.CollectionAdd,
                SubItems = Collections.Select(c => new ContextMenuAction
                {
                    Label = c.Name,
                    IconSvg = Icons.Folder,
                    Execute = () => AddToCollectionAsync(c.Id, entry.FullPath)
                }).ToList()
            });
        }

        // In collection view, add "从收藏夹中移除" option
        if (IsCollectionView && CurrentCollectionId != null)
        {
            result.Add(new ContextMenuAction { IsSeparator = true });
            result.Add(new ContextMenuAction
            {
                Label = "从收藏夹中移除",
                IconSvg = Icons.Delete,
                Execute = () => RemoveFromCollectionAsync(entry.FullPath)
            });
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

    private List<ContextMenuAction> WireUpTrashFileContextMenuActions(IReadOnlyList<ContextMenuAction> actions, FileSystemEntry entry)
    {
        var result = new List<ContextMenuAction>();
        foreach (var action in actions)
        {
            if (action.IsSeparator) { result.Add(action); continue; }
            if (action.Label == "永久删除")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    Execute = async () =>
                    {
                        await _fileService.DeletePermanentlyAsync(entry.FullPath);
                        await LoadDirectoryContentsAsync(forceRefresh: true);
                    }
                });
            }
            else { result.Add(action); }
        }
        return result;
    }

    private List<ContextMenuAction> WireUpTrashBackgroundContextMenuActions(IReadOnlyList<ContextMenuAction> actions)
    {
        var result = new List<ContextMenuAction>();
        foreach (var action in actions)
        {
            if (action.IsSeparator) { result.Add(action); continue; }
            if (action.Label == "清倒废纸篓")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    Execute = async () =>
                    {
                        await _fileService.EmptyTrashAsync();
                        await LoadDirectoryContentsAsync(forceRefresh: true);
                    }
                });
            }
            else { result.Add(action); }
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

    private bool _wasHomePageBeforeSearch;

    [RelayCommand]
    public async Task SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) { ExitSearch(); return; }
        if (_searchService == null) return;

        _searchCts?.Cancel(); _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        // Remember home page state and switch to file list view for results
        if (!IsSearchMode)
            _wasHomePageBeforeSearch = IsHomePage;
        IsHomePage = false;
        IsSearchMode = true; SearchQuery = query; IsLoading = true;

        // Use HomeDirectory as search root when there's no current path
        var searchPath = string.IsNullOrEmpty(CurrentPath) ? HomeDirectory : CurrentPath;

        try
        {
            var results = new ObservableCollection<FileSystemEntry>();
            await foreach (var entry in _searchService.SearchAsync(searchPath, query, _searchCts.Token))
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

        // Restore home page if search was initiated from there
        if (_wasHomePageBeforeSearch)
        {
            IsHomePage = true;
            _wasHomePageBeforeSearch = false;
        }
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

        // Resolve app icons lazily in background (don't block the list display)
        _ = ResolveIconsInBackgroundAsync(entries);
        _ = ResolveThumbnailsInBackgroundAsync(entries);

        // Batch load ratings for current directory
        if (_ratingService != null)
        {
            try { await _ratingService.BatchLoadRatingsAsync(entries.Select(e => e.FullPath)); }
            catch { }
        }
    }

    private async Task ResolveIconsInBackgroundAsync(IReadOnlyList<FileSystemEntry> entries)
    {
        try
        {
            await _fileService.ResolveAppIconsAsync(entries, () =>
            {
                OnPropertyChanged(nameof(Entries));
            });
        }
        catch { }
    }

    private async Task ResolveThumbnailsInBackgroundAsync(IReadOnlyList<FileSystemEntry> entries)
    {
        if (_thumbnailService == null) return;
        try
        {
            var imageEntries = entries
                .Where(e => e.IconKey == "file-image" && e.ThumbnailUrl == null)
                .ToList();
            if (imageEntries.Count == 0) return;

            const int batchSize = 20;
            for (int i = 0; i < imageEntries.Count; i += batchSize)
            {
                var batch = imageEntries.Skip(i).Take(batchSize);
                foreach (var entry in batch)
                {
                    var bytes = await _thumbnailService.GetThumbnailAsync(entry.FullPath, 128);
                    if (bytes != null)
                    {
                        // Store as base64 data URI for simplicity
                        entry.ThumbnailUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
                    }
                }
                OnPropertyChanged(nameof(Entries));
            }
        }
        catch { }
    }

    private void ApplyEntries(IReadOnlyList<FileSystemEntry> entries)
    {
        _rawEntries = entries;
        ApplySortAndGroup();
        SelectedEntries.Clear();
        _lastClickedEntry = null;

        // Restore selected entry when navigating back/forward
        if (IsRestoringNavigation && _pathSelectedEntries.TryGetValue(CurrentPath, out var savedName) && savedName != null)
        {
            var entry = Entries.FirstOrDefault(e => e.Name == savedName);
            if (entry != null)
            {
                SelectedEntries.Add(entry);
                _lastClickedEntry = entry;
            }
            IsRestoringNavigation = false;
        }
        else
        {
            IsRestoringNavigation = false;
        }

        StatusText = $"{Entries.Count} 项";
    }

    partial void OnHideSystemFilesChanged(bool value)
    {
        _settingsService?.Set("HideSystemFiles", value);
        ApplySortAndGroup();
    }

    private void ApplySortAndGroup()
    {
        if (_rawEntries.Count == 0) { Entries = []; Groups = []; return; }
        var filtered = HideSystemFiles
            ? _rawEntries.Where(e => !SystemFileNames.Contains(e.Name)).ToList()
            : (IReadOnlyList<FileSystemEntry>)_rawEntries;
        var sorted = SortEntries(filtered).ToList();
        Entries = new ObservableCollection<FileSystemEntry>(sorted);
        Groups = GroupField == GroupField.None ? [] : new ObservableCollection<FileGroup>(BuildGroups(sorted));
        StatusText = $"{Entries.Count} 项";
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
        GroupField.Type => sorted.GroupBy(e => e.IsDirectory ? "文件夹" : GetCategoryName(e.Extension))
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

    private void UpdateBreadcrumbs()
    {
        var segments = new List<BreadcrumbSegment>();
        if (IsCollectionView && CurrentCollectionName != null)
        {
            segments.Add(new BreadcrumbSegment { Name = "收藏夹", FullPath = "", HasDropdown = false });
            segments.Add(new BreadcrumbSegment { Name = CurrentCollectionName, FullPath = "", HasDropdown = false });
        }
        else if (CurrentPath == _fileService.TrashDirectory)
        {
            segments.Add(new BreadcrumbSegment { Name = "废纸篓", FullPath = CurrentPath, HasDropdown = false });
        }
        else if (CurrentPath == "/")
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

    // ── Preview Pane ──

    [RelayCommand]
    public void TogglePreviewPane()
    {
        IsPreviewPaneVisible = !IsPreviewPaneVisible;
        _settingsService?.Set("IsPreviewPaneVisible", IsPreviewPaneVisible);
    }

    // ── Star Ratings ──

    public int GetRating(string filePath)
    {
        return _ratingService?.GetRatingCached(filePath) ?? 0;
    }

    public async Task SetRatingAsync(string filePath, int rating)
    {
        if (_ratingService == null) return;
        await _ratingService.SetRatingAsync(filePath, rating);
        OnPropertyChanged(nameof(Entries));
    }

    // ── Collections ──

    public async Task LoadCollectionsAsync()
    {
        if (_collectionService == null) return;
        try
        {
            var list = await _collectionService.GetAllCollectionsAsync();
            Collections = new ObservableCollection<Collection>(list);
        }
        catch { }
    }

    public async Task NavigateToCollectionAsync(int collectionId)
    {
        if (_collectionService == null) return;

        if (IsMetadataPanelVisible)
            CloseMetadata();

        IsHomePage = false;
        IsCollectionView = true;
        CurrentCollectionId = collectionId;
        IsLoading = true;

        try
        {
            var collection = Collections.FirstOrDefault(c => c.Id == collectionId);
            CurrentCollectionName = collection?.Name ?? "收藏夹";

            var filePaths = await _collectionService.GetFilePathsInCollectionAsync(collectionId);
            var entries = new List<FileSystemEntry>();
            int removed = 0;

            foreach (var path in filePaths)
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    var entry = await _fileIndex.GetEntryAsync(path);
                    if (entry != null)
                        entries.Add(entry);
                    else
                    {
                        var isDir = Directory.Exists(path);
                        if (isDir)
                        {
                            var di = new DirectoryInfo(path);
                            entries.Add(new FileSystemEntry
                            {
                                FullPath = path,
                                Name = di.Name,
                                Extension = di.Extension,
                                Size = 0,
                                LastModified = di.LastWriteTime,
                                Created = di.CreationTime,
                                IsDirectory = true,
                                IconKey = Indexing.SqliteFileIndex.ResolveBundleIconKey(di.Extension)
                            });
                        }
                        else
                        {
                            var fi = new FileInfo(path);
                            if (fi.Exists)
                            {
                                entries.Add(new FileSystemEntry
                                {
                                    FullPath = path,
                                    Name = fi.Name,
                                    Extension = fi.Extension,
                                    Size = fi.Length,
                                    LastModified = fi.LastWriteTime,
                                    Created = fi.CreationTime,
                                    IsDirectory = false,
                                    IconKey = Indexing.SqliteFileIndex.ResolveIconKey(fi.Extension)
                                });
                            }
                        }
                    }
                }
                else
                {
                    removed++;
                }
            }

            CurrentPath = $"__collection:{collectionId}";
            UpdateBreadcrumbs();
            ApplyEntries(entries);

            if (removed > 0)
                StatusText = $"{entries.Count} 项 ({removed} 项已移除)";

            if (_ratingService != null && entries.Count > 0)
            {
                try { await _ratingService.BatchLoadRatingsAsync(entries.Select(e => e.FullPath)); }
                catch { }
            }

            _ = ResolveIconsInBackgroundAsync(entries);
            _ = ResolveThumbnailsInBackgroundAsync(entries);
        }
        catch (Exception ex)
        {
            StatusText = $"打开收藏夹失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task AddToCollectionAsync(int collectionId, string filePath)
    {
        if (_collectionService == null) return;
        await _collectionService.AddFileToCollectionAsync(collectionId, filePath);
        var collection = Collections.FirstOrDefault(c => c.Id == collectionId);
        StatusText = $"已添加到 {collection?.Name ?? "收藏夹"}";

        if (IsCollectionView && CurrentCollectionId == collectionId)
            await NavigateToCollectionAsync(collectionId);
    }

    public async Task RemoveFromCollectionAsync(string filePath)
    {
        if (_collectionService == null || !IsCollectionView || CurrentCollectionId == null) return;
        await _collectionService.RemoveFileFromCollectionAsync(CurrentCollectionId.Value, filePath);
        await NavigateToCollectionAsync(CurrentCollectionId.Value);
    }

    public bool IsCollectionNameDuplicate(string name, int? excludeId = null)
    {
        return Collections.Any(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
            && c.Id != excludeId);
    }

    public async Task CreateCollectionAsync(string name)
    {
        if (_collectionService == null || string.IsNullOrWhiteSpace(name)) return;
        if (IsCollectionNameDuplicate(name))
        {
            StatusText = $"收藏夹 \"{name}\" 已存在";
            return;
        }
        await _collectionService.CreateCollectionAsync(name);
        await LoadCollectionsAsync();
    }

    public async Task RenameCollectionAsync(int id, string newName)
    {
        if (_collectionService == null || string.IsNullOrWhiteSpace(newName)) return;
        if (IsCollectionNameDuplicate(newName, id))
        {
            StatusText = $"收藏夹 \"{newName}\" 已存在";
            return;
        }
        await _collectionService.RenameCollectionAsync(id, newName);
        await LoadCollectionsAsync();
        if (IsCollectionView && CurrentCollectionId == id)
            CurrentCollectionName = newName;
    }

    public async Task DeleteCollectionAsync(int id)
    {
        if (_collectionService == null) return;
        await _collectionService.DeleteCollectionAsync(id);
        await LoadCollectionsAsync();
        if (IsCollectionView && CurrentCollectionId == id)
            GoHome();
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
