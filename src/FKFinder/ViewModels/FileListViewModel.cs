using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FKFinder.Indexing;
using FKFinder.Models;
using FKFinder.Services;
using Microsoft.Extensions.Logging;

namespace FKFinder.ViewModels;

public partial class FileListViewModel : ObservableObject
{
    private readonly IFileService _fileService;
    private readonly IFileIndex _fileIndex;
    private readonly IFileIndexWriter _fileIndexWriter;
    private readonly IndexConfiguration _indexConfig;
    private readonly IContextMenuService? _contextMenuService;
    private readonly IMetadataService? _metadataService;
    private readonly IThumbnailService? _thumbnailService;
    private readonly INativeContextMenuService? _nativeContextMenuService;
    private readonly IDragDropBridge? _dragDropBridge;
    private readonly IDirectoryChangeNotifier? _directoryChangeNotifier;
    private readonly IClipboardService? _clipboardService;
    private readonly IApplicationLauncherService? _launcherService;
    private readonly ISettingsService? _settingsService;
    private readonly IArchiveService? _archiveService;
    private readonly ILogger<FileListViewModel>? _logger;

    // Sub-viewmodels
    private readonly NavigationViewModel _navigation;
    private readonly FileOpsViewModel _fileOps;
    private readonly SearchViewModel _search;
    private readonly ArchiveViewModel _archive;
    private readonly AiViewModel _ai;
    private readonly CollectionViewModel _collection;
    private readonly SortFilterViewModel _sortFilter;

    private bool _isRefreshingFromNotification;
    private FileSystemEntry? _lastClickedEntry;

    // ── Properties that remain in coordinator ──

    [ObservableProperty]
    private ObservableCollection<FileSystemEntry> _entries = [];

    [ObservableProperty]
    private ObservableCollection<FileSystemEntry> _selectedEntries = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

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
    private bool _isPreviewPaneVisible;

    // Enums - kept here for backward compatibility
    public enum ScrollMode { ResetToTop, RestoreNavigation, ScrollToSelected, PreservePosition }
    public ScrollMode ScrollBehaviorAfterLoad { get; set; } = ScrollMode.ResetToTop;

    // Forwarded properties from sub-viewmodels for binding
    public string CurrentPath => _navigation.CurrentPath;
    public bool CanGoBack => _navigation.CanGoBack;
    public bool CanGoForward => _navigation.CanGoForward;
    public bool IsHomePage => _navigation.IsHomePage;
    public ObservableCollection<BreadcrumbSegment> Breadcrumbs => _navigation.Breadcrumbs;
    public string HomeDirectory => _fileService.HomeDirectory;

    // Archive state forwarded
    public bool IsArchiveView => _navigation.IsArchiveView;
    public string? CurrentArchivePath => _navigation.CurrentArchivePath;
    public string CurrentArchiveInternalPath => _navigation.CurrentArchiveInternalPath;
    public bool IsCompressDialogVisible => _archive.IsCompressDialogVisible;
    public CompressOptions? PendingCompressOptions => _archive.PendingCompressOptions;
    public string? ActiveTaskId => _archive.ActiveTaskId;

    // Search state forwarded
    public bool IsSearchMode => _navigation.IsSearchMode;
    public string SearchQuery => _navigation.SearchQuery;

    // Collection state forwarded
    public bool IsCollectionView => _navigation.IsCollectionView;
    public int? CurrentCollectionId => _navigation.CurrentCollectionId;
    public string? CurrentCollectionName => _navigation.CurrentCollectionName;
    public ObservableCollection<Collection> Collections => _collection.Collections;
    public ObservableCollection<PinnedFolder> PinnedFolders => _collection.PinnedFolders;

    // AI state forwarded
    public bool IsAiView => _navigation.IsAiView;
    public AiViewMode AiViewMode => _ai.AiViewMode;
    public int? CurrentFaceClusterId => _ai.CurrentFaceClusterId;
    public string? CurrentAiContextLabel => _ai.CurrentAiContextLabel;
    public bool IsAiAnalysisEnabled
    {
        get => _ai.IsAiAnalysisEnabled;
        set => _ai.IsAiAnalysisEnabled = value;
    }
    public ObservableCollection<FaceCluster> FaceClusters => _ai.FaceClusters;
    public ObservableCollection<AiCategory> AiCategories => _ai.AiCategories;
    public ObservableCollection<AiCategory> TextTokens => _ai.TextTokens;

    // Sort/Filter state forwarded
    public ViewMode ViewMode => _sortFilter.ViewMode;
    public SortField SortField => _sortFilter.SortField;
    public bool SortAscending => _sortFilter.SortAscending;
    public GroupField GroupField
    {
        get => _sortFilter.GroupField;
        set => _sortFilter.GroupField = value;
    }
    public ObservableCollection<FileGroup> Groups => _sortFilter.Groups;
    public bool HideSystemFiles
    {
        get => _sortFilter.HideSystemFiles;
        set => _sortFilter.HideSystemFiles = value;
    }

    // Pending select for auto-selection after navigation
    public string? PendingSelectFileName
    {
        get => _navigation.PendingSelectFileName;
        set => _navigation.PendingSelectFileName = value;
    }

    // Cut paths from FileOps
    public HashSet<string> CutPaths => _fileOps.CutPaths;

    public bool IsRestoringNavigation => ScrollBehaviorAfterLoad == ScrollMode.RestoreNavigation;

    public FileListViewModel(
        NavigationViewModel navigation,
        FileOpsViewModel fileOps,
        SearchViewModel search,
        ArchiveViewModel archive,
        AiViewModel ai,
        CollectionViewModel collection,
        SortFilterViewModel sortFilter,
        IFileService fileService,
        IFileIndex fileIndex,
        IFileIndexWriter fileIndexWriter,
        IndexConfiguration indexConfig,
        IContextMenuService? contextMenuService = null,
        IMetadataService? metadataService = null,
        IThumbnailService? thumbnailService = null,
        INativeContextMenuService? nativeContextMenuService = null,
        IClipboardService? clipboardService = null,
        IApplicationLauncherService? launcherService = null,
        ISettingsService? settingsService = null,
        IArchiveService? archiveService = null,
        IDragDropBridge? dragDropBridge = null,
        IDirectoryChangeNotifier? directoryChangeNotifier = null,
        ILoggerFactory? loggerFactory = null)
    {
        _navigation = navigation;
        _fileOps = fileOps;
        _search = search;
        _archive = archive;
        _ai = ai;
        _collection = collection;
        _sortFilter = sortFilter;
        _fileService = fileService;
        _fileIndex = fileIndex;
        _fileIndexWriter = fileIndexWriter;
        _indexConfig = indexConfig;
        _contextMenuService = contextMenuService;
        _metadataService = metadataService;
        _thumbnailService = thumbnailService;
        _nativeContextMenuService = nativeContextMenuService;
        _clipboardService = clipboardService;
        _launcherService = launcherService;
        _settingsService = settingsService;
        _archiveService = archiveService;
        _dragDropBridge = dragDropBridge;
        _directoryChangeNotifier = directoryChangeNotifier;
        _logger = loggerFactory?.CreateLogger<FileListViewModel>();

        // Wire up RenameRequested event from FileOps
        _fileOps.RequestRename += OnFileOpsRenameRequested;

        // Wire up PropertyChanged events from sub-viewmodels to forward notifications
        _navigation.PropertyChanged += OnNavigationPropertyChanged;
        _ai.PropertyChanged += OnAiPropertyChanged;
        _archive.PropertyChanged += OnArchivePropertyChanged;
        _collection.PropertyChanged += OnCollectionPropertyChanged;
        _sortFilter.PropertyChanged += OnSortFilterPropertyChanged;

        // Load persisted user preferences
        if (_settingsService != null)
        {
            IsPreviewPaneVisible = _settingsService.Get<bool>("IsPreviewPaneVisible", false);
        }

        _ = _collection.LoadCollectionsAsync();
        _ = _collection.LoadPinnedFoldersAsync();
    }

    private void OnFileOpsRenameRequested(FileSystemEntry entry)
    {
        RenameRequested?.Invoke(entry);
    }

    private void OnNavigationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Forward property change notifications for properties that are delegated to _navigation
        if (e.PropertyName is nameof(NavigationViewModel.IsHomePage)
            or nameof(NavigationViewModel.CurrentPath)
            or nameof(NavigationViewModel.CanGoBack)
            or nameof(NavigationViewModel.CanGoForward)
            or nameof(NavigationViewModel.IsArchiveView)
            or nameof(NavigationViewModel.IsCollectionView)
            or nameof(NavigationViewModel.IsAiView)
            or nameof(NavigationViewModel.IsSearchMode)
            or nameof(NavigationViewModel.Breadcrumbs))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    private void OnAiPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiViewModel.AiViewMode)
            or nameof(AiViewModel.CurrentFaceClusterId)
            or nameof(AiViewModel.CurrentAiContextLabel)
            or nameof(AiViewModel.IsAiAnalysisEnabled)
            or nameof(AiViewModel.FaceClusters)
            or nameof(AiViewModel.AiCategories)
            or nameof(AiViewModel.TextTokens))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    private void OnArchivePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArchiveViewModel.IsCompressDialogVisible)
            or nameof(ArchiveViewModel.PendingCompressOptions)
            or nameof(ArchiveViewModel.ActiveTaskId))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    private void OnCollectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CollectionViewModel.Collections)
            or nameof(CollectionViewModel.PinnedFolders))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    private void OnSortFilterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SortFilterViewModel.ViewMode)
            or nameof(SortFilterViewModel.SortField)
            or nameof(SortFilterViewModel.SortAscending)
            or nameof(SortFilterViewModel.GroupField)
            or nameof(SortFilterViewModel.Groups)
            or nameof(SortFilterViewModel.HideSystemFiles))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    // ── Refresh from notification ──

    public async Task RefreshFromNotification()
    {
        if (_isRefreshingFromNotification) return;
        if (!_navigation.NeedsRefreshFromNotification(IsArchiveView, IsAiView, IsCollectionView)) return;
        _isRefreshingFromNotification = true;
        try
        {
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
        }
        finally { _isRefreshingFromNotification = false; }
    }

    // ── Navigation Commands ──

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Intercept archive sentinel paths
        if (ArchivePathHelper.IsArchivePath(path))
        {
            await NavigateToArchiveAsync(path);
            return;
        }

        // Intercept AI sentinel paths
        if (AiPathHelper.IsAiPath(path))
        {
            await HandleAiNavigationAsync(path);
            return;
        }

        // Reset AI runtime state (NavigationViewModel.NavigateToAsync handles flag reset)
        _ai.Reset();
        _navigation.IsSearchMode = false;

        if (string.IsNullOrEmpty(PendingSelectFileName))
        {
            ScrollBehaviorAfterLoad = ScrollMode.ResetToTop;
        }

        // Validate that the path exists
        if (path != _fileService.TrashDirectory && !Directory.Exists(path))
        {
            StatusText = $"路径不存在: {path}";
            return;
        }

        if (_navigation.CurrentPath == path && Entries.Count > 0) return;

        IsLoading = true;

        if (IsMetadataPanelVisible)
            CloseMetadata();

        _navigation.IsHomePage = false;

        var oldPath = _navigation.CurrentPath;
        await _navigation.NavigateToAsync(path);

        try
        {
            await LoadDirectoryContentsAsync();
        }
        catch (Exception ex) { StatusText = $"无法访问: {ex.Message}"; }
        finally { IsLoading = false; }

        // Update FSEvents watch
        _navigation.UnwatchCurrentDirectory(oldPath);
        _navigation.WatchCurrentDirectory();
    }

    [RelayCommand]
    public async Task NavigateUpAsync()
    {
        if (IsHomePage) return;

        if (IsAiView)
        {
            var aiParent = AiPathHelper.GetParentPath(_navigation.CurrentPath);
            if (string.IsNullOrEmpty(aiParent))
                GoHome();
            else
                await NavigateToAsync(aiParent);
            return;
        }

        if (IsArchiveView && _navigation.CurrentArchivePath != null)
        {
            if (!string.IsNullOrEmpty(_navigation.CurrentArchiveInternalPath))
            {
                var parent = Path.GetDirectoryName(_navigation.CurrentArchiveInternalPath.TrimEnd('/'))?.Replace('\\', '/') ?? "";
                await NavigateToAsync(ArchivePathHelper.Build(_navigation.CurrentArchivePath!, parent));
            }
            else
            {
                var parentDir = Path.GetDirectoryName(_navigation.CurrentArchivePath) ?? "/";
                await NavigateToAsync(parentDir);
            }
            return;
        }

        var parentPath = _fileService.GetParentPath(_navigation.CurrentPath);
        if (parentPath != _navigation.CurrentPath)
            await NavigateToAsync(parentPath);
    }

    [RelayCommand]
    public async Task NavigateBackAsync()
    {
        if (!_navigation.CanGoBack) return;
        ScrollBehaviorAfterLoad = ScrollMode.RestoreNavigation;
        await _navigation.NavigateBackAsync();
        await ReloadAfterHistoryNavigation();
    }

    [RelayCommand]
    public async Task NavigateForwardAsync()
    {
        if (!_navigation.CanGoForward) return;
        ScrollBehaviorAfterLoad = ScrollMode.RestoreNavigation;
        await _navigation.NavigateForwardAsync();
        await ReloadAfterHistoryNavigation();
    }

    private async Task ReloadAfterHistoryNavigation()
    {
        try
        {
            var path = _navigation.CurrentPath;
            if (ArchivePathHelper.IsArchivePath(path))
            {
                await NavigateToArchiveAsync(path);
            }
            else if (AiPathHelper.IsAiPath(path))
            {
                await HandleAiNavigationAsync(path);
            }
            else
            {
                // Returning to a normal directory — reset special view flags
                _navigation.IsArchiveView = false;
                _navigation.IsAiView = false;
                _navigation.IsCollectionView = false;
                _navigation.CurrentArchivePath = null;
                _navigation.CurrentArchiveInternalPath = "";
                _navigation.CurrentCollectionId = null;
                _navigation.CurrentCollectionName = null;
                _navigation.CurrentFaceClusterId = null;
                _navigation.CurrentAiContextLabel = null;
                _ai.Reset();
                _navigation.UpdateBreadcrumbs();
                IsLoading = true;
                try
                {
                    await LoadDirectoryContentsAsync();
                }
                catch (Exception ex) { StatusText = $"无法访问: {ex.Message}"; }
                finally { IsLoading = false; }
            }
        }
        finally
        {
            _navigation.EndHistoryNavigation();
        }
    }

    [RelayCommand]
    public void GoHome()
    {
        if (!string.IsNullOrEmpty(_navigation.CurrentPath))
            _navigation.UnwatchCurrentDirectory(_navigation.CurrentPath);

        _navigation.GoHome();
        _ai.Reset();
        Entries.Clear();
        StatusText = "";
        SelectedEntries.Clear();
    }

    [RelayCommand]
    public async Task OpenEntryAsync(FileSystemEntry entry)
    {
        // Virtual AI folder: navigate into AI detail
        if (entry.IsVirtual)
        {
            await NavigateToAsync(entry.FullPath);
            return;
        }

        // Archive view: directory -> navigate deeper
        if (IsArchiveView && entry.IsDirectory)
        {
            var (archPath, _) = ArchivePathHelper.Parse(entry.FullPath);
            var entryInternalPath = entry.FullPath[(entry.FullPath.IndexOf('#') + 1)..];
            await NavigateToAsync(ArchivePathHelper.Build(archPath, entryInternalPath));
            return;
        }

        // Archive view: file -> extract to temp and open
        if (IsArchiveView && !entry.IsDirectory)
        {
            if (_launcherService == null) return;
            try
            {
                var (archPath, entryKey) = ArchivePathHelper.Parse(entry.FullPath);
                var tempFile = await _archive.ExtractEntryToTempAsync(archPath, entryKey);
                if (tempFile != null)
                    await _launcherService.OpenFileAsync(tempFile);
            }
            catch (Exception ex)
            {
                StatusText = $"打开文件失败: {ex.Message}";
            }
            return;
        }

        // Normal view: double-click archive file -> enter archive browsing
        if (!entry.IsDirectory && _archiveService?.IsArchiveFile(entry.FullPath) == true)
        {
            await NavigateToAsync(ArchivePathHelper.Build(entry.FullPath, ""));
            return;
        }

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

    // ── Archive Navigation ──

    private async Task NavigateToArchiveAsync(string sentinelPath)
    {
        _navigation.IsHomePage = false;
        _navigation.IsCollectionView = false;
        _navigation.IsArchiveView = true;
        _navigation.IsAiView = false;
        _ai.Reset();
        _navigation.IsSearchMode = false;

        var (archivePath, internalPath) = ArchivePathHelper.Parse(sentinelPath);
        _navigation.CurrentArchivePath = archivePath;
        _navigation.CurrentArchiveInternalPath = internalPath;

        await _archive.NavigateToArchiveAsync(
            archivePath,
            internalPath,
            path => { _navigation.CurrentPath = path; },
            () => _navigation.UpdateBreadcrumbsForArchive(),
            entries => ApplyEntries(entries),
            msg => StatusText = msg,
            loading => IsLoading = loading
        );

        _navigation.UpdateHistoryForSentinelPath(sentinelPath);
        _navigation.WatchCurrentDirectory();
    }

    // ── AI Navigation ──

    private async Task HandleAiNavigationAsync(string sentinelPath)
    {
        _navigation.IsHomePage = false;
        _navigation.IsCollectionView = false;
        _navigation.IsArchiveView = false;
        _navigation.IsAiView = true;
        _navigation.IsSearchMode = false;

        await _ai.HandleAiNavigationAsync(
            sentinelPath,
            path => { _navigation.CurrentPath = path; },
            () => _navigation.UpdateBreadcrumbsForAi(
                AiPathHelper.GetModeName(_ai.AiViewMode),
                AiPathHelper.GetTopLevelPath(_ai.AiViewMode),
                _ai.CurrentAiContextLabel),
            entries => ApplyEntries(entries),
            msg => StatusText = msg,
            loading => IsLoading = loading
        );

        _navigation.UpdateHistoryForSentinelPath(sentinelPath);
    }

    // ── Refresh ──

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsHomePage) return;

        if (IsArchiveView && _navigation.CurrentArchivePath != null)
        {
            IsLoading = true;
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            try
            {
                await _archive.NavigateToArchiveAsync(
                    _navigation.CurrentArchivePath!,
                    _navigation.CurrentArchiveInternalPath,
                    path => { },
                    () => { },
                    e => ApplyEntries(e),
                    msg => StatusText = msg,
                    loading => { }
                );
            }
            catch (Exception ex) { StatusText = $"刷新失败: {ex.Message}"; }
            finally { IsLoading = false; }
            return;
        }

        if (IsAiView)
        {
            IsLoading = true;
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            try
            {
                var info = AiPathHelper.Parse(_navigation.CurrentPath);
                if (info.IsTopLevel)
                {
                    if (info.Mode == AiViewMode.TextSearch && _search.SearchQuery != null)
                        await _ai.SearchAiTagsAsync(_search.SearchQuery, entries => ApplyEntries(entries), msg => StatusText = msg);
                    else
                        await _ai.LoadAiTopLevelAsync(info.Mode, entries => ApplyEntries(entries), msg => StatusText = msg);
                }
                else if (info.IsFaceDetail)
                    await _ai.LoadFaceClusterEntriesAsync(info.FaceClusterId!.Value, entries => ApplyEntries(entries), msg => StatusText = msg);
                else
                    await _ai.LoadAiCategoryEntriesAsync(info.TagType!, info.TagValue!, entries => ApplyEntries(entries), msg => StatusText = msg);
            }
            catch (Exception ex) { StatusText = $"刷新失败: {ex.Message}"; }
            finally { IsLoading = false; }
            return;
        }

        if (IsCollectionView && _navigation.CurrentCollectionId != null)
        {
            await _collection.NavigateToCollectionAsync(
                _navigation.CurrentCollectionId.Value,
                v => _navigation.IsHomePage = v,
                v => _navigation.IsCollectionView = v,
                v => _navigation.IsAiView = v,
                v => _ai.CurrentFaceClusterId = v,
                v => _ai.CurrentAiContextLabel = v,
                v => _navigation.CurrentArchivePath = v,
                v => _navigation.CurrentArchiveInternalPath = v,
                v => _navigation.IsSearchMode = v,
                v => _navigation.CurrentCollectionId = v,
                v => _navigation.CurrentCollectionName = v,
                v => IsLoading = v,
                entries => ApplyEntries(entries),
                msg => StatusText = msg,
                () => _navigation.UpdateBreadcrumbs(),
                folders => { }
            );
            return;
        }

        IsLoading = true;
        ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
        try { await LoadDirectoryContentsAsync(forceRefresh: true); }
        finally { IsLoading = false; }
    }

    // ── Selection ──

    public void SelectEntry(FileSystemEntry entry, bool cmdKey = false, bool shiftKey = false)
    {
        if (shiftKey && _lastClickedEntry != null)
        {
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
            if (SelectedEntries.Contains(entry))
                SelectedEntries.Remove(entry);
            else
                SelectedEntries.Add(entry);
            _lastClickedEntry = entry;
        }
        else
        {
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

    // ── Context Menu ──

    public async Task ShowFileContextMenuAsync(FileSystemEntry entry, double x, double y)
    {
        ContextMenuEntry = entry;
        ContextMenuX = x;
        ContextMenuY = y;

        if (_contextMenuService != null)
        {
            if (entry.IsVirtual)
            {
                var actions = new List<ContextMenuAction>
                {
                    new()
                    {
                        Label = "打开",
                        IconSvg = Icons.Open,
                        Execute = () => OpenEntryCommand.ExecuteAsync(entry)
                    }
                };
                if (entry.VirtualFolderType == "face")
                {
                    actions.Add(new ContextMenuAction
                    {
                        Label = "重命名",
                        IconSvg = Icons.Rename,
                        Execute = () => { _fileOps.RaiseRequestRename(entry); return Task.CompletedTask; }
                    });
                }
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
            }
            else if (IsArchiveView)
            {
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(new[]
                {
                    new ContextMenuAction
                    {
                        Label = "打开",
                        IconSvg = Icons.Open,
                        Execute = () => OpenEntryCommand.ExecuteAsync(entry)
                    }
                });
            }
            else if (_navigation.IsInTrash)
            {
                var actions = await _contextMenuService.GetTrashFileContextMenuActionsAsync(entry);
                actions = WireUpTrashFileContextMenuActions(actions, entry);
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
            }
            else
            {
                var actions = await _contextMenuService.GetFileContextMenuActionsAsync(entry);
                actions = await WireUpContextMenuActionsAsync(actions, entry);
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
            }
        }

        if (_nativeContextMenuService != null)
        {
            await Task.Delay(50);
            var actions = ContextMenuActions;
            var menuX = x;
            var menuY = y;
            MainThread.BeginInvokeOnMainThread(() =>
                _nativeContextMenuService.ShowContextMenu(actions, menuX, menuY));
            return;
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
            if (IsArchiveView)
            {
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(new[]
                {
                    new ContextMenuAction
                    {
                        Label = "刷新",
                        IconSvg = Icons.Refresh,
                        Execute = () => _navigation.CurrentArchivePath != null
                            ? NavigateToAsync(ArchivePathHelper.Build(_navigation.CurrentArchivePath!, _navigation.CurrentArchiveInternalPath))
                            : Task.CompletedTask
                    }
                });
            }
            else if (IsCollectionView || IsAiView)
            {
                return;
            }
            else if (_navigation.IsInTrash)
            {
                var actions = await _contextMenuService.GetTrashBackgroundContextMenuActionsAsync();
                actions = WireUpTrashBackgroundContextMenuActions(actions);
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
            }
            else
            {
                var actions = await _contextMenuService.GetBackgroundContextMenuActionsAsync(_navigation.CurrentPath);
                actions = WireUpBackgroundContextMenuActions(actions);
                ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
            }
        }

        if (_nativeContextMenuService != null)
        {
            await Task.Delay(50);
            var actions = ContextMenuActions;
            var menuX = x;
            var menuY = y;
            MainThread.BeginInvokeOnMainThread(() =>
                _nativeContextMenuService.ShowContextMenu(actions, menuX, menuY));
            return;
        }

        IsContextMenuVisible = true;
    }

    private async Task<List<ContextMenuAction>> WireUpContextMenuActionsAsync(IReadOnlyList<ContextMenuAction> actions, FileSystemEntry entry)
    {
        var result = new List<ContextMenuAction>();
        foreach (var action in actions)
        {
            if (action.IsSeparator)
            {
                result.Add(action);
                continue;
            }
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
                    IsQuickAction = action.IsQuickAction,
                    Execute = async () => { _fileOps.CopySelectedCommand.Execute(SelectedEntries.ToList()); await Task.CompletedTask; }
                });
            }
            else if (action.Label == "剪切")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    IsQuickAction = action.IsQuickAction,
                    Execute = async () => { _fileOps.CutSelectedCommand.Execute(SelectedEntries.ToList()); await Task.CompletedTask; }
                });
            }
            else if (action.Label == "粘贴")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    IsQuickAction = action.IsQuickAction,
                    IsEnabled = _clipboardService?.HasClipboardFiles ?? false,
                    Execute = () => PasteCommand.ExecuteAsync(null)
                });
            }
            else if (action.Label == "删除")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    ShortcutText = action.ShortcutText,
                    IsQuickAction = action.IsQuickAction,
                    Execute = () =>
                    {
                        SelectedEntries.Clear();
                        SelectedEntries.Add(entry);
                        ShowDeleteConfirmDialogCommand.Execute(null);
                        return Task.CompletedTask;
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
                    IsQuickAction = action.IsQuickAction,
                    Execute = () =>
                    {
                        _fileOps.RaiseRequestRename(entry);
                        return Task.CompletedTask;
                    }
                });
            }
            else if (action.Label == "解压到此处")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    Execute = () => { ExtractHere(entry); return Task.CompletedTask; }
                });
            }
            else if (action.Label == "压缩")
            {
                result.Add(new ContextMenuAction
                {
                    Label = action.Label,
                    IconSvg = action.IconSvg,
                    Execute = () => { ShowCompressDialog(); return Task.CompletedTask; }
                });
            }
            else if (action.Label == "Pin到收藏" || action.Label == "取消Pin")
            {
                var isPinned = await _fileOps.IsFolderPinnedAsync(entry.FullPath);
                result.Add(new ContextMenuAction
                {
                    Label = isPinned ? "取消Pin" : "Pin到收藏",
                    IconSvg = Icons.Pin,
                    Execute = isPinned
                        ? () => _fileOps.UnpinFolderAsync(entry.FullPath)
                        : () => _fileOps.PinFolderAsync(entry.FullPath, entry.Name)
                });
            }
            else
            {
                result.Add(action);
            }
        }

        // Insert "添加到收藏夹" submenu
        if (_collection.Collections.Count > 0 && !entry.IsDirectory)
        {
            var insertIdx = result.FindLastIndex(a => a.IsSeparator);
            if (insertIdx < 0) insertIdx = result.Count;
            result.Insert(insertIdx, new ContextMenuAction
            {
                Label = "添加到收藏夹",
                IconSvg = Icons.CollectionAdd,
                SubItems = _collection.Collections.Select(c => new ContextMenuAction
                {
                    Label = c.Name,
                    IconSvg = Icons.Folder,
                    Execute = () => AddToCollectionAsync(c.Id, entry.FullPath)
                }).ToList()
            });
        }

        // In collection view, add "从收藏夹中移除"
        if (IsCollectionView && _navigation.CurrentCollectionId != null)
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
                    IsQuickAction = action.IsQuickAction,
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
                        ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
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
                        ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
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

    // ── Metadata ──

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

    // ── File Operations ──

    [RelayCommand]
    public void CopySelected()
    {
        _fileOps.CopySelectedCommand.Execute(SelectedEntries.ToList());
        StatusText = $"已拷贝 {SelectedEntries.Count} 项";
    }

    [RelayCommand]
    public void CutSelected()
    {
        _fileOps.CutSelectedCommand.Execute(SelectedEntries.ToList());
        StatusText = $"已剪切 {SelectedEntries.Count} 项";
    }

    [RelayCommand]
    public async Task PasteAsync()
    {
        try
        {
            await _fileOps.PasteAsync(_navigation.CurrentPath, IsCollectionView, _navigation.CurrentCollectionId);
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
            StatusText = "已粘贴";
        }
        catch (Exception ex) { StatusText = $"粘贴失败: {ex.Message}"; }
    }

    // Delete confirmation dialog state
    [ObservableProperty]
    private bool _isDeleteConfirmDialogVisible;

    [ObservableProperty]
    private bool _isCollectionDeleteConfirmDialogVisible;

    public int DeleteConfirmItemCount => SelectedEntries.Count;
    public string DeleteConfirmFirstItemName => SelectedEntries.FirstOrDefault()?.Name ?? "";
    public int? PendingDeleteCollectionId { get; private set; }
    public string PendingDeleteCollectionName { get; private set; } = "";

    [RelayCommand]
    public void ShowDeleteConfirmDialog()
    {
        if (SelectedEntries.Count == 0) return;
        IsContextMenuVisible = false; // Close context menu if open
        IsDeleteConfirmDialogVisible = true;
    }

    [RelayCommand]
    public async Task ConfirmDeleteSelectedAsync()
    {
        IsDeleteConfirmDialogVisible = false;
        await ExecuteDeleteSelectedAsync();
    }

    [RelayCommand]
    public void CancelDeleteConfirmDialog()
    {
        IsDeleteConfirmDialogVisible = false;
    }

    public void ShowCollectionDeleteConfirmDialog(int collectionId, string collectionName)
    {
        IsContextMenuVisible = false; // Close context menu if open
        PendingDeleteCollectionId = collectionId;
        PendingDeleteCollectionName = collectionName;
        IsCollectionDeleteConfirmDialogVisible = true;
    }

    [RelayCommand]
    public async Task ConfirmDeleteCollectionAsync()
    {
        IsCollectionDeleteConfirmDialogVisible = false;
        if (PendingDeleteCollectionId.HasValue)
        {
            var collectionId = PendingDeleteCollectionId.Value;
            PendingDeleteCollectionId = null;
            PendingDeleteCollectionName = "";
            await DeleteCollectionAsync(collectionId);
        }
    }

    [RelayCommand]
    public void CancelCollectionDeleteConfirmDialog()
    {
        IsCollectionDeleteConfirmDialogVisible = false;
        PendingDeleteCollectionId = null;
        PendingDeleteCollectionName = "";
    }

    private async Task ExecuteDeleteSelectedAsync()
    {
        if (SelectedEntries.Count == 0) return;
        try
        {
            await _fileOps.DeleteSelectedAsync(
                SelectedEntries.ToList(),
                _navigation.CurrentPath,
                IsCollectionView,
                _navigation.CurrentCollectionId,
                msg => StatusText = msg
            );

            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            if (IsCollectionView && _navigation.CurrentCollectionId != null)
                await _collection.NavigateToCollectionAsync(
                    _navigation.CurrentCollectionId.Value,
                    v => _navigation.IsHomePage = v,
                    v => _navigation.IsCollectionView = v,
                    v => _navigation.IsAiView = v,
                    v => _ai.CurrentFaceClusterId = v,
                    v => _ai.CurrentAiContextLabel = v,
                    v => _navigation.CurrentArchivePath = v,
                    v => _navigation.CurrentArchiveInternalPath = v,
                    v => _navigation.IsSearchMode = v,
                    v => _navigation.CurrentCollectionId = v,
                    v => _navigation.CurrentCollectionName = v,
                    v => IsLoading = v,
                    entries => ApplyEntries(entries),
                    msg => StatusText = msg,
                    () => _navigation.UpdateBreadcrumbs(),
                    folders => { }
                );
            else
                await LoadDirectoryContentsAsync(forceRefresh: true);

            _directoryChangeNotifier?.NotifyChanged([_navigation.CurrentPath], this);
        }
        catch (Exception ex) { StatusText = $"删除失败: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task CreateNewFolderAsync()
    {
        try
        {
            var rawEntries = Entries.ToList();
            await _fileOps.CreateNewFolderAsync(
                _navigation.CurrentPath,
                rawEntries,
                msg => StatusText = msg,
                async () =>
                {
                    ScrollBehaviorAfterLoad = ScrollMode.ScrollToSelected;
                    await LoadDirectoryContentsAsync(forceRefresh: true);
                    // Auto-select and rename
                    var newEntry = Entries.FirstOrDefault(e => e.Name.EndsWith("未命名文件夹"));
                    if (newEntry != null)
                    {
                        SelectedEntries.Clear();
                        SelectedEntries.Add(newEntry);
                        _fileOps.RaiseRequestRename(newEntry);
                    }
                }
            );
        }
        catch (Exception ex) { StatusText = $"创建文件夹失败: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task CreateNewFileAsync(string? extension = null)
    {
        try
        {
            var rawEntries = Entries.ToList();
            await _fileOps.CreateNewFileAsync(
                _navigation.CurrentPath,
                rawEntries,
                extension,
                msg => StatusText = msg,
                async () =>
                {
                    ScrollBehaviorAfterLoad = ScrollMode.ScrollToSelected;
                    await LoadDirectoryContentsAsync(forceRefresh: true);
                    // Auto-select
                    var newEntry = Entries.FirstOrDefault(e => e.Name.StartsWith("未命名"));
                    if (newEntry != null)
                    {
                        SelectedEntries.Clear();
                        SelectedEntries.Add(newEntry);
                    }
                }
            );
        }
        catch (Exception ex) { StatusText = $"创建文件失败: {ex.Message}"; }
    }

    public async Task MoveEntryAsync(FileSystemEntry source, FileSystemEntry targetFolder)
    {
        try
        {
            await _fileOps.MoveEntryAsync(source, targetFolder, msg => StatusText = msg);
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
        }
        catch (Exception ex) { StatusText = $"移动失败: {ex.Message}"; }
    }

    public async Task MoveEntriesAsync(IReadOnlyList<FileSystemEntry> entries, FileSystemEntry targetFolder)
    {
        try
        {
            await _fileOps.MoveEntriesAsync(
                entries,
                targetFolder,
                msg => StatusText = msg,
                id => { } // active task id
            );
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
        }
        catch (Exception ex) { StatusText = $"移动失败: {ex.Message}"; }
    }

    public event Action<FileSystemEntry>? RenameRequested;

    public void RequestRename(FileSystemEntry entry)
    {
        RenameRequested?.Invoke(entry);
    }

    public async Task RenameEntryAsync(FileSystemEntry entry, string newName)
    {
        // Virtual face cluster rename
        if (entry.IsVirtual && entry.VirtualFolderType == "face")
        {
            var clusterId = int.Parse(entry.VirtualFolderKey!);
            await _ai.RenameFaceClusterAsync(clusterId, newName);

            // Update virtual entry in Entries
            var path = $"{VirtualPath.AiFacePrefix}{clusterId}";
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].FullPath == path)
                {
                    var old = Entries[i];
                    Entries[i] = new FileSystemEntry
                    {
                        FullPath = old.FullPath,
                        Name = newName,
                        IsDirectory = old.IsDirectory,
                        IconKey = old.IconKey,
                        ThumbnailUrl = old.ThumbnailUrl,
                        Size = old.Size,
                        LastModified = old.LastModified,
                        Created = old.Created,
                        IsVirtual = old.IsVirtual,
                        VirtualFolderType = old.VirtualFolderType,
                        VirtualFolderKey = old.VirtualFolderKey,
                        VirtualItemCount = old.VirtualItemCount,
                    };
                    SelectedEntries.Clear();
                    SelectedEntries.Add(Entries[i]);
                    break;
                }
            }
            OnPropertyChanged(nameof(Entries));
            return;
        }

        // Other virtual entries cannot be renamed
        if (entry.IsVirtual)
            return;

        try
        {
            await _fileOps.RenameEntryAsync(entry, newName, IsAiView, msg => StatusText = msg);

            // Update PIN folder paths
            var oldPath = entry.FullPath;
            var dir = Path.GetDirectoryName(oldPath) ?? "";
            var newPath = Path.Combine(dir, newName);

            await _fileOps.PinFolderAsync(newPath, newName); // This will update if needed

            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);

            // Re-select the renamed entry
            var renamed = Entries.FirstOrDefault(e => e.Name == newName);
            if (renamed != null)
            {
                SelectedEntries.Clear();
                SelectedEntries.Add(renamed);
            }
            _directoryChangeNotifier?.NotifyChanged([_navigation.CurrentPath], this);
        }
        catch (Exception ex) { StatusText = $"重命名失败: {ex.Message}"; }
    }

    // ── Archive Operations ──

    public void ExtractHere(FileSystemEntry entry)
    {
        _archive.ExtractHereAsync(
            entry,
            _navigation.CurrentPath,
            msg => StatusText = msg,
            id => { }, // active task id
            async () => await RefreshAsync()
        );
    }

    public void ShowCompressDialog()
    {
        _archive.ShowCompressDialog(
            SelectedEntries.ToList(),
            ContextMenuEntry,
            _navigation.CurrentPath,
            IsCollectionView,
            IsArchiveView,
            _navigation.CurrentCollectionId
        );
    }

    public void ConfirmCompress(CompressOptions options)
    {
        _archive.ConfirmCompress(
            options,
            _collection.CollectionService,
            async () => await RefreshAsync(),
            id => { },
            msg => StatusText = msg
        );
    }

    public void MinimizeActiveTask() => _archive.MinimizeActiveTask();

    public void CancelCompressDialog() => _archive.CancelCompressDialog();

    // ── Search ──

    [RelayCommand]
    public async Task SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) { ExitSearch(); return; }

        _search.EnterSearchMode(IsHomePage);
        _navigation.IsHomePage = false;
        _navigation.IsSearchMode = true;
        _navigation.SearchQuery = query;
        IsLoading = true;

        var searchPath = string.IsNullOrEmpty(_navigation.CurrentPath) ? HomeDirectory : _navigation.CurrentPath;

        try
        {
            await _search.SearchAsync(
                query,
                HomeDirectory,
                _navigation.CurrentPath,
                entries =>
                {
                    Entries = entries;
                    SelectedEntries.Clear();
                },
                msg => StatusText = msg
            );
        }
        finally { IsLoading = false; }
    }

    public void ExitSearch()
    {
        var wasHomePage = _search.WasHomePageBeforeSearch;
        _search.ExitSearchMode(true);
        _navigation.IsSearchMode = false;
        _navigation.SearchQuery = string.Empty;

        if (wasHomePage)
        {
            _navigation.IsHomePage = true;
        }
    }

    // ── Collections ──

    public async Task AddToCollectionAsync(int collectionId, string filePath)
    {
        await _collection.AddToCollectionAsync(collectionId, filePath, msg => StatusText = msg);

        if (IsCollectionView && _navigation.CurrentCollectionId == collectionId)
            await _collection.NavigateToCollectionAsync(
                _navigation.CurrentCollectionId.Value,
                v => _navigation.IsHomePage = v,
                v => _navigation.IsCollectionView = v,
                v => _navigation.IsAiView = v,
                v => _ai.CurrentFaceClusterId = v,
                v => _ai.CurrentAiContextLabel = v,
                v => _navigation.CurrentArchivePath = v,
                v => _navigation.CurrentArchiveInternalPath = v,
                v => _navigation.IsSearchMode = v,
                v => _navigation.CurrentCollectionId = v,
                v => _navigation.CurrentCollectionName = v,
                v => IsLoading = v,
                entries => ApplyEntries(entries),
                msg => StatusText = msg,
                () => _navigation.UpdateBreadcrumbs(),
                folders => { }
            );
    }

    public async Task RemoveFromCollectionAsync(string filePath)
    {
        if (!IsCollectionView || _navigation.CurrentCollectionId == null) return;
        await _collection.RemoveFromCollectionAsync(
            _navigation.CurrentCollectionId.Value,
            filePath,
            async () =>
            {
                await _collection.NavigateToCollectionAsync(
                    _navigation.CurrentCollectionId!.Value,
                    v => _navigation.IsHomePage = v,
                    v => _navigation.IsCollectionView = v,
                    v => _navigation.IsAiView = v,
                    v => _ai.CurrentFaceClusterId = v,
                    v => _ai.CurrentAiContextLabel = v,
                    v => _navigation.CurrentArchivePath = v,
                    v => _navigation.CurrentArchiveInternalPath = v,
                    v => _navigation.IsSearchMode = v,
                    v => _navigation.CurrentCollectionId = v,
                    v => _navigation.CurrentCollectionName = v,
                    v => IsLoading = v,
                    entries => ApplyEntries(entries),
                    msg => StatusText = msg,
                    () => _navigation.UpdateBreadcrumbs(),
                    folders => { }
                );
            }
        );
    }

    public async Task CreateCollectionAsync(string name)
    {
        await _collection.CreateCollectionAsync(name, msg => StatusText = msg);
    }

    public async Task NavigateToCollectionAsync(int collectionId)
    {
        await _collection.NavigateToCollectionAsync(
            collectionId,
            v => _navigation.IsHomePage = v,
            v => _navigation.IsCollectionView = v,
            v => _navigation.IsAiView = v,
            v => _ai.CurrentFaceClusterId = v,
            v => _ai.CurrentAiContextLabel = v,
            v => _navigation.CurrentArchivePath = v,
            v => _navigation.CurrentArchiveInternalPath = v,
            v => _navigation.IsSearchMode = v,
            v => _navigation.CurrentCollectionId = v,
            v => _navigation.CurrentCollectionName = v,
            v => IsLoading = v,
            entries => ApplyEntries(entries),
            msg => StatusText = msg,
            () => _navigation.UpdateBreadcrumbs(),
            folders => { }
        );
    }

    public async Task RenameCollectionAsync(int id, string newName)
    {
        await _collection.RenameCollectionAsync(
            id,
            newName,
            IsCollectionView,
            _navigation.CurrentCollectionId,
            name => { _navigation.CurrentCollectionName = name; },
            msg => StatusText = msg
        );
    }

    public async Task DeleteCollectionAsync(int id)
    {
        await _collection.DeleteCollectionAsync(
            id,
            IsCollectionView,
            _navigation.CurrentCollectionId,
            () => GoHome()
        );
    }

    // ── AI View Commands ──

    [RelayCommand]
    public async Task NavigateToAiViewAsync(AiViewMode mode)
    {
        await NavigateToAsync(AiPathHelper.GetTopLevelPath(mode));
    }

    [RelayCommand]
    public async Task NavigateToFaceClusterAsync(int clusterId)
    {
        await NavigateToAsync($"{VirtualPath.AiFacePrefix}{clusterId}");
    }

    public async Task NavigateToAiCategoryAsync(string tagType, string tagValue)
    {
        await NavigateToAsync($"{VirtualPath.AiPrefix}{tagType}:{tagValue}");
    }

    public async Task RenameFaceClusterAsync(int clusterId, string name)
    {
        await _ai.RenameFaceClusterAsync(clusterId, name);
    }

    [RelayCommand]
    public async Task SearchAiTagsAsync(string query)
    {
        await _ai.SearchAiTagsAsync(query, entries => ApplyEntries(entries), msg => StatusText = msg);
    }

    public void ClearTextSearchQuery() => _ai.ClearTextSearchQuery();

    // ── Sort/Filter ──

    [RelayCommand]
    public void ToggleViewMode()
    {
        _sortFilter.ViewMode = _sortFilter.ViewMode == ViewMode.Grid ? ViewMode.List : ViewMode.Grid;
        OnPropertyChanged(nameof(ViewMode));
    }

    public void SetSort(SortField field, bool? ascending = null)
    {
        _sortFilter.SetSort(field, ascending);
        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(SortAscending));
    }

    // ── Preview Pane ──

    [RelayCommand]
    public void TogglePreviewPane()
    {
        IsPreviewPaneVisible = !IsPreviewPaneVisible;
        _settingsService?.Set("IsPreviewPaneVisible", IsPreviewPaneVisible);
    }

    // ── Ratings ──

    public int GetRating(string filePath) => _collection.GetRating(filePath);

    public async Task SetRatingAsync(string filePath, int rating)
    {
        await _collection.SetRatingAsync(filePath, rating, () => OnPropertyChanged(nameof(Entries)));
    }

    // ── Pinned Folders ──

    public async Task<bool> IsFolderPinnedAsync(string path)
    {
        return await _fileOps.IsFolderPinnedAsync(path);
    }

    public async Task PinFolderAsync(string path, string displayName)
    {
        await _fileOps.PinFolderAsync(path, displayName);
        await _collection.LoadPinnedFoldersAsync();
    }

    public async Task UnpinFolderAsync(string path)
    {
        await _fileOps.UnpinFolderAsync(path);
        await _collection.LoadPinnedFoldersAsync();
    }

    public bool IsCollectionNameDuplicate(string name, int? excludeId = null)
    {
        return _collection.IsCollectionNameDuplicate(name, excludeId);
    }

    // ── Load Directory Contents ──

    private async Task LoadDirectoryContentsAsync(bool forceRefresh = false)
    {
        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            // Skip index for /Applications paths because they need to merge /System/Applications counterpart
            // This ensures Utilities and other folders show both user and system applications
            var shouldUseIndex = !IsApplicationsPath(_navigation.CurrentPath);
            if (!forceRefresh && shouldUseIndex && _fileIndex != null && _indexConfig.ShouldIndex(_navigation.CurrentPath))
            {
                var isFresh = await _fileIndex.IsDirectoryFreshAsync(_navigation.CurrentPath, _indexConfig.FreshnessThreshold);
                if (isFresh)
                {
                    entries = await _fileIndex.GetDirectoryContentsAsync(_navigation.CurrentPath);
                    if (entries.Count > 0)
                    {
                        ApplyEntries(entries);
                        // Resolve app icons even when loading from index (IconUrl is not persisted in index)
                        _ = ResolveIconsInBackgroundAsync(entries);
                        return;
                    }
                }
            }
        }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to load directory contents from index for {Path}", _navigation.CurrentPath); }

        entries = await _fileService.GetDirectoryContentsAsync(_navigation.CurrentPath);

        try
        {
            if (_fileIndexWriter != null && _indexConfig.ShouldIndex(_navigation.CurrentPath) && entries.Count > 0)
                await _fileIndexWriter.UpdateDirectoryAsync(_navigation.CurrentPath, entries);
        }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to update index for directory {Path}", _navigation.CurrentPath); }

        ApplyEntries(entries);

        // Resolve app icons lazily in background (don't block the list display)
        _ = ResolveIconsInBackgroundAsync(entries);
        _ = ResolveThumbnailsInBackgroundAsync(entries);
        _ = TriggerImageAnalysisAsync(entries);

        // Batch load ratings for current directory
        _ = _collection.GetRating(_navigation.CurrentPath); // Just to initialize
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
        catch (Exception ex) { _logger?.LogError(ex, "Failed to resolve app icons"); }
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
                        entry.ThumbnailUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
                    }
                }
                OnPropertyChanged(nameof(Entries));
            }
        }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to resolve thumbnails"); }
    }

    private async Task TriggerImageAnalysisAsync(IReadOnlyList<FileSystemEntry> entries)
    {
        await _ai.TriggerImageAnalysisAsync(
            entries,
            _navigation.CurrentPath,
            id => { } // active task id
        );
    }

    private void ApplyEntries(IReadOnlyList<FileSystemEntry> entries)
    {
        _sortFilter.SetRawEntries(entries);
        _sortFilter.ApplySortAndGroup(
            sortedEntries => Entries = sortedEntries,
            msg => StatusText = msg
        );
        SelectedEntries.Clear();
        _lastClickedEntry = null;

        // Restore selected entry when navigating back/forward
        if (IsRestoringNavigation)
        {
            var savedName = _navigation.GetSavedSelectedEntryName(_navigation.CurrentPath);
            if (savedName != null)
            {
                var entry = Entries.FirstOrDefault(e => e.Name == savedName);
                if (entry != null)
                {
                    SelectedEntries.Add(entry);
                    _lastClickedEntry = entry;
                }
            }
        }

        // Auto-select a specific file (e.g. from breadcrumb search suggestion)
        if (PendingSelectFileName != null)
        {
            var target = Entries.FirstOrDefault(e => e.Name == PendingSelectFileName);
            if (target != null)
            {
                SelectedEntries.Clear();
                SelectedEntries.Add(target);
                _lastClickedEntry = target;
            }
            PendingSelectFileName = null;
        }

        // Reset to PreservePosition so background icon/thumbnail updates don't affect scroll
        if (ScrollBehaviorAfterLoad != ScrollMode.ScrollToSelected)
        {
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
        }
    }

    /// <summary>
    /// Check if a path is under /Applications and needs to merge /System/Applications counterpart.
    /// These paths should not use the index because the merge logic is in GetDirectoryContentsAsync.
    /// </summary>
    private static bool IsApplicationsPath(string path)
    {
        return path == "/Applications" || path.StartsWith("/Applications/", StringComparison.OrdinalIgnoreCase);
    }
}

// Enums - kept here for backward compatibility
public enum ViewMode { Grid, List }
public enum SortField { Name, Modified, Size, Type }
public enum GroupField { None, Type, Modified, Size }

public class FileGroup
{
    public string Name { get; init; } = "";
    public IReadOnlyList<FileSystemEntry> Entries { get; init; } = [];
}