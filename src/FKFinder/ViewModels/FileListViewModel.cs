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
    private readonly IArchiveService? _archiveService;
    private readonly IBackgroundTaskManager? _taskManager;
    private readonly INativeContextMenuService? _nativeContextMenuService;
    private readonly IPinnedFolderService? _pinnedFolderService;
    private readonly IImageAnalysisService? _imageAnalysisService;
    private readonly IAiTagService? _aiTagService;
    private readonly IDragDropBridge? _dragDropBridge;
    private CancellationTokenSource? _searchCts;

    private IReadOnlyList<FileSystemEntry> _rawEntries = [];

    // Navigation history
    private readonly List<string> _historyStack = [];
    private int _historyIndex = -1;
    private bool _isNavigatingHistory;
    private readonly Dictionary<string, string?> _pathSelectedEntries = new();

    /// <summary>被剪切文件的完整路径集合，用于 UI 半透明显示</summary>
    public HashSet<string> CutPaths { get; } = [];

    // Scroll behavior after Entries change
    public enum ScrollMode { ResetToTop, RestoreNavigation, ScrollToSelected, PreservePosition }
    public ScrollMode ScrollBehaviorAfterLoad { get; set; } = ScrollMode.ResetToTop;

    /// <summary>
    /// When set, ApplyEntries will auto-select and scroll to the file with this name after loading.
    /// </summary>
    public string? PendingSelectFileName { get; set; }

    // Keep backward-compat alias
    public bool IsRestoringNavigation => ScrollBehaviorAfterLoad == ScrollMode.RestoreNavigation;

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
        ".DS_Store", "Thumbs.db", "desktop.ini", ".Spotlight-V100", ".Trashes", ".fseventsd", ".localized"
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

    // Pinned folders
    [ObservableProperty]
    private ObservableCollection<PinnedFolder> _pinnedFolders = [];

    // Last clicked entry for shift-range selection
    private FileSystemEntry? _lastClickedEntry;

    // Archive browsing
    [ObservableProperty]
    private bool _isArchiveView;

    [ObservableProperty]
    private string? _currentArchivePath;

    [ObservableProperty]
    private string _currentArchiveInternalPath = "";

    [ObservableProperty]
    private bool _isCompressDialogVisible;

    [ObservableProperty]
    private CompressOptions? _pendingCompressOptions;

    [ObservableProperty]
    private string? _activeTaskId;

    // AI view
    [ObservableProperty]
    private bool _isAiView;

    [ObservableProperty]
    private AiViewMode _aiViewMode;

    [ObservableProperty]
    private int? _currentFaceClusterId;

    [ObservableProperty]
    private string? _currentAiContextLabel;

    [ObservableProperty]
    private bool _isAiAnalysisEnabled = true;

    public ObservableCollection<FaceCluster> FaceClusters { get; } = [];
    public ObservableCollection<AiCategory> AiCategories { get; } = [];
    public ObservableCollection<AiCategory> TextTokens { get; } = [];

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
        IFrequentFolderService? frequentFolderService = null,
        IArchiveService? archiveService = null,
        IBackgroundTaskManager? taskManager = null,
        INativeContextMenuService? nativeContextMenuService = null,
        IPinnedFolderService? pinnedFolderService = null,
        IImageAnalysisService? imageAnalysisService = null,
        IAiTagService? aiTagService = null,
        IDragDropBridge? dragDropBridge = null)
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
        _archiveService = archiveService;
        _taskManager = taskManager;
        _nativeContextMenuService = nativeContextMenuService;
        _pinnedFolderService = pinnedFolderService;
        _imageAnalysisService = imageAnalysisService;
        _aiTagService = aiTagService;
        _dragDropBridge = dragDropBridge;

        if (_dragDropBridge != null)
            _dragDropBridge.ExternalDropReceived += HandleExternalDrop;

        // Load persisted user preferences
        if (_settingsService != null)
        {
            ViewMode = _settingsService.Get<ViewMode>("ViewMode", ViewMode.List);
            SortField = _settingsService.Get<SortField>("SortField", SortField.Name);
            SortAscending = _settingsService.Get<bool>("SortAscending", true);
            GroupField = _settingsService.Get<GroupField>("GroupField", GroupField.None);
            IsPreviewPaneVisible = _settingsService.Get<bool>("IsPreviewPaneVisible", false);
            HideSystemFiles = _settingsService.Get<bool>("HideSystemFiles", true);
            IsAiAnalysisEnabled = _settingsService.Get<bool>("IsAiAnalysisEnabled", true);
        }

        _ = LoadCollectionsAsync();
        _ = LoadPinnedFoldersAsync();
    }

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (string.IsNullOrEmpty(PendingSelectFileName))
        {
            ScrollBehaviorAfterLoad = ScrollMode.ResetToTop;
        }

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

        // Leaving AI view when navigating to a filesystem path
        IsAiView = false;
        CurrentFaceClusterId = null;
        CurrentAiContextLabel = null;
        TextTokens.Clear();

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
        IsArchiveView = false;
        CurrentArchivePath = null;
        CurrentArchiveInternalPath = "";
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

        if (IsAiView)
        {
            var aiParent = AiPathHelper.GetParentPath(CurrentPath);
            if (string.IsNullOrEmpty(aiParent))
                GoHome();
            else
                await NavigateToAsync(aiParent);
            return;
        }

        if (IsArchiveView && CurrentArchivePath != null)
        {
            if (!string.IsNullOrEmpty(CurrentArchiveInternalPath))
            {
                var parent = Path.GetDirectoryName(CurrentArchiveInternalPath.TrimEnd('/'))?.Replace('\\', '/') ?? "";
                await NavigateToAsync(ArchivePathHelper.Build(CurrentArchivePath, parent));
            }
            else
            {
                var parentDir = Path.GetDirectoryName(CurrentArchivePath) ?? "/";
                await NavigateToAsync(parentDir);
            }
            return;
        }

        var parentPath = _fileService.GetParentPath(CurrentPath);
        if (parentPath != CurrentPath)
            await NavigateToAsync(parentPath);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsHomePage) return;

        if (IsArchiveView && CurrentArchivePath != null)
        {
            IsLoading = true;
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            try
            {
                var entries = await _archiveService!.GetArchiveContentsAsync(CurrentArchivePath, CurrentArchiveInternalPath);
                ApplyEntries(entries);
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
                var info = AiPathHelper.Parse(CurrentPath);
                if (info.IsTopLevel)
                    await LoadAiTopLevelAsync(info.Mode);
                else if (info.IsFaceDetail)
                    await LoadFaceClusterEntriesAsync(info.FaceClusterId!.Value);
                else
                    await LoadAiCategoryEntriesAsync(info.TagType!, info.TagValue!);
            }
            catch (Exception ex) { StatusText = $"刷新失败: {ex.Message}"; }
            finally { IsLoading = false; }
            return;
        }

        if (IsCollectionView && CurrentCollectionId != null)
        {
            await NavigateToCollectionAsync(CurrentCollectionId.Value);
            return;
        }

        IsLoading = true;
        ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
        try { await LoadDirectoryContentsAsync(forceRefresh: true); }
        finally { IsLoading = false; }
    }

    // ── Archive Browsing ──

    private async Task NavigateToArchiveAsync(string sentinelPath)
    {
        if (_archiveService == null) return;
        var (archivePath, internalPath) = ArchivePathHelper.Parse(sentinelPath);

        if (IsMetadataPanelVisible)
            CloseMetadata();

        IsHomePage = false;
        IsCollectionView = false;
        IsAiView = false;
        CurrentFaceClusterId = null;
        CurrentAiContextLabel = null;
        TextTokens.Clear();
        IsArchiveView = true;
        CurrentArchivePath = archivePath;
        CurrentArchiveInternalPath = internalPath;
        IsSearchMode = false;
        IsLoading = true;

        try
        {
            if (!string.IsNullOrEmpty(CurrentPath) && SelectedEntries.Count > 0)
                _pathSelectedEntries[CurrentPath] = SelectedEntries.First().Name;

            if (!_isNavigatingHistory)
            {
                if (_historyIndex < _historyStack.Count - 1)
                    _historyStack.RemoveRange(_historyIndex + 1, _historyStack.Count - _historyIndex - 1);
                _historyStack.Add(sentinelPath);
                _historyIndex = _historyStack.Count - 1;
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoForward));
            }

            CurrentPath = sentinelPath;
            UpdateBreadcrumbs();

            var entries = await _archiveService.GetArchiveContentsAsync(archivePath, internalPath);
            ApplyEntries(entries);
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开归档: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Archive Extract / Compress ──

    public void ExtractHere(FileSystemEntry entry)
    {
        if (_archiveService == null || _taskManager == null) return;
        var destDir = Path.GetDirectoryName(entry.FullPath) ?? CurrentPath;

        var taskInfo = _taskManager.AddTask("正在解压...", async () =>
        {
            await RefreshAsync();
        });
        ActiveTaskId = taskInfo.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<ArchiveProgress>(p =>
                {
                    _taskManager.UpdateProgress(taskInfo.Id, p.Percentage, p.CurrentFile);
                });
                await _archiveService.ExtractAsync(entry.FullPath, destDir, progress, taskInfo.Cts.Token);
                _taskManager.CompleteTask(taskInfo.Id);
            }
            catch (OperationCanceledException)
            {
                _taskManager.RemoveTask(taskInfo.Id);
            }
            catch (Exception ex)
            {
                _taskManager.FailTask(taskInfo.Id, ex.Message);
            }
        });
    }

    public void ShowCompressDialog()
    {
        var sources = SelectedEntries.Count > 0
            ? SelectedEntries.Select(e => e.FullPath).ToList()
            : ContextMenuEntry != null ? new List<string> { ContextMenuEntry.FullPath } : new List<string>();
        if (sources.Count == 0) return;

        // Sentinel paths (collection/archive) are not real dirs — use first file's parent
        var outputDir = (IsCollectionView || IsArchiveView)
            ? Path.GetDirectoryName(sources[0]) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : CurrentPath;

        var defaultName = sources.Count == 1
            ? Path.GetFileNameWithoutExtension(sources[0])
            : Path.GetFileName(outputDir) ?? "archive";

        PendingCompressOptions = new CompressOptions
        {
            ArchiveName = defaultName,
            OutputDirectory = outputDir,
            SourcePaths = sources,
            CollectionId = IsCollectionView ? CurrentCollectionId : null
        };
        IsCompressDialogVisible = true;
    }

    public void ConfirmCompress(CompressOptions options)
    {
        IsCompressDialogVisible = false;
        PendingCompressOptions = null;
        if (_archiveService == null || _taskManager == null) return;

        var collectionId = options.CollectionId;

        var taskInfo = _taskManager.AddTask("正在压缩...", async () =>
        {
            if (collectionId != null && _collectionService != null)
            {
                var ext = options.Format switch
                {
                    ArchiveFormat.Zip => ".zip",
                    ArchiveFormat.TarGz => ".tar.gz",
                    ArchiveFormat.TarBz2 => ".tar.bz2",
                    _ => ".zip"
                };
                var outputPath = Path.Combine(options.OutputDirectory, options.ArchiveName + ext);
                await _collectionService.AddFileToCollectionAsync(collectionId.Value, outputPath);
            }
            await RefreshAsync();
        });
        ActiveTaskId = taskInfo.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<ArchiveProgress>(p =>
                {
                    _taskManager.UpdateProgress(taskInfo.Id, p.Percentage, p.CurrentFile);
                });
                await _archiveService.CompressAsync(options, progress, taskInfo.Cts.Token);
                _taskManager.CompleteTask(taskInfo.Id);
            }
            catch (OperationCanceledException)
            {
                _taskManager.RemoveTask(taskInfo.Id);
            }
            catch (Exception ex)
            {
                _taskManager.FailTask(taskInfo.Id, ex.Message);
            }
        });
    }

    public void MinimizeActiveTask()
    {
        if (ActiveTaskId != null)
        {
            _taskManager?.MinimizeTask(ActiveTaskId);
            ActiveTaskId = null;
        }
    }

    public void CancelCompressDialog()
    {
        IsCompressDialogVisible = false;
        PendingCompressOptions = null;
    }

    [RelayCommand]
    public async Task NavigateBackAsync()
    {
        if (!CanGoBack) return;
        _historyIndex--;
        _isNavigatingHistory = true;
        ScrollBehaviorAfterLoad = ScrollMode.RestoreNavigation;
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
        ScrollBehaviorAfterLoad = ScrollMode.RestoreNavigation;
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
            if (_archiveService == null || _launcherService == null) return;
            try
            {
                var (archPath, entryKey) = ArchivePathHelper.Parse(entry.FullPath);
                var tempFile = await _archiveService.ExtractEntryToTempAsync(archPath, entryKey);
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
                        Execute = () => { RequestRename(entry); return Task.CompletedTask; }
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
            else if (IsInTrash)
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
            // Yield to allow Blazor to render selection highlight before the blocking native menu
            await Task.Delay(50);
            // Must dispatch to main thread — popUpMenu is modal and must run on the UI thread
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
                        Execute = () => CurrentArchivePath != null
                            ? NavigateToAsync(ArchivePathHelper.Build(CurrentArchivePath, CurrentArchiveInternalPath))
                            : Task.CompletedTask
                    }
                });
            }
            else if (IsCollectionView || IsAiView)
            {
                // Collection/AI view: no background context menu
                return;
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
                    IsQuickAction = action.IsQuickAction,
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
                    IsQuickAction = action.IsQuickAction,
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
                    IsQuickAction = action.IsQuickAction,
                    Execute = () =>
                    {
                        RequestRename(entry);
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
                // 动态判断当前文件夹是否已Pin
                var isPinned = _pinnedFolderService != null && await _pinnedFolderService.IsPinnedAsync(entry.FullPath);
                result.Add(new ContextMenuAction
                {
                    Label = isPinned ? "取消Pin" : "Pin到收藏",
                    IconSvg = Icons.Pin,
                    Execute = isPinned
                        ? () => UnpinFolderAsync(entry.FullPath)
                        : () => PinFolderAsync(entry.FullPath, entry.Name)
                });
            }
            else
            {
                result.Add(action);
            }
        }

        // Insert "添加到收藏夹" submenu before the last separator+info block
        if (_collectionService != null && Collections.Count > 0 && !entry.IsDirectory)
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
        if (CutPaths.Count > 0) { CutPaths.Clear(); OnPropertyChanged(nameof(CutPaths)); }
        StatusText = $"已拷贝 {SelectedEntries.Count} 项";
    }

    [RelayCommand]
    public void CutSelected()
    {
        if (_clipboardService == null || SelectedEntries.Count == 0) return;
        _clipboardService.CutFiles(SelectedEntries.Select(e => e.FullPath).ToArray());
        CutPaths.Clear();
        foreach (var e in SelectedEntries) CutPaths.Add(e.FullPath);
        OnPropertyChanged(nameof(CutPaths));
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
            if (entry.Operation == ClipboardOperation.Cut) { _clipboardService.Clear(); CutPaths.Clear(); OnPropertyChanged(nameof(CutPaths)); }
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
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
            var deletedPaths = SelectedEntries.Select(e => e.FullPath).ToList();
            foreach (var entry in SelectedEntries.ToList())
                await _fileService.DeleteAsync(entry.FullPath, moveToTrash: true);

            // Clean up AI analysis data for deleted files
            if (_aiTagService != null)
            {
                try { await _aiTagService.DeleteAnalysisForFilesAsync(deletedPaths); }
                catch { }
            }

            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            if (IsCollectionView && CurrentCollectionId != null)
                await NavigateToCollectionAsync(CurrentCollectionId.Value);
            else
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

    private string GetUniqueNameInCurrentDir(string baseName, bool isDirectory)
    {
        var existingNames = new HashSet<string>(_rawEntries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains(baseName)) return baseName;

        // For files, separate name and extension
        string nameWithoutExt, ext;
        if (!isDirectory)
        {
            ext = Path.GetExtension(baseName);
            nameWithoutExt = string.IsNullOrEmpty(ext) ? baseName : baseName[..^ext.Length];
        }
        else
        {
            ext = "";
            nameWithoutExt = baseName;
        }

        for (int i = 2; ; i++)
        {
            var candidate = $"{nameWithoutExt} {i}{ext}";
            if (!existingNames.Contains(candidate)) return candidate;
        }
    }

    [RelayCommand]
    public async Task CreateNewFolderAsync()
    {
        try
        {
            var name = GetUniqueNameInCurrentDir("未命名文件夹", isDirectory: true);
            var fullPath = await _fileService.CreateFolderAsync(CurrentPath, name);
            ScrollBehaviorAfterLoad = ScrollMode.ScrollToSelected;
            await LoadDirectoryContentsAsync(forceRefresh: true);
            var newEntry = Entries.FirstOrDefault(e => e.FullPath == fullPath);
            if (newEntry != null)
            {
                SelectedEntries.Clear();
                SelectedEntries.Add(newEntry);
                // 自动进入重命名编辑模式
                RequestRename(newEntry);
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
            var baseName = ext.ToLowerInvariant() switch
            {
                ".docx" => "未命名文稿.docx",
                ".xlsx" => "未命名表格.xlsx",
                ".pptx" => "未命名演示文稿.pptx",
                ".pages" => "未命名文稿.pages",
                ".numbers" => "未命名表格.numbers",
                ".key" => "未命名演示文稿.key",
                ".txt" => "未命名.txt",
                _ => $"未命名{ext}"
            };
            var name = GetUniqueNameInCurrentDir(baseName, isDirectory: false);

            var template = FileTemplateProvider.GetTemplate(ext);
            var fullPath = template != null
                ? await _fileService.CreateFileWithContentAsync(CurrentPath, name, template)
                : await _fileService.CreateFileAsync(CurrentPath, name);

            ScrollBehaviorAfterLoad = ScrollMode.ScrollToSelected;
            await LoadDirectoryContentsAsync(forceRefresh: true);
            var newEntry = Entries.FirstOrDefault(e => e.FullPath == fullPath);
            if (newEntry != null)
            {
                SelectedEntries.Clear();
                SelectedEntries.Add(newEntry);
                RequestRename(newEntry);
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
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
        }
        catch (Exception ex) { StatusText = $"移动失败: {ex.Message}"; }
    }

    public async Task MoveEntriesAsync(IReadOnlyList<FileSystemEntry> entries, FileSystemEntry targetFolder)
    {
        if (!targetFolder.IsDirectory) return;

        var sourcePaths = entries.Where(e => e != targetFolder)
            .Select(e => e.FullPath).ToList();
        if (sourcePaths.Count == 0) return;

        bool crossVolume = _fileService.IsCrossVolume(sourcePaths[0], targetFolder.FullPath);

        if (!crossVolume)
        {
            try
            {
                foreach (var path in sourcePaths)
                    await _fileService.MoveAsync(path, targetFolder.FullPath);
                ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
                await LoadDirectoryContentsAsync(forceRefresh: true);
            }
            catch (Exception ex) { StatusText = $"移动失败: {ex.Message}"; }
            return;
        }

        // 跨卷：后台任务 + 进度弹窗
        if (_taskManager == null) return;

        var taskInfo = _taskManager.AddTask("正在移动...", async () =>
        {
            await RefreshAsync();
        });
        ActiveTaskId = taskInfo.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<Models.FileOperationProgress>(p =>
                {
                    _taskManager.UpdateProgress(taskInfo.Id, p.Percentage, p.CurrentFile);
                });
                await _fileService.MoveWithProgressAsync(sourcePaths, targetFolder.FullPath,
                    progress, taskInfo.Cts.Token);
                _taskManager.CompleteTask(taskInfo.Id);
            }
            catch (OperationCanceledException)
            {
                _taskManager.RemoveTask(taskInfo.Id);
            }
            catch (Exception ex)
            {
                _taskManager.FailTask(taskInfo.Id, ex.Message);
            }
        });
    }

    /// <summary>
    /// Handles files dropped from external sources (Finder, other apps, other FKFinder windows).
    /// Called via IDragDropBridge.ExternalDropReceived event.
    /// </summary>
    private async void HandleExternalDrop(string[] sourcePaths, string targetDirectory)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            int moved = 0;
            foreach (var sourcePath in sourcePaths)
            {
                // Skip files already in the target directory (same-window drag to content area)
                if (Path.GetDirectoryName(sourcePath) == targetDirectory)
                    continue;

                try
                {
                    await _fileService.MoveAsync(sourcePath, targetDirectory);
                    moved++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FKFinder] Move failed: {sourcePath} → {ex.Message}");
                }
            }

            if (moved > 0)
            {
                ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
                await LoadDirectoryContentsAsync(forceRefresh: true);
                StatusText = $"已移动 {moved} 项";
            }
        });
    }

    // Rename support: event to notify the view to start inline rename
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
            await RenameFaceClusterAsync(clusterId, newName);
            return;
        }

        // Other virtual entries cannot be renamed
        if (entry.IsVirtual)
            return;

        try
        {
            var oldPath = entry.FullPath;
            await _fileService.RenameAsync(oldPath, newName);

            var dir = Path.GetDirectoryName(oldPath) ?? "";
            var newPath = Path.Combine(dir, newName);

            if (IsAiView)
            {

                if (_aiTagService != null)
                    await _aiTagService.UpdateFilePathAsync(oldPath, newPath);

                // Update file index so FTS5 search reflects the new name
                await _fileIndexWriter.RenameEntryAsync(oldPath, newPath, newName);

                // 同步更新 PIN 文件夹路径
                if (_pinnedFolderService != null && entry.IsDirectory)
                {
                    await _pinnedFolderService.UpdateFolderPathAsync(oldPath, newPath, newName);
                    await LoadPinnedFoldersAsync();
                }

                for (int i = 0; i < Entries.Count; i++)
                {
                    if (Entries[i].FullPath == oldPath)
                    {
                        var old = Entries[i];
                        Entries[i] = new FileSystemEntry
                        {
                            FullPath = newPath,
                            Name = newName,
                            IsDirectory = old.IsDirectory,
                            Size = old.Size,
                            LastModified = DateTime.Now,
                            Created = old.Created,
                            Extension = Path.GetExtension(newName),
                            IsHidden = old.IsHidden,
                            IsSymbolicLink = old.IsSymbolicLink,
                            IsReadable = old.IsReadable,
                            IsWritable = old.IsWritable,
                            IconKey = old.IconKey,
                            IconUrl = old.IconUrl,
                            ThumbnailUrl = old.ThumbnailUrl,
                        };
                        SelectedEntries.Clear();
                        SelectedEntries.Add(Entries[i]);
                        break;
                    }
                }
                OnPropertyChanged(nameof(Entries));
                return;
            }

            // 同步更新 PIN 文件夹路径
            if (_pinnedFolderService != null && entry.IsDirectory)
            {
                await _pinnedFolderService.UpdateFolderPathAsync(oldPath, newPath, newName);
                await LoadPinnedFoldersAsync();
            }

            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
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
        _ = TriggerImageAnalysisAsync(entries);

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
        }

        // Auto-select a specific file (e.g. from breadcrumb search suggestion)
        bool didSelectPending = false;
        if (PendingSelectFileName != null)
        {
            var target = Entries.FirstOrDefault(e => e.Name == PendingSelectFileName);
            if (target != null)
            {
                SelectedEntries.Clear();
                SelectedEntries.Add(target);
                _lastClickedEntry = target;
                didSelectPending = true;
            }
            PendingSelectFileName = null;
        }

        // Reset to PreservePosition so background icon/thumbnail updates don't affect scroll
        // But keep ScrollToSelected when a pending file was just selected, so the frontend can read the signal
        if (!didSelectPending)
        {
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
        }

        StatusText = $"{Entries.Count} 项";
    }

    partial void OnHideSystemFilesChanged(bool value)
    {
        _settingsService?.Set("HideSystemFiles", value);
        ApplySortAndGroup();
    }

    partial void OnIsAiAnalysisEnabledChanged(bool value)
    {
        _settingsService?.Set("IsAiAnalysisEnabled", value);
    }

    private void ApplySortAndGroup()
    {
        if (_rawEntries.Count == 0) { Entries = []; Groups = []; return; }
        var filtered = _rawEntries.Where(e => !e.Name.EndsWith(".fkfinder-tmp"));
        if (HideSystemFiles)
            filtered = filtered.Where(e => !SystemFileNames.Contains(e.Name));
        var list = filtered.ToList();
        var sorted = SortEntries(list).ToList();
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

    private void UpdateBreadcrumbs()
    {
        var segments = new List<BreadcrumbSegment>();
        if (IsCollectionView && CurrentCollectionName != null)
        {
            segments.Add(new BreadcrumbSegment { Name = "收藏夹", FullPath = "", HasDropdown = false });
            segments.Add(new BreadcrumbSegment { Name = CurrentCollectionName, FullPath = "", HasDropdown = false });
        }
        else if (IsArchiveView && CurrentArchivePath != null)
        {
            var archiveDir = Path.GetDirectoryName(CurrentArchivePath) ?? "/";
            segments.Add(new BreadcrumbSegment { Name = "/", FullPath = "/", HasDropdown = true });
            var dirParts = archiveDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var buildPath = "";
            foreach (var part in dirParts)
            {
                buildPath += "/" + part;
                segments.Add(new BreadcrumbSegment { Name = part, FullPath = buildPath, HasDropdown = true });
            }
            var archiveName = Path.GetFileName(CurrentArchivePath);
            segments.Add(new BreadcrumbSegment
            {
                Name = archiveName,
                FullPath = ArchivePathHelper.Build(CurrentArchivePath, ""),
                HasDropdown = !string.IsNullOrEmpty(CurrentArchiveInternalPath)
            });
            if (!string.IsNullOrEmpty(CurrentArchiveInternalPath))
            {
                var internalParts = CurrentArchiveInternalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var internalBuild = "";
                for (int i = 0; i < internalParts.Length; i++)
                {
                    internalBuild += (internalBuild.Length > 0 ? "/" : "") + internalParts[i];
                    segments.Add(new BreadcrumbSegment
                    {
                        Name = internalParts[i],
                        FullPath = ArchivePathHelper.Build(CurrentArchivePath, internalBuild + "/"),
                        HasDropdown = i < internalParts.Length - 1
                    });
                }
            }
        }
        else if (IsAiView)
        {
            segments.Add(new BreadcrumbSegment { Name = "AI 智能", FullPath = "", HasDropdown = false });
            var modeName = AiPathHelper.GetModeName(AiViewMode);
            var modePath = AiPathHelper.GetTopLevelPath(AiViewMode);
            var isDetail = CurrentAiContextLabel != null;
            segments.Add(new BreadcrumbSegment { Name = modeName, FullPath = modePath, HasDropdown = isDetail });
            if (isDetail)
            {
                segments.Add(new BreadcrumbSegment { Name = CurrentAiContextLabel!, FullPath = CurrentPath, HasDropdown = false });
            }
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
        IsAiView = false;
        CurrentFaceClusterId = null;
        CurrentAiContextLabel = null;
        TextTokens.Clear();
        IsArchiveView = false;
        CurrentArchivePath = null;
        CurrentArchiveInternalPath = "";
        IsSearchMode = false;
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

    // ── Pinned Folders ──────────────────────────────────────────────

    private async Task LoadPinnedFoldersAsync()
    {
        if (_pinnedFolderService == null) return;
        var pins = await _pinnedFolderService.GetAllAsync();
        PinnedFolders = new ObservableCollection<PinnedFolder>(pins);
    }

    public async Task PinFolderAsync(string path, string displayName)
    {
        if (_pinnedFolderService == null) return;
        await _pinnedFolderService.PinAsync(path, displayName);
        await LoadPinnedFoldersAsync();
    }

    public async Task UnpinFolderAsync(string path)
    {
        if (_pinnedFolderService == null) return;
        await _pinnedFolderService.UnpinAsync(path);
        await LoadPinnedFoldersAsync();
    }

    public async Task<bool> IsFolderPinnedAsync(string path)
    {
        if (_pinnedFolderService == null) return false;
        return await _pinnedFolderService.IsPinnedAsync(path);
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

    // ── AI Image Analysis ──

    private static readonly HashSet<string> ImageExtensionsForAi = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif",
        ".webp", ".heic", ".heif", ".dng", ".cr2", ".cr3", ".nef", ".arw"
    };

    private async Task TriggerImageAnalysisAsync(IReadOnlyList<FileSystemEntry> entries)
    {
        if (_aiTagService == null || _imageAnalysisService == null || _taskManager == null) return;
        if (!IsAiAnalysisEnabled) return;

        try
        {
            // 1. Filter image files
            var imageEntries = entries
                .Where(e => !e.IsDirectory && ImageExtensionsForAi.Contains(e.Extension))
                .ToList();
            if (imageEntries.Count == 0) return;

            // 2. Batch check analysis status — find unanalyzed/outdated files
            var toAnalyze = await _aiTagService.GetUnanalyzedFilesAsync(
                imageEntries.Select(e => e.FullPath).ToList(),
                imageEntries.Select(e => e.LastModified.Ticks).ToList());

            // 3. Detect deleted files — clean up orphan AI data
            var currentPaths = new HashSet<string>(imageEntries.Select(e => e.FullPath));
            var analyzedPaths = await _aiTagService.GetAnalyzedPathsInDirectoryAsync(CurrentPath);
            var deletedPaths = analyzedPaths.Where(p => !currentPaths.Contains(p)).ToList();
            if (deletedPaths.Count > 0)
            {
                try { await _aiTagService.DeleteAnalysisForFilesAsync(deletedPaths); }
                catch { }
            }

            // 4. Nothing to analyze
            if (toAnalyze.Count == 0) return;

            // 5. Run analysis in background
            var taskInfo = _taskManager.AddTask($"AI 图像分析 0/{toAnalyze.Count}");
            var semaphore = new SemaphoreSlim(3);
            var completed = 0;

            try
            {
                var tasks = toAnalyze.Select(async file =>
                {
                    await semaphore.WaitAsync(taskInfo.Cts.Token);
                    try
                    {
                        taskInfo.Cts.Token.ThrowIfCancellationRequested();
                        var result = await _imageAnalysisService.AnalyzeImageAsync(file.Path, taskInfo.Cts.Token);
                        await _aiTagService.SaveAnalysisResultAsync(file.Path, file.ModifiedTicks, result);
                        var count = Interlocked.Increment(ref completed);
                        _taskManager.UpdateProgress(taskInfo.Id, (double)count / toAnalyze.Count,
                            $"AI 图像分析 {count}/{toAnalyze.Count}");
                    }
                    finally { semaphore.Release(); }
                });

                await Task.WhenAll(tasks);

                // 6. Run face clustering
                await _aiTagService.RunClusteringAsync();
            }
            catch (OperationCanceledException) { }
            finally
            {
                _taskManager.CompleteTask(taskInfo.Id);
                semaphore.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI analysis trigger failed: {ex.Message}");
        }
    }

    // ── AI View Navigation ──

    private static FileSystemEntry CreateVirtualEntry(FaceCluster cluster) => new()
    {
        FullPath = $"__ai:face:{cluster.Id}",
        Name = cluster.DisplayName ?? "未命名",
        IsDirectory = true,
        IconKey = "ai-face",
        ThumbnailUrl = cluster.FaceThumbnailUrl,
        Size = cluster.FaceCount,
        LastModified = cluster.UpdatedAt,
        Created = cluster.CreatedAt,
        IsVirtual = true,
        VirtualFolderType = "face",
        VirtualFolderKey = cluster.Id.ToString(),
        VirtualItemCount = cluster.FaceCount,
    };

    private static FileSystemEntry CreateVirtualEntry(AiCategory category) => new()
    {
        FullPath = $"__ai:{category.TagType}:{category.TagValue}",
        Name = category.TagValue,
        IsDirectory = true,
        IconKey = $"ai-{category.TagType}",
        Size = category.FileCount,
        IsVirtual = true,
        VirtualFolderType = category.TagType,
        VirtualFolderKey = $"{category.TagType}:{category.TagValue}",
        VirtualItemCount = category.FileCount,
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

    [RelayCommand]
    public async Task NavigateToAiViewAsync(AiViewMode mode)
        => await NavigateToAsync(AiPathHelper.GetTopLevelPath(mode));

    [RelayCommand]
    public async Task NavigateToFaceClusterAsync(int clusterId)
        => await NavigateToAsync($"__ai:face:{clusterId}");

    public async Task NavigateToAiCategoryAsync(string tagType, string tagValue)
        => await NavigateToAsync($"__ai:{tagType}:{tagValue}");

    private async Task HandleAiNavigationAsync(string sentinelPath)
    {
        if (_aiTagService == null) return;
        var info = AiPathHelper.Parse(sentinelPath);

        if (info.Mode == AiViewMode.TextSearch && info.IsTopLevel)
        {
            // TextSearch: skip duplicate guard, allow re-entry
        }
        else if (CurrentPath == sentinelPath && Entries.Count > 0)
        {
            return;
        }

        if (IsMetadataPanelVisible)
            CloseMetadata();

        IsHomePage = false;
        IsCollectionView = false;
        IsArchiveView = false;
        CurrentArchivePath = null;
        CurrentArchiveInternalPath = "";
        IsSearchMode = false;
        IsAiView = true;
        AiViewMode = info.Mode;
        IsLoading = true;

        try
        {
            if (!string.IsNullOrEmpty(CurrentPath) && SelectedEntries.Count > 0)
                _pathSelectedEntries[CurrentPath] = SelectedEntries.First().Name;

            if (!_isNavigatingHistory)
            {
                if (_historyIndex < _historyStack.Count - 1)
                    _historyStack.RemoveRange(_historyIndex + 1, _historyStack.Count - _historyIndex - 1);
                _historyStack.Add(sentinelPath);
                _historyIndex = _historyStack.Count - 1;
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoForward));
            }

            CurrentPath = sentinelPath;

            if (info.IsTopLevel)
            {
                CurrentFaceClusterId = null;
                CurrentAiContextLabel = null;
                await LoadAiTopLevelAsync(info.Mode);
            }
            else if (info.IsFaceDetail)
            {
                // Ensure FaceClusters is populated (e.g. when navigating directly from search)
                if (FaceClusters.Count == 0 && _aiTagService != null)
                {
                    var clusters = await _aiTagService.GetAllFaceClustersAsync();
                    FaceClusters.Clear();
                    foreach (var c in clusters) FaceClusters.Add(c);
                }
                await LoadFaceClusterEntriesAsync(info.FaceClusterId!.Value);
            }
            else
            {
                CurrentFaceClusterId = null;
                CurrentAiContextLabel = info.TagValue;
                await LoadAiCategoryEntriesAsync(info.TagType!, info.TagValue!);
            }

            UpdateBreadcrumbs();
        }
        catch (Exception ex)
        {
            StatusText = $"加载 AI 视图失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadAiTopLevelAsync(AiViewMode mode)
    {
        switch (mode)
        {
            case AiViewMode.People:
                var clusters = await _aiTagService!.GetAllFaceClustersAsync();
                FaceClusters.Clear();
                foreach (var c in clusters) FaceClusters.Add(c);
                var peopleEntries = clusters.Select(CreateVirtualEntry).ToList();
                _rawEntries = peopleEntries;
                GroupField = GroupField.None;
                Entries = new ObservableCollection<FileSystemEntry>(peopleEntries);
                SelectedEntries.Clear();
                StatusText = $"{clusters.Count} 个人物";
                _ = ResolveFaceThumbnailsAsync(clusters);
                break;

            case AiViewMode.Categories:
                AiCategories.Clear();
                var allCats = new List<AiCategory>();
                foreach (var type in new[] { "scene", "object", "animal" })
                {
                    var cats = await _aiTagService!.GetCategoriesByTypeAsync(type);
                    foreach (var c in cats)
                    {
                        AiCategories.Add(c);
                        allCats.Add(c);
                    }
                }
                var catEntries = allCats.Select(CreateVirtualEntry).ToList();
                _rawEntries = catEntries;
                GroupField = GroupField.Type;
                Entries = new ObservableCollection<FileSystemEntry>(catEntries);
                Groups = new ObservableCollection<FileGroup>(BuildGroups(catEntries));
                SelectedEntries.Clear();
                StatusText = $"{allCats.Count} 个分类";
                break;

            case AiViewMode.Locations:
                AiCategories.Clear();
                var locations = await _aiTagService!.GetCategoriesByTypeAsync("location");
                foreach (var l in locations) AiCategories.Add(l);
                var locEntries = locations
                    .OrderByDescending(l => l.FileCount)
                    .Select(CreateVirtualEntry).ToList();
                _rawEntries = locEntries;
                GroupField = GroupField.None;
                Entries = new ObservableCollection<FileSystemEntry>(locEntries);
                SelectedEntries.Clear();
                StatusText = $"{locations.Count} 个地点";
                break;

            case AiViewMode.Dates:
                AiCategories.Clear();
                var dates = await _aiTagService!.GetCategoriesByTypeAsync("date");
                foreach (var d in dates) AiCategories.Add(d);
                var dateEntries = dates
                    .OrderByDescending(d => d.TagValue)
                    .Select(CreateVirtualEntry).ToList();
                _rawEntries = dateEntries;
                GroupField = GroupField.None;
                Entries = new ObservableCollection<FileSystemEntry>(dateEntries);
                SelectedEntries.Clear();
                StatusText = $"{dates.Count} 个日期";
                break;

            case AiViewMode.TextSearch:
                _rawEntries = [];
                Entries.Clear();
                SelectedEntries.Clear();
                Groups.Clear();
                TextTokens.Clear();
                if (_aiTagService != null)
                {
                    var tokens = await _aiTagService.GetPopularTextTagsAsync();
                    foreach (var t in tokens) TextTokens.Add(t);
                }
                StatusText = TextTokens.Count > 0 ? $"{TextTokens.Count} 个热门文字标签" : "";
                break;
        }
    }

    private async Task LoadFaceClusterEntriesAsync(int clusterId)
    {
        var filePaths = await _aiTagService!.GetFilePathsForClusterAsync(clusterId);
        var entries = new List<FileSystemEntry>();
        foreach (var p in filePaths)
        {
            var entry = await _fileIndex.GetEntryAsync(p);
            if (entry != null) entries.Add(entry);
        }

        CurrentFaceClusterId = clusterId;
        var cluster = FaceClusters.FirstOrDefault(c => c.Id == clusterId);
        CurrentAiContextLabel = cluster?.DisplayName ?? "未命名";
        _rawEntries = entries;
        GroupField = GroupField.None;
        Entries = new ObservableCollection<FileSystemEntry>(entries);
        SelectedEntries.Clear();
        _ = ResolveThumbnailsInBackgroundAsync(entries);
    }

    private async Task LoadAiCategoryEntriesAsync(string tagType, string tagValue)
    {
        var filePaths = await _aiTagService!.GetFilePathsForCategoryAsync(tagType, tagValue);
        var entries = new List<FileSystemEntry>();
        foreach (var p in filePaths)
        {
            var entry = await _fileIndex.GetEntryAsync(p);
            if (entry != null) entries.Add(entry);
        }

        CurrentAiContextLabel = tagValue;
        _rawEntries = entries;
        GroupField = GroupField.None;
        Entries = new ObservableCollection<FileSystemEntry>(entries);
        SelectedEntries.Clear();
        _ = ResolveThumbnailsInBackgroundAsync(entries);
    }

    public async Task RenameFaceClusterAsync(int clusterId, string name)
    {
        if (_aiTagService == null) return;
        try
        {
            await _aiTagService.SetClusterNameAsync(clusterId, name);

            // Update in-memory FaceClusters
            var cluster = FaceClusters.FirstOrDefault(c => c.Id == clusterId);
            if (cluster != null)
                cluster.DisplayName = name;

            // Replace virtual entry in Entries (Name is init-only)
            var path = $"__ai:face:{clusterId}";
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].FullPath == path)
                {
                    var old = Entries[i];
                    Entries[i] = new FileSystemEntry
                    {
                        FullPath = old.FullPath,
                        Name = name,
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
                    break;
                }
            }
            OnPropertyChanged(nameof(Entries));
        }
        catch (Exception ex) { StatusText = $"重命名失败: {ex.Message}"; }
    }

    private async Task ResolveFaceThumbnailsAsync(IReadOnlyList<FaceCluster> clusters)
    {
        if (_thumbnailService == null) return;
        foreach (var cluster in clusters)
        {
            if (string.IsNullOrEmpty(cluster.RepresentativeFacePath) || cluster.BoundingBoxW <= 0)
                continue;
            try
            {
                var bytes = await _thumbnailService.GetFaceCropAsync(
                    cluster.RepresentativeFacePath,
                    cluster.BoundingBoxX, cluster.BoundingBoxY,
                    cluster.BoundingBoxW, cluster.BoundingBoxH);
                if (bytes != null)
                {
                    var url = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
                    cluster.FaceThumbnailUrl = url;
                    // Propagate thumbnail to virtual entry in Entries
                    var virtualKey = cluster.Id.ToString();
                    var entry = Entries.FirstOrDefault(e => e.IsVirtual && e.VirtualFolderKey == virtualKey);
                    if (entry != null)
                        entry.ThumbnailUrl = url;
                    OnPropertyChanged(nameof(FaceClusters));
                    OnPropertyChanged(nameof(Entries));
                }
            }
            catch { }
        }
    }

    [RelayCommand]
    public async Task SearchAiTagsAsync(string query)
    {
        if (_aiTagService == null || string.IsNullOrWhiteSpace(query)) return;
        try
        {
            var paths = await _aiTagService.SearchByTagAsync(query);
            var entries = new List<FileSystemEntry>();
            foreach (var path in paths)
            {
                var entry = await _fileIndex.GetEntryAsync(path);
                if (entry != null) entries.Add(entry);
            }

            CurrentAiContextLabel = $"搜索: {query}";
            Entries = new ObservableCollection<FileSystemEntry>(entries);
            SelectedEntries.Clear();
            StatusText = $"AI 搜索 \"{query}\" — 找到 {entries.Count} 项";
            _ = ResolveThumbnailsInBackgroundAsync(entries);
        }
        catch (Exception ex) { StatusText = $"AI 搜索失败: {ex.Message}"; }
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
