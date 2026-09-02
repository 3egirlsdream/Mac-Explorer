using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Views.Dialogs;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.Logging;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel : ObservableObject, IDisposable
{
    private readonly IFileService _fileService;
    private readonly IFileIndex _fileIndex;
    private readonly IFileIndexWriter _fileIndexWriter;
    private readonly IndexConfiguration _indexConfig;
    private readonly IContextMenuService? _contextMenuService;
    private readonly IMetadataService? _metadataService;
    private readonly IThumbnailService? _thumbnailService;
    private readonly IQuickLookService? _quickLookService;
    private readonly INativeContextMenuService? _nativeContextMenuService;
    private readonly IDragDropBridge? _dragDropBridge;
    private readonly IDirectoryChangeNotifier? _directoryChangeNotifier;
    private readonly IClipboardService? _clipboardService;
    private readonly IApplicationLauncherService? _launcherService;
    private readonly ISettingsService? _settingsService;
    private readonly IArchiveService? _archiveService;
    private readonly ILogger<FileListViewModel>? _logger;
    private readonly IGitStatusService? _gitStatusService;
    private readonly IDisplayNameService? _displayNameService;
    private readonly IVolumeMonitorService? _volumeMonitorService;
    private readonly IRemoteConnectionService? _remoteConnectionService;
    private readonly IRemoteFileService? _sftpFileService;
    private readonly IRemoteFileEditService? _remoteFileEditService;
    private readonly IOpenWithAppService? _openWithAppService;
    private readonly IFileTagService? _fileTagService;
    private FileListColumnLayoutService _columnLayoutService;

    public event Action? TransientInteractionStarted;

    // Sub-viewmodels
    private readonly NavigationViewModel _navigation;
    private readonly FileOpsViewModel _fileOps;
    private readonly SearchViewModel _search;
    private readonly ArchiveViewModel _archive;
    private readonly AiViewModel _ai;
    private readonly CollectionViewModel _collection;
    private readonly SortFilterViewModel _sortFilter;

    public ArchiveViewModel Archive => _archive;

    public void SetOwnerWindow(Window? owner)
    {
        _topLevelWindow = owner;
    }

    private bool _isRefreshingFromNotification;
    private FileSystemEntry? _lastClickedEntry;
    private string? _lastClickedPath;
    private readonly object _directoryWorkLock = new();
    private CancellationTokenSource _directoryWorkCts = new();
    private readonly CancellationTokenSource _startupSidebarCts = new();
    private int _directoryWorkGeneration;
    private string? _metadataLoadPath;
    private Task? _metadataLoadTask;
    private int _metadataLoadGeneration;
    private CancellationTokenSource? _metadataLoadDebounceCts;
    private int _selectionPreviewSuppressionDepth;
    private bool _disposed;
    private Window? _topLevelWindow;

    private const string LastDirectorySettingKey = "navigation_last_directory";
    private const string PreviewPaneVisibleSettingKey = "IsPreviewPaneVisible";
    private const string MetadataPanelVisibleSettingKey = "IsMetadataPanelVisible";
    private const int SelectionMetadataDelayMs = 350;

    // ── Properties that remain in coordinator ──

    [ObservableProperty]
    private ObservableCollection<FileSystemEntry> _entries = [];

    [ObservableProperty]
    private ObservableCollection<FileSystemEntry> _selectedEntries = [];

    private readonly HashSet<FileSystemEntry> _selectedEntriesSet = [];
    private readonly HashSet<FileSystemEntry> _selectionFlaggedEntries = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    private string _locationStatusText = string.Empty;
    private string _locationStatusTooltip = string.Empty;
    private bool _isRemoteLocation;
    private bool _isRemoteLocationConnected;
    private RemoteServerInfo? _locationRemoteServer;

    public string StatusSummaryText => FileListStatusFormatter.FormatSelectionSummary(Entries, SelectedEntries);
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusText);
    public string LocationStatusText
    {
        get => _locationStatusText;
        private set
        {
            if (!SetProperty(ref _locationStatusText, value)) return;
            OnPropertyChanged(nameof(HasLocationStatus));
        }
    }
    public string LocationStatusTooltip
    {
        get => _locationStatusTooltip;
        private set => SetProperty(ref _locationStatusTooltip, value);
    }
    public bool HasLocationStatus => !string.IsNullOrWhiteSpace(LocationStatusText);
    public bool IsRemoteLocation
    {
        get => _isRemoteLocation;
        private set
        {
            if (!SetProperty(ref _isRemoteLocation, value)) return;
            OnPropertyChanged(nameof(IsRemoteLocationDisconnected));
        }
    }
    public bool IsRemoteLocationConnected
    {
        get => _isRemoteLocationConnected;
        private set
        {
            if (!SetProperty(ref _isRemoteLocationConnected, value)) return;
            OnPropertyChanged(nameof(IsRemoteLocationDisconnected));
        }
    }
    public bool IsRemoteLocationDisconnected => IsRemoteLocation && !IsRemoteLocationConnected;
    internal FileListColumnLayoutService ColumnLayoutService => _columnLayoutService;

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

    [ObservableProperty]
    private bool _isInfoPanelVisible;

    // Enums - kept here for backward compatibility
    public enum ScrollMode { ResetToTop, RestoreNavigation, ScrollToSelected, PreservePosition }
    public ScrollMode ScrollBehaviorAfterLoad { get; set; } = ScrollMode.ResetToTop;
    public event Action? ScrollToSelectionRequested;
    public event Action? CaptureNavigationAnchorRequested;

    // Forwarded properties from sub-viewmodels for binding
    public string CurrentPath => _navigation.CurrentPath;
    public bool CanGoBack => _navigation.CanGoBack;
    public bool CanGoForward => _navigation.CanGoForward;
    public bool IsHomePage => _navigation.IsHomePage;
    public ObservableCollection<BreadcrumbSegment> Breadcrumbs => _navigation.Breadcrumbs;
    public string CurrentLocationTitle
    {
        get
        {
            var breadcrumbTitle = Breadcrumbs.LastOrDefault()?.DisplayName;
            return !string.IsNullOrWhiteSpace(breadcrumbTitle)
                ? breadcrumbTitle
                : IsHomePage ? "首页" : "Mac Explorer";
        }
    }
    public string HomeDirectory => _fileService.HomeDirectory;

    public string? GetRestorableDirectoryPath()
    {
        var path = _settingsService?.Get(LastDirectorySettingKey);
        return !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;
    }

    // Archive state forwarded
    public bool IsArchiveView => _navigation.IsArchiveView;
    public string? CurrentArchivePath => _navigation.CurrentArchivePath;
    public string CurrentArchiveInternalPath => _navigation.CurrentArchiveInternalPath;
    public bool IsCompressDialogVisible => _archive.IsCompressDialogVisible;
    public CompressOptions? PendingCompressOptions => _archive.PendingCompressOptions;

    // Search state forwarded
    public bool IsSearchMode => _navigation.IsSearchMode;
    public string SearchQuery => _navigation.SearchQuery;

    // Tag state is encoded in the virtual path so back/forward history can restore it.
    public bool IsTagView => TagPathHelper.IsTagPath(_navigation.CurrentPath);
    public FileTag? CurrentTag => TagPathHelper.TryParse(_navigation.CurrentPath, out var tag) ? tag : null;

    // Collection state forwarded
    public bool IsCollectionView => _navigation.IsCollectionView;
    public int? CurrentCollectionId => _navigation.CurrentCollectionId;
    public string? CurrentCollectionName => _navigation.CurrentCollectionName;
    public ObservableCollection<Collection> Collections => _collection.Collections;
    public ObservableCollection<PinnedFolder> PinnedFolders => _collection.PinnedFolders;

    // ── Sidebar items (instance-based for localized names) ──
    private IReadOnlyList<SidebarItem> _sidebarFavorites = [];
    private IReadOnlyList<SidebarItem> _sidebarLocations = [];
    private IReadOnlyList<SidebarItem> _sidebarAiItems = [];

    public IReadOnlyList<SidebarItem> SidebarFavorites => _sidebarFavorites;
    public IReadOnlyList<SidebarItem> SidebarLocations => _sidebarLocations;
    public IReadOnlyList<SidebarItem> SidebarAiItems => _sidebarAiItems;
    public ObservableCollection<FileTag> SidebarTags { get; } = [];

    // ── Sidebar localized names ──
    public string UserName { get; private set; } = "";
    public string DesktopName { get; private set; } = "";
    public string DocumentsName { get; private set; } = "";
    public string DownloadsName { get; private set; } = "";
    public string PicturesName { get; private set; } = "";
    public string MusicName { get; private set; } = "";
    public string ApplicationsName { get; private set; } = "";
    public string VolumeName { get; private set; } = "";
    public string TrashName { get; private set; } = "";

    // ── Sidebar visibility toggles ──
    public bool ShowSidebarUsername { get; set; } = true;
    public bool ShowSidebarDesktop { get; set; } = true;
    public bool ShowSidebarDocuments { get; set; } = true;
    public bool ShowSidebarDownloads { get; set; } = true;
    public bool ShowSidebarPictures { get; set; } = true;
    public bool ShowSidebarMusic { get; set; } = true;
    public bool ShowSidebarMacintoshHd { get; set; } = true;
    public bool ShowSidebarApplications { get; set; } = true;
    public bool ShowSidebarTrash { get; set; } = true;
    public bool ShowSidebarAiPeople { get; set; } = true;
    public bool ShowSidebarAiCategories { get; set; } = true;
    public bool ShowSidebarAiLocations { get; set; } = true;
    public bool ShowSidebarAiDates { get; set; } = true;
    public bool ShowSidebarAiTextSearch { get; set; } = true;

    // ── Sidebar collapse states ──
    private bool _isAiCollapsed;
    public bool IsAiSectionCollapsed
    {
        get => _isAiCollapsed;
        set { if (SetProperty(ref _isAiCollapsed, value)) _settingsService?.Set("sidebar_ai_collapsed", value); }
    }
    private bool _isCollCollapsed;
    public bool IsCollectionsSectionCollapsed
    {
        get => _isCollCollapsed;
        set { if (SetProperty(ref _isCollCollapsed, value)) _settingsService?.Set("sidebar_collections_collapsed", value); }
    }
    private bool _isTagsCollapsed;
    public bool IsTagsSectionCollapsed
    {
        get => _isTagsCollapsed;
        set { if (SetProperty(ref _isTagsCollapsed, value)) _settingsService?.Set("sidebar_tags_collapsed", value); }
    }

    // ── External volumes ──
    public ObservableCollection<VolumeInfo> ExternalVolumes { get; } = [];

    // ── Trash helpers ──
    public string TrashPath => _fileService.TrashDirectory;
    public bool IsTrashActive => CurrentPath == TrashPath;

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
    public bool HideDotFiles
    {
        get => _sortFilter.HideDotFiles;
        set => _sortFilter.HideDotFiles = value;
    }
    public bool HideDotFolders
    {
        get => _sortFilter.HideDotFolders;
        set => _sortFilter.HideDotFolders = value;
    }

    // Pending select for auto-selection after navigation
    public string? PendingSelectFileName
    {
        get => _navigation.PendingSelectFileName;
        set => _navigation.PendingSelectFileName = value;
    }

    // Cut paths from FileOps
    public HashSet<string> CutPaths => _fileOps.CutPaths;

    // Public access for command palette providers
    public FileOpsViewModel FileOps => _fileOps;

    // Events triggered by command palette commands (consumed by MainWindow)
    public event Action? RequestBatchRename;
    public event Action? RequestRemoteConnection;
    public event Action? RequestShowTaskPanel;

    public void RaiseRequestBatchRename() => RequestBatchRename?.Invoke();
    public void RaiseRequestRemoteConnection() => RequestRemoteConnection?.Invoke();
    public void RaiseRequestShowTaskPanel() => RequestShowTaskPanel?.Invoke();

    // Refresh helper for command palette actions that modify the directory
    public async Task RefreshAfterCreateAsync(string newName)
    {
        await RefreshAsync();
        PendingSelectFileName = newName;
    }

    public bool IsRestoringNavigation => ScrollBehaviorAfterLoad == ScrollMode.RestoreNavigation;
    public double? RestoredNavigationAnchorViewportY => _navigation.CurrentHistoryEntry?.SelectedEntryViewportY;
    public double? RestoredNavigationScrollOffsetY => _navigation.CurrentHistoryEntry?.SelectedEntryScrollOffsetY;
    private double? _capturedNavigationAnchorViewportY;
    private double? _capturedNavigationScrollOffsetY;

    public void SaveCurrentNavigationAnchor(double? viewportY, double? scrollOffsetY)
    {
        _capturedNavigationAnchorViewportY = viewportY;
        _capturedNavigationScrollOffsetY = scrollOffsetY;
    }

    private void CaptureCurrentNavigationViewState()
    {
        _capturedNavigationAnchorViewportY = null;
        _capturedNavigationScrollOffsetY = null;
        CaptureNavigationAnchorRequested?.Invoke();

        var selected = SelectedEntries.FirstOrDefault();
        _navigation.SaveCurrentHistorySelection(
            selected?.FullPath,
            selected?.Name,
            _capturedNavigationAnchorViewportY,
            _capturedNavigationScrollOffsetY);

        if (_navigation.IsSearchMode)
            _navigation.UpdateCurrentSearchResults(Entries.ToArray());
    }

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
        IQuickLookService? quickLookService = null,
        INativeContextMenuService? nativeContextMenuService = null,
        IClipboardService? clipboardService = null,
        IApplicationLauncherService? launcherService = null,
        ISettingsService? settingsService = null,
        IArchiveService? archiveService = null,
        IDragDropBridge? dragDropBridge = null,
        IDirectoryChangeNotifier? directoryChangeNotifier = null,
        ILoggerFactory? loggerFactory = null,
        IGitStatusService? gitStatusService = null,
        IDisplayNameService? displayNameService = null,
        IVolumeMonitorService? volumeMonitorService = null,
        IRemoteConnectionService? remoteConnectionService = null,
        IRemoteFileService? sftpFileService = null,
        IRemoteFileEditService? remoteFileEditService = null,
        IOpenWithAppService? openWithAppService = null,
        IFileTagService? fileTagService = null)
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
        _quickLookService = quickLookService;
        _nativeContextMenuService = nativeContextMenuService;
        _clipboardService = clipboardService;
        _launcherService = launcherService;
        _settingsService = settingsService;
        _archiveService = archiveService;
        _dragDropBridge = dragDropBridge;
        _directoryChangeNotifier = directoryChangeNotifier;
        _logger = loggerFactory?.CreateLogger<FileListViewModel>();
        _gitStatusService = gitStatusService;
        _displayNameService = displayNameService;
        _volumeMonitorService = volumeMonitorService;
        _remoteConnectionService = remoteConnectionService;
        _sftpFileService = sftpFileService;
        _remoteFileEditService = remoteFileEditService;
        _openWithAppService = openWithAppService;
        _fileTagService = fileTagService;
        _columnLayoutService = new FileListColumnLayoutService(settingsService);

        // Initialize sidebar names with cheap defaults. macOS localized names are
        // resolved in the background so process launches do not block first paint.
        var home = _fileService.HomeDirectory;
        InitializeSidebarDisplayNames(home);

        // Wire up RenameRequested event from FileOps
        _fileOps.RequestRename += OnFileOpsRenameRequested;

        // Keep a HashSet mirror of SelectedEntries for O(1) lookups during render
        SelectedEntries.CollectionChanged += OnSelectedEntriesCollectionChanged;
        Entries.CollectionChanged += OnEntriesCollectionChanged;

        // Wire up PropertyChanged events from sub-viewmodels to forward notifications
        _navigation.PropertyChanged += OnNavigationPropertyChanged;
        _ai.PropertyChanged += OnAiPropertyChanged;
        _archive.PropertyChanged += OnArchivePropertyChanged;
        _collection.PropertyChanged += OnCollectionPropertyChanged;
        _sortFilter.PropertyChanged += OnSortFilterPropertyChanged;
        _fileOps.PropertyChanged += OnFileOpsPropertyChanged;
        if (_fileTagService != null)
            _fileTagService.TagsChanged += OnTagsChanged;

        // Load persisted user preferences
        if (_settingsService != null)
        {
            IsPreviewPaneVisible = _settingsService.Get(PreviewPaneVisibleSettingKey, false);
            IsMetadataPanelVisible = _settingsService.Get(MetadataPanelVisibleSettingKey, false);
            IsInfoPanelVisible = _settingsService.Get("IsInfoPanelVisible", false);
        }

        _ = LoadSidebarDataDeferredAsync(_startupSidebarCts.Token);

        // Build sidebar items with localized names
        _sidebarFavorites = BuildSidebarFavorites();
        _sidebarLocations = BuildSidebarLocations();
        _sidebarAiItems = BuildSidebarAiItems();

        // Load sidebar visibility settings
        LoadSidebarVisibility();

        // Load collapse states from settings
        IsAiSectionCollapsed = _settingsService?.Get("sidebar_ai_collapsed", false) ?? false;
        IsCollectionsSectionCollapsed = _settingsService?.Get("sidebar_collections_collapsed", false) ?? false;
        IsTagsSectionCollapsed = _settingsService?.Get("sidebar_tags_collapsed", false) ?? false;

        // Initialize external volumes and subscribe to changes
        RefreshExternalVolumes();
        if (_volumeMonitorService != null)
            _volumeMonitorService.VolumesChanged += OnVolumesChanged;

        RefreshLocationStatus();
    }

    internal void UseColumnLayoutService(FileListColumnLayoutService columnLayoutService)
    {
        _columnLayoutService = columnLayoutService;
    }

    partial void OnEntriesChanging(ObservableCollection<FileSystemEntry> value)
    {
        Entries.CollectionChanged -= OnEntriesCollectionChanged;
    }

    partial void OnEntriesChanged(ObservableCollection<FileSystemEntry> value)
    {
        value.CollectionChanged += OnEntriesCollectionChanged;
        OnPropertyChanged(nameof(StatusSummaryText));
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusMessage));
    }

    private void OnEntriesCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(StatusSummaryText));
    }

    private void RefreshLocationStatus()
    {
        if (_navigation.IsRemoteView && !string.IsNullOrWhiteSpace(_navigation.CurrentRemoteServerId))
        {
            var serverId = _navigation.CurrentRemoteServerId;
            var server = _remoteConnectionService?.GetSavedServers()
                .FirstOrDefault(candidate => string.Equals(candidate.Id, serverId, StringComparison.Ordinal));
            var connected = _remoteConnectionService?.IsConnected(serverId) == true;
            var displayName = string.IsNullOrWhiteSpace(server?.DisplayName) ? serverId : server.DisplayName;
            var status = FileListStatusFormatter.GetRemoteLocationStatus(displayName, connected);

            SetLocationRemoteServer(server);
            IsRemoteLocation = true;
            IsRemoteLocationConnected = connected;
            LocationStatusText = status.Text;
            LocationStatusTooltip = status.Tooltip;
            return;
        }

        SetLocationRemoteServer(null);
        IsRemoteLocation = false;
        IsRemoteLocationConnected = false;

        if (_navigation.IsHomePage
            || _navigation.IsAiView
            || _navigation.IsCollectionView
            || _navigation.IsArchiveView
            || IsTagView
            || string.IsNullOrWhiteSpace(_navigation.CurrentPath)
            || VirtualPath.IsRemotePath(_navigation.CurrentPath)
            || AiPathHelper.IsAiPath(_navigation.CurrentPath)
            || TagPathHelper.IsTagPath(_navigation.CurrentPath)
            || ArchivePathHelper.IsArchivePath(_navigation.CurrentPath))
        {
            ClearLocationStatus();
            return;
        }

        try
        {
            var status = FileListStatusFormatter.GetLocalLocationStatus(
                _navigation.CurrentPath,
                GetDriveSpaceSnapshots());
            if (status is { } value)
            {
                LocationStatusText = value.Text;
                LocationStatusTooltip = value.Tooltip;
            }
            else
            {
                ClearLocationStatus();
            }
        }
        catch
        {
            ClearLocationStatus();
        }
    }

    private void SetLocationRemoteServer(RemoteServerInfo? server)
    {
        if (ReferenceEquals(_locationRemoteServer, server))
            return;

        if (_locationRemoteServer != null)
            _locationRemoteServer.PropertyChanged -= OnLocationRemoteServerPropertyChanged;

        _locationRemoteServer = server;
        if (_locationRemoteServer != null)
            _locationRemoteServer.PropertyChanged += OnLocationRemoteServerPropertyChanged;
    }

    private void OnLocationRemoteServerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RemoteServerInfo.IsConnected))
            return;

        if (Dispatcher.UIThread.CheckAccess())
            RefreshLocationStatus();
        else
            Dispatcher.UIThread.Post(RefreshLocationStatus);
    }

    private static IReadOnlyList<DriveSpaceSnapshot> GetDriveSpaceSnapshots()
    {
        var snapshots = new List<DriveSpaceSnapshot>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return snapshots;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                snapshots.Add(new DriveSpaceSnapshot(
                    drive.RootDirectory.FullName,
                    drive.AvailableFreeSpace,
                    drive.TotalSize));
            }
            catch
            {
                // A volume can disappear while DriveInfo is being queried.
            }
        }

        return snapshots;
    }

    private void ClearLocationStatus()
    {
        LocationStatusText = string.Empty;
        LocationStatusTooltip = string.Empty;
    }

    private void InitializeSidebarDisplayNames(string home)
    {
        UserName = Path.GetFileName(home);
        DesktopName = "桌面";
        DocumentsName = "文稿";
        DownloadsName = "下载";
        PicturesName = "图片";
        MusicName = "音乐";
        ApplicationsName = "应用程序";
        VolumeName = "Macintosh HD";
        TrashName = "废纸篓";

        if (_displayNameService != null)
            _ = LoadLocalizedSidebarDisplayNamesAsync(home, _displayNameService);
    }

    private async Task LoadSidebarDataDeferredAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(
                async () =>
                {
                    if (cancellationToken.IsCancellationRequested || _disposed) return;
                    await _collection.LoadCollectionsAsync();
                    if (cancellationToken.IsCancellationRequested || _disposed) return;
                    await _collection.LoadPinnedFoldersAsync();
                    if (cancellationToken.IsCancellationRequested || _disposed) return;
                    await LoadSidebarTagsAsync(cancellationToken);
                },
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load sidebar data during startup");
        }
    }

    private async Task LoadSidebarTagsAsync(CancellationToken cancellationToken = default)
    {
        if (_fileTagService == null)
        {
            ReplaceSidebarTags(FileTagCatalog.FinderColors);
            return;
        }

        try
        {
            var tags = await _fileTagService.GetSidebarTagsAsync(cancellationToken);
            ReplaceSidebarTags(tags);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load sidebar tags");
            ReplaceSidebarTags(FileTagCatalog.FinderColors);
        }
    }

    private void ReplaceSidebarTags(IEnumerable<FileTag> tags)
    {
        SidebarTags.Clear();
        foreach (var tag in tags)
            SidebarTags.Add(tag);
    }

    private void OnTagsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed) return;
            await LoadSidebarTagsAsync();
            if (IsTagView)
                await RefreshAsync();
        }, DispatcherPriority.Background);
    }

    private async Task LoadLocalizedSidebarDisplayNamesAsync(string home, IDisplayNameService displayNameService)
    {
        try
        {
            var names = await Task.Run(() =>
            {
                var trashDisplayName = displayNameService.GetDisplayName(Path.Combine(home, ".Trash"));
                if (string.IsNullOrEmpty(trashDisplayName) || trashDisplayName.StartsWith(".", StringComparison.Ordinal))
                    trashDisplayName = "废纸篓";

                return new SidebarDisplayNames(
                    ValueOrFallback(displayNameService.GetUserName(), Path.GetFileName(home)),
                    LocalizedOrFallback(displayNameService.GetDisplayName(home + "/Desktop"), "桌面", home + "/Desktop"),
                    LocalizedOrFallback(displayNameService.GetDisplayName(home + "/Documents"), "文稿", home + "/Documents"),
                    LocalizedOrFallback(displayNameService.GetDisplayName(home + "/Downloads"), "下载", home + "/Downloads"),
                    LocalizedOrFallback(displayNameService.GetDisplayName(home + "/Pictures"), "图片", home + "/Pictures"),
                    LocalizedOrFallback(displayNameService.GetDisplayName(home + "/Music"), "音乐", home + "/Music"),
                    LocalizedOrFallback(displayNameService.GetDisplayName("/Applications"), "应用程序", "/Applications"),
                    ValueOrFallback(displayNameService.GetDisplayName("/"), "Macintosh HD"),
                    ValueOrFallback(trashDisplayName, "废纸篓"));
            });

            await Dispatcher.UIThread.InvokeAsync(() => ApplySidebarDisplayNames(names));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load localized sidebar display names");
        }
    }

    private static string ValueOrFallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string LocalizedOrFallback(string? value, string fallback, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var fileName = Path.GetFileName(path);
        return string.Equals(value, fileName, StringComparison.Ordinal) ? fallback : value;
    }

    private void ApplySidebarDisplayNames(SidebarDisplayNames names)
    {
        var favoritesChanged = false;
        var locationsChanged = false;
        if (UserName != names.UserName) { UserName = names.UserName; favoritesChanged = true; OnPropertyChanged(nameof(UserName)); }
        if (DesktopName != names.DesktopName) { DesktopName = names.DesktopName; favoritesChanged = true; OnPropertyChanged(nameof(DesktopName)); }
        if (DocumentsName != names.DocumentsName) { DocumentsName = names.DocumentsName; favoritesChanged = true; OnPropertyChanged(nameof(DocumentsName)); }
        if (DownloadsName != names.DownloadsName) { DownloadsName = names.DownloadsName; favoritesChanged = true; OnPropertyChanged(nameof(DownloadsName)); }
        if (PicturesName != names.PicturesName) { PicturesName = names.PicturesName; favoritesChanged = true; OnPropertyChanged(nameof(PicturesName)); }
        if (MusicName != names.MusicName) { MusicName = names.MusicName; favoritesChanged = true; OnPropertyChanged(nameof(MusicName)); }
        if (ApplicationsName != names.ApplicationsName) { ApplicationsName = names.ApplicationsName; locationsChanged = true; OnPropertyChanged(nameof(ApplicationsName)); }
        if (VolumeName != names.VolumeName) { VolumeName = names.VolumeName; locationsChanged = true; OnPropertyChanged(nameof(VolumeName)); }
        if (TrashName != names.TrashName) { TrashName = names.TrashName; locationsChanged = true; OnPropertyChanged(nameof(TrashName)); }

        if (favoritesChanged)
        {
            _sidebarFavorites = BuildSidebarFavorites();
            OnPropertyChanged(nameof(SidebarFavorites));
        }
        if (locationsChanged)
        {
            _sidebarLocations = BuildSidebarLocations();
            OnPropertyChanged(nameof(SidebarLocations));
        }
    }

    private sealed record SidebarDisplayNames(
        string UserName,
        string DesktopName,
        string DocumentsName,
        string DownloadsName,
        string PicturesName,
        string MusicName,
        string ApplicationsName,
        string VolumeName,
        string TrashName);

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
            or nameof(NavigationViewModel.IsRemoteView)
            or nameof(NavigationViewModel.CurrentRemoteServerId)
            or nameof(NavigationViewModel.IsSearchMode)
            or nameof(NavigationViewModel.SearchQuery)
            or nameof(NavigationViewModel.Breadcrumbs))
        {
            OnPropertyChanged(e.PropertyName);
        }

        if (e.PropertyName == nameof(NavigationViewModel.CurrentPath))
        {
            StatusText = string.Empty;
            OnPropertyChanged(nameof(IsTagView));
            OnPropertyChanged(nameof(CurrentTag));
        }

        if (e.PropertyName is nameof(NavigationViewModel.CurrentPath)
            or nameof(NavigationViewModel.IsHomePage)
            or nameof(NavigationViewModel.Breadcrumbs))
        {
            OnPropertyChanged(nameof(CurrentLocationTitle));
        }

        if (e.PropertyName is nameof(NavigationViewModel.CurrentPath)
            or nameof(NavigationViewModel.IsHomePage)
            or nameof(NavigationViewModel.IsArchiveView)
            or nameof(NavigationViewModel.IsCollectionView)
            or nameof(NavigationViewModel.IsAiView)
            or nameof(NavigationViewModel.IsRemoteView)
            or nameof(NavigationViewModel.CurrentRemoteServerId))
        {
            RefreshLocationStatus();
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
            or nameof(ArchiveViewModel.PendingCompressOptions))
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
            or nameof(SortFilterViewModel.HideSystemFiles)
            or nameof(SortFilterViewModel.HideDotFiles)
            or nameof(SortFilterViewModel.HideDotFolders))
        {
            OnPropertyChanged(e.PropertyName);
        }

        // Re-apply sort/group/filter when sort, group, or hide settings change
        if (e.PropertyName is nameof(SortFilterViewModel.SortField)
            or nameof(SortFilterViewModel.SortAscending)
            or nameof(SortFilterViewModel.GroupField)
            or nameof(SortFilterViewModel.HideSystemFiles)
            or nameof(SortFilterViewModel.HideDotFiles)
            or nameof(SortFilterViewModel.HideDotFolders))
        {
            _sortFilter.ApplySortAndGroup(sortedEntries => Entries = sortedEntries);
        }
    }

    private void OnFileOpsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileOpsViewModel.CutPaths))
        {
            OnPropertyChanged(nameof(CutPaths));
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
            if (IsRemoteView)
                await RefreshRemoteDirectoryAsync();
            else
                await LoadDirectoryContentsAsync(forceRefresh: true);
        }
        finally { _isRefreshingFromNotification = false; }
    }

    // ── Navigation Commands ──

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        CaptureCurrentNavigationViewState();
        if (_navigation.IsSearchMode)
            _search.Reset();

        if (VirtualPath.IsHomePath(path))
        {
            GoHome();
            return;
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

        // Intercept tag virtual paths.
        if (TagPathHelper.IsTagPath(path))
        {
            if (string.Equals(_navigation.CurrentPath, path, StringComparison.Ordinal) && Entries.Count > 0)
                await RefreshAsync();
            else
                await HandleTagNavigationAsync(path);
            return;
        }

        // Intercept remote server paths
        if (VirtualPath.IsRemotePath(path))
        {
            await NavigateToRemoteAsync(path);
            return;
        }

        // Reset AI runtime state (NavigationViewModel.NavigateToAsync handles flag reset)
        _ai.Reset();
        _navigation.IsSearchMode = false;
        _navigation.SearchQuery = string.Empty;

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

        if (_navigation.CurrentPath == path && Entries.Count > 0
            && string.IsNullOrEmpty(PendingSelectFileName)
            && !_navigation.IsCollectionView && !_navigation.IsAiView && !_navigation.IsArchiveView && !_navigation.IsRemoteView) return;

        // Keep the current directory visually stable while the next directory is
        // produced in the background. Showing the loading overlay over an existing
        // list paints an unnecessary intermediate frame before the first new batch.
        var showLoadingOverlay = Entries.Count == 0;
        if (showLoadingOverlay)
            IsLoading = true;

        CancelQueuedMetadataLoad();
        _metadataLoadGeneration++;
        _metadataLoadPath = null;
        _metadataLoadTask = null;
        CurrentMetadata = null;

        _navigation.IsHomePage = false;

        await _navigation.NavigateToAsync(path);

        try
        {
            await LoadDirectoryContentsAsync();
        }
        catch (Exception ex) { StatusText = $"无法访问: {ex.Message}"; }
        finally
        {
            if (showLoadingOverlay)
                IsLoading = false;
        }

        if (string.Equals(_navigation.CurrentPath, path, StringComparison.Ordinal))
        {
            _navigation.SetWatchedDirectory(path);
            _settingsService?.Set(LastDirectorySettingKey, path);
        }
    }

    public async Task RevealFileAsync(FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            await NavigateToAsync(entry.FullPath);
            return;
        }

        var parentPath = Path.GetDirectoryName(entry.FullPath);
        if (string.IsNullOrEmpty(parentPath) || !Directory.Exists(parentPath))
            return;

        var canReuseCurrentDirectory =
            string.Equals(_navigation.CurrentPath, parentPath, StringComparison.Ordinal)
            && !_navigation.IsSearchMode
            && !_navigation.IsCollectionView
            && !_navigation.IsAiView
            && !_navigation.IsArchiveView
            && !_navigation.IsRemoteView;
        if (canReuseCurrentDirectory)
        {
            var target = Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FullPath, entry.FullPath, StringComparison.Ordinal));
            if (target != null)
            {
                SetSelection([target], target);
                ScrollToSelectionRequested?.Invoke();
                return;
            }
        }

        PendingSelectFileName = entry.Name;
        ScrollBehaviorAfterLoad = ScrollMode.ScrollToSelected;
        await NavigateToAsync(parentPath);
    }

    [RelayCommand]
    public async Task NavigateUpAsync()
    {
        if (IsHomePage) return;

        if (IsTagView)
        {
            GoHome();
            return;
        }

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
        CaptureCurrentNavigationViewState();
        ScrollBehaviorAfterLoad = ScrollMode.RestoreNavigation;
        await _navigation.NavigateBackAsync();
        await ReloadAfterHistoryNavigation();
    }

    [RelayCommand]
    public async Task NavigateForwardAsync()
    {
        if (!_navigation.CanGoForward) return;
        CaptureCurrentNavigationViewState();
        ScrollBehaviorAfterLoad = ScrollMode.ResetToTop;
        await _navigation.NavigateForwardAsync();
        await ReloadAfterHistoryNavigation();
    }

    private async Task ReloadAfterHistoryNavigation()
    {
        try
        {
            if (_navigation.CurrentHistoryEntry is { IsSearchMode: true } searchEntry)
            {
                RestoreSearchHistoryEntry(searchEntry);
                return;
            }

            var path = _navigation.CurrentPath;
            if (ArchivePathHelper.IsArchivePath(path))
            {
                await NavigateToArchiveAsync(path);
            }
            else if (AiPathHelper.IsAiPath(path))
            {
                await HandleAiNavigationAsync(path);
            }
            else if (TagPathHelper.IsTagPath(path))
            {
                await HandleTagNavigationAsync(path);
            }
            else if (VirtualPath.IsRemotePath(path))
            {
                await NavigateToRemoteAsync(path);
            }
            else
            {
                // Returning to a normal directory — reset special view flags
                _navigation.IsArchiveView = false;
                _navigation.IsAiView = false;
                _navigation.IsCollectionView = false;
                _navigation.IsRemoteView = false;
                _navigation.CurrentArchivePath = null;
                _navigation.CurrentArchiveInternalPath = "";
                _navigation.CurrentCollectionId = null;
                _navigation.CurrentCollectionName = null;
                _navigation.CurrentFaceClusterId = null;
                _navigation.CurrentAiContextLabel = null;
                _navigation.CurrentRemoteServerId = null;
                _ai.Reset();
                _navigation.UpdateBreadcrumbs();
                IsLoading = true;
                try
                {
                    await LoadDirectoryContentsAsync();
                    if (string.Equals(_navigation.CurrentPath, path, StringComparison.Ordinal))
                    {
                        _navigation.SetWatchedDirectory(path);
                        _settingsService?.Set(LastDirectorySettingKey, path);
                    }
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
        CaptureCurrentNavigationViewState();
        CancelDirectoryWork();
        _navigation.GoHome();
        _settingsService?.Set(LastDirectorySettingKey, "");
        _search.Reset();
        _ai.Reset();
        Entries.Clear();
        StatusText = "";
        SelectedEntries.Clear();
    }

    [RelayCommand]
    public async Task OpenEntryAsync(FileSystemEntry entry)
    {
        _logger?.LogDebug("[OpenEntry] Called: path={Path}, isDir={IsDir}, isVirtual={IsVirtual}, iconKey={IconKey}, isArchiveView={IsArchiveView}",
            entry.FullPath, entry.IsDirectory, entry.IsVirtual, entry.IconKey, IsArchiveView);

        // Virtual AI folder: navigate into AI detail
        if (IsTrashActive)
        {
            ContextMenuActions = new ObservableCollection<ContextMenuAction>(BuildTrashFileContextMenu(entry));
        }
        else if (entry.IsVirtual)
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
                var tempFile = await _archive.ExtractEntryToTempWithPasswordAsync(
                    archPath, entryKey, PromptPasswordAsync);
                if (tempFile != null)
                    await _launcherService.OpenFileAsync(tempFile);
            }
            catch (Exception ex)
            {
                StatusText = $"打开文件失败: {ex.Message}";
            }
            return;
        }

        // Remote view: directory -> navigate deeper
        if (IsRemoteView && entry.IsDirectory)
        {
            await NavigateToAsync(entry.FullPath);
            return;
        }

        // Remote view: file -> download to temp and open
        if (IsRemoteView && !entry.IsDirectory)
        {
            if (_remoteFileEditService == null || _remoteConnectionService == null) return;
            try
            {
                var (serverId, remotePath) = VirtualPath.ParseRemotePath(entry.FullPath);
                var localPath = await _remoteFileEditService.DownloadForEditAsync(remotePath, serverId);
                _remoteFileEditService.WatchForChanges(localPath, remotePath, serverId);
                if (_launcherService != null)
                    await _launcherService.OpenFileAsync(localPath);
            }
            catch (Exception ex)
            {
                StatusText = $"打开文件失败: {ex.Message}";
            }
            return;
        }

        if (IsTagView && !File.Exists(entry.FullPath) && !Directory.Exists(entry.FullPath))
        {
            StatusText = $"项目不存在，已刷新: {entry.Name}";
            await RefreshAsync();
            return;
        }

        if (!IsCollectionView && !IsTagView && !IsRemoteView && !File.Exists(entry.FullPath) && !Directory.Exists(entry.FullPath))
        {
            StatusText = $"项目不存在，已刷新: {entry.Name}";
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
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
        {
            _logger?.LogDebug("[OpenEntry] Opening file via launcherService: {Path}", entry.FullPath);
            await _launcherService.OpenFileAsync(entry.FullPath);
        }
        else
        {
            _logger?.LogWarning("[OpenEntry] _launcherService is null, cannot open file");
        }
    }

    // ── Archive Navigation ──

    private async Task NavigateToArchiveAsync(string sentinelPath)
    {
        var (archivePath, internalPath) = ArchivePathHelper.Parse(sentinelPath);

        var opened = await _archive.NavigateToArchiveAsync(
            archivePath,
            internalPath,
            path =>
            {
                CancelDirectoryWork();
                _navigation.SetWatchedDirectory(null);
                _navigation.IsHomePage = false;
                _navigation.IsCollectionView = false;
                _navigation.IsArchiveView = true;
                _navigation.IsAiView = false;
                _navigation.IsRemoteView = false;
                _ai.Reset();
                _navigation.IsSearchMode = false;
                _navigation.CurrentArchivePath = archivePath;
                _navigation.CurrentArchiveInternalPath = internalPath;
                _navigation.CurrentPath = path;
            },
            () => _navigation.UpdateBreadcrumbsForArchive(),
            entries => ApplyEntries(entries),
            msg => StatusText = msg,
            loading => IsLoading = loading,
            async () =>
            {
                var password = await PromptPasswordAsync();
                return password;
            }
        );

        if (opened)
            _navigation.UpdateHistoryForSentinelPath(sentinelPath);
    }

    // ── Remote Server Navigation ──

    public bool IsRemoteView => _navigation.IsRemoteView;
    public string? CurrentRemoteServerId => _navigation.CurrentRemoteServerId;

    private async Task NavigateToRemoteAsync(string sentinelPath)
    {
        if (_remoteConnectionService == null || _sftpFileService == null)
        {
            StatusText = "远程连接服务未初始化";
            return;
        }

        var (serverId, remotePath) = VirtualPath.ParseRemotePath(sentinelPath);

        if (string.IsNullOrEmpty(PendingSelectFileName))
            ScrollBehaviorAfterLoad = ScrollMode.ResetToTop;

        var work = BeginDirectoryWork();
        var selectionState = CaptureEntryLoadSelectionState();
        _navigation.SetWatchedDirectory(null);
        _navigation.IsHomePage = false;
        _navigation.IsCollectionView = false;
        _navigation.IsArchiveView = false;
        _navigation.IsAiView = false;
        _navigation.IsRemoteView = true;
        _navigation.CurrentRemoteServerId = serverId;
        _ai.Reset();
        _navigation.IsSearchMode = false;

        IsLoading = true;
        _navigation.CurrentPath = sentinelPath;
        _navigation.UpdateBreadcrumbsForRemote(remotePath);

        try
        {
            if (!_remoteConnectionService.IsConnected(serverId))
            {
                if (IsCurrentDirectoryWork(work))
                    StatusText = "服务器未连接";
                return;
            }

            _sftpFileService.SetCurrentServer(serverId);
            await StreamDirectoryEntriesAsync(
                _sftpFileService,
                remotePath,
                work,
                selectionState);
            if (!IsCurrentDirectoryWork(work)) return;
            RefreshLocationStatus();
            _navigation.UpdateHistoryForSentinelPath(sentinelPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrentDirectoryWork(work))
                StatusText = $"无法访问远程目录: {ex.Message}";
            _logger?.LogError(ex, "Failed to navigate to remote path {Path}", sentinelPath);
        }
        finally
        {
            if (IsCurrentDirectoryWork(work))
                IsLoading = false;
        }
    }

    private async Task RefreshRemoteDirectoryAsync(bool showLoadingOverlay = false)
    {
        if (_remoteConnectionService == null || _sftpFileService == null
            || string.IsNullOrEmpty(_navigation.CurrentRemoteServerId)
            || !VirtualPath.IsRemotePath(_navigation.CurrentPath))
            return;

        var (serverId, remotePath) = VirtualPath.ParseRemotePath(_navigation.CurrentPath);
        if (!string.Equals(serverId, _navigation.CurrentRemoteServerId, StringComparison.Ordinal))
            return;

        var work = BeginDirectoryWork();
        var selectionState = CaptureEntryLoadSelectionState();
        if (showLoadingOverlay)
            IsLoading = true;
        try
        {
            if (!_remoteConnectionService.IsConnected(serverId))
            {
                if (IsCurrentDirectoryWork(work))
                    StatusText = "服务器未连接";
                return;
            }

            _sftpFileService.SetCurrentServer(serverId);
            await StreamDirectoryEntriesAsync(
                _sftpFileService,
                remotePath,
                work,
                selectionState);
            if (IsCurrentDirectoryWork(work))
                RefreshLocationStatus();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (showLoadingOverlay && IsCurrentDirectoryWork(work))
                IsLoading = false;
        }
    }

    public async Task ConnectToServerAsync(RemoteServerInfo server)
    {
        if (_remoteConnectionService == null) return;
        try
        {
            await _remoteConnectionService.ConnectAsync(server);
            var remotePath = VirtualPath.BuildRemotePath(server.Id, server.DefaultPath);
            await NavigateToAsync(remotePath);
        }
        catch (Exception ex)
        {
            StatusText = $"连接失败: {ex.Message}";
        }
    }

    public void DisconnectServer(string serverId)
    {
        _remoteConnectionService?.Disconnect(serverId);
        if (_navigation.CurrentRemoteServerId == serverId)
            GoHome();
    }

    // ── AI Navigation ──

    private async Task HandleAiNavigationAsync(string sentinelPath)
    {
        CancelDirectoryWork();
        _navigation.SetWatchedDirectory(null);
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
            entries => { ApplyEntries(entries); ResolveRealEntries(entries); },
            msg => StatusText = msg,
            loading => IsLoading = loading,
            () => OnPropertyChanged(nameof(Entries))
        );

        _navigation.UpdateHistoryForSentinelPath(sentinelPath);
    }

    // ── Refresh ──

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsHomePage) return;

        if (IsRemoteView)
        {
            var showRemoteLoadingOverlay = Entries.Count == 0;
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            try
            {
                await RefreshRemoteDirectoryAsync(showRemoteLoadingOverlay);
            }
            catch (Exception ex)
            {
                StatusText = $"刷新失败: {ex.Message}";
            }
            return;
        }

        if (IsTagView)
        {
            await HandleTagNavigationAsync(_navigation.CurrentPath, recordHistory: false);
            return;
        }

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
                    loading => { },
                    PromptPasswordAsync
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
                        await _ai.SearchAiTagsAsync(_search.SearchQuery, entries => { ApplyEntries(entries); ResolveRealEntries(entries); }, msg => StatusText = msg);
                    else
                        await _ai.LoadAiTopLevelAsync(info.Mode, entries => { ApplyEntries(entries); ResolveRealEntries(entries); }, msg => StatusText = msg, () => OnPropertyChanged(nameof(Entries)));
                }
                else if (info.IsFaceDetail)
                    await _ai.LoadFaceClusterEntriesAsync(info.FaceClusterId!.Value, entries => { ApplyEntries(entries); ResolveRealEntries(entries); }, msg => StatusText = msg);
                else
                    await _ai.LoadAiCategoryEntriesAsync(info.TagType!, info.TagValue!, entries => { ApplyEntries(entries); ResolveRealEntries(entries); }, msg => StatusText = msg);
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
                entries => { ApplyEntries(entries); ResolveRealEntries(entries); },
                msg => StatusText = msg,
                () => _navigation.UpdateBreadcrumbs(),
                folders => { }
            );
            return;
        }

        var showLoadingOverlay = Entries.Count == 0;
        if (showLoadingOverlay)
            IsLoading = true;
        ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
        try { await LoadDirectoryContentsAsync(forceRefresh: true); }
        finally
        {
            if (showLoadingOverlay)
                IsLoading = false;
        }
    }

    // ── Selection ──

    private IReadOnlyList<FileSystemEntry> GetSelectableEntries() =>
        GroupField != GroupField.None
            ? Groups.SelectMany(g => g.Entries).ToList()
            : Entries.ToList();

    private static int FindEntryIndexByPath(IReadOnlyList<FileSystemEntry> list, string fullPath)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].FullPath, fullPath, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private void SetSelectionAnchor(FileSystemEntry entry)
    {
        _lastClickedEntry = entry;
        _lastClickedPath = entry.FullPath;
    }

    private void OnSelectedEntriesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (FileSystemEntry item in e.NewItems) _selectedEntriesSet.Add(item);
        if (e.OldItems != null)
            foreach (FileSystemEntry item in e.OldItems) _selectedEntriesSet.Remove(item);
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            _selectedEntriesSet.Clear();

        UpdateEntrySelectionFlags();
        OnPropertyChanged(nameof(StatusSummaryText));

        if (IsSelectionPreviewSuppressed)
            return;

        if (IsInfoPanelVisible)
        {
            if (SelectedEntries.Count == 1)
                QueueMetadataLoad(SelectedEntries[0]);
            else
            {
                CancelQueuedMetadataLoad();
                _metadataLoadGeneration++;
                _metadataLoadPath = null;
                _metadataLoadTask = null;
                CurrentMetadata = null;
            }
        }
    }

    private void UpdateEntrySelectionFlags()
    {
        var selected = SelectedEntries.ToHashSet();

        foreach (var entry in _selectionFlaggedEntries.ToArray())
        {
            if (selected.Contains(entry))
                continue;

            entry.IsSelected = false;
            _selectionFlaggedEntries.Remove(entry);
        }

        foreach (var entry in selected)
        {
            entry.IsSelected = true;
            _selectionFlaggedEntries.Add(entry);
        }
    }

    /// <summary>
    /// Updates the shared selection collection in place so Avalonia views keep
    /// their CollectionChanged subscriptions and refresh preview/details state.
    /// </summary>
    private void ReplaceSelection(IEnumerable<FileSystemEntry> entries, FileSystemEntry? anchor = null)
    {
        var replacement = entries.Distinct().ToList();
        if (SelectedEntries.Count == replacement.Count)
        {
            for (var i = 0; i < replacement.Count; i++)
            {
                if (!ReferenceEquals(SelectedEntries[i], replacement[i]))
                    SelectedEntries[i] = replacement[i];
            }
        }
        else
        {
            SelectedEntries.Clear();
            foreach (var entry in replacement)
                SelectedEntries.Add(entry);
        }

        if (anchor != null)
            SetSelectionAnchor(anchor);
        else if (replacement.Count == 0)
        {
            _lastClickedEntry = null;
            _lastClickedPath = null;
        }
    }

    public bool IsEntrySelected(FileSystemEntry entry) => _selectedEntriesSet.Contains(entry);

    public bool IsSelectionPreviewSuppressed => _selectionPreviewSuppressionDepth > 0;

    public void NotifyTransientInteractionStarted()
        => TransientInteractionStarted?.Invoke();

    public void SetSelection(IEnumerable<FileSystemEntry> entries, FileSystemEntry? anchor = null)
    {
        ReplaceSelection(entries, anchor);
    }

    public void SelectEntryForContextMenu(FileSystemEntry entry)
    {
        _selectionPreviewSuppressionDepth++;
        try
        {
            SelectEntry(entry);
        }
        finally
        {
            _selectionPreviewSuppressionDepth--;
        }
    }

    public void SelectEntry(FileSystemEntry entry, bool cmdKey = false, bool shiftKey = false)
    {
        if (shiftKey && _lastClickedPath != null)
        {
            var list = GetSelectableEntries();
            var startIdx = FindEntryIndexByPath(list, _lastClickedPath);
            var endIdx = FindEntryIndexByPath(list, entry.FullPath);
            if (startIdx < 0 || endIdx < 0) return;

            if (startIdx > endIdx) (startIdx, endIdx) = (endIdx, startIdx);

            var range = new List<FileSystemEntry>(endIdx - startIdx + 1);
            for (int i = startIdx; i <= endIdx; i++)
                range.Add(list[i]);

            if (cmdKey)
            {
                // Cmd+Shift: 把范围追加到现有选择中（不创建新集合）
                foreach (var e in range)
                {
                    if (!_selectedEntriesSet.Contains(e))
                    {
                        _selectedEntriesSet.Add(e);
                        SelectedEntries.Add(e);
                    }
                }
            }
            else
            {
                ReplaceSelection(range, list[startIdx]);
            }
        }
        else if (cmdKey)
        {
            if (_selectedEntriesSet.Contains(entry))
            {
                _selectedEntriesSet.Remove(entry);
                SelectedEntries.Remove(entry);
            }
            else
            {
                _selectedEntriesSet.Add(entry);
                SelectedEntries.Add(entry);
            }
            SetSelectionAnchor(entry);
        }
        else
        {
            if (SelectedEntries.Count == 1 && SelectedEntries[0] == entry)
                return;

            if (SelectedEntries.Count == 1)
            {
                // 使用 Replace（单事件）代替 Clear+Add（两个事件）
                _selectedEntriesSet.Remove(SelectedEntries[0]);
                SelectedEntries[0] = entry;
                _selectedEntriesSet.Add(entry);
            }
            else
            {
                ReplaceSelection([entry], entry);
            }
        }
    }

    public void SelectAll()
    {
        var selectable = GetSelectableEntries();
        if (selectable.Count == 0)
        {
            ClearSelection();
            return;
        }
        ReplaceSelection(selectable, selectable[0]);
    }

    [RelayCommand]
    public void ClearSelection()
    {
        if (SelectedEntries.Count == 0) return;

        SelectedEntries.Clear();

        _lastClickedEntry = null;
        _lastClickedPath = null;
    }

    // ── Context Menu ──

    public async Task ShowFileContextMenuAsync(FileSystemEntry entry, double x, double y)
    {
        _ = Task.Run(ActivateAppWindow);

        ContextMenuEntry = entry;
        ContextMenuX = x;
        ContextMenuY = y;

        if (entry.IsVirtual)
        {
            var actions = new List<ContextMenuAction>
            {
                new() { Label = "打开", IconSvg = Icons.Open, Execute = () => OpenEntryCommand.ExecuteAsync(entry) }
            };
            if (entry.VirtualFolderType == "face")
            {
                actions.Add(new ContextMenuAction { Label = "重命名", IconSvg = Icons.Rename, Execute = () => { _fileOps.RaiseRequestRename(entry); return Task.CompletedTask; } });
            }
            ContextMenuActions = new ObservableCollection<ContextMenuAction>(actions);
        }
        else if (IsArchiveView)
        {
            ContextMenuActions = new ObservableCollection<ContextMenuAction>(new[]
            {
                new ContextMenuAction { Label = "打开", IconSvg = Icons.Open, Execute = () => OpenEntryCommand.ExecuteAsync(entry) }
            });
        }
        else
        {
            ContextMenuActions = new ObservableCollection<ContextMenuAction>(
                await BuildFileContextMenuAsync(entry, includeDynamicActions: false));
        }

        IsContextMenuVisible = true;
    }

    public async Task ShowBackgroundContextMenuAsync(double x, double y)
    {
        _ = Task.Run(ActivateAppWindow);

        ContextMenuEntry = null;
        ContextMenuX = x;
        ContextMenuY = y;

        if (IsTrashActive)
        {
            ContextMenuActions = new ObservableCollection<ContextMenuAction>(BuildTrashBackgroundContextMenu());
        }
        else if (IsArchiveView)
        {
            ContextMenuActions = new ObservableCollection<ContextMenuAction>(new[]
            {
                new ContextMenuAction { Label = "刷新", IconSvg = Icons.Refresh, Execute = () => RefreshCommand.ExecuteAsync(null) }
            });
        }
        else if (IsCollectionView || IsAiView || IsTagView)
        {
            ContextMenuActions = new ObservableCollection<ContextMenuAction>();
        }
        else
        {
            ContextMenuActions = new ObservableCollection<ContextMenuAction>(await BuildBackgroundContextMenuAsync());
        }

        IsContextMenuVisible = true;
    }

    // ── Direct menu builders (no service dependency) ──────────────

    public async Task<IReadOnlyList<ContextMenuAction>> LoadCompleteFileContextMenuAsync(FileSystemEntry entry)
        => await BuildFileContextMenuAsync(entry, includeDynamicActions: true);

    private async Task<List<ContextMenuAction>> BuildFileContextMenuAsync(
        FileSystemEntry entry,
        bool includeDynamicActions)
    {
        var actions = new List<ContextMenuAction>();
        var isRemote = VirtualPath.IsRemotePath(entry.FullPath);

        // Open
        var opensInsideMacExplorer = entry.IsDirectory
            || _archiveService?.IsArchiveFile(entry.FullPath) == true;
        actions.Add(new ContextMenuAction
        {
            Label = "打开",
            IconSvg = Icons.Open,
            LoadIconBase64Async = !isRemote && !opensInsideMacExplorer && _contextMenuService != null
                ? () => _contextMenuService.GetDefaultApplicationIconBase64Async(entry.FullPath)
                : null,
            Execute = () => OpenEntryCommand.ExecuteAsync(entry)
        });

        if (_contextMenuService != null && includeDynamicActions)
        {
            var openWith = isRemote
                ? await BuildOpenWithActionsForRemoteAsync(entry)
                : await _contextMenuService.GetOpenWithActionsAsync(entry.FullPath);
            if (openWith.Count > 0)
            {
                actions.Add(new ContextMenuAction
                {
                    Label = "用…打开",
                    IconSvg = AppIcons.Apps,
                    SubItems = openWith
                });
            }
        }
        else if (_contextMenuService != null)
        {
            actions.Add(new ContextMenuAction
            {
                Label = "用…打开",
                IconSvg = AppIcons.Apps,
                SubItems =
                [
                    new ContextMenuAction { Label = "正在加载…", IsEnabled = false }
                ]
            });
        }

        if (entry.IsDirectory && entry.IconKey == "app-bundle" && !isRemote)
        {
            actions.Add(new ContextMenuAction { Label = "显示包内容", IconSvg = Icons.Folder, Execute = () => NavigateToAsync(entry.FullPath) });
        }

        actions.Add(ContextMenuAction.Separator);

        // File operations
        actions.Add(new ContextMenuAction { Label = "拷贝", IconSvg = Icons.Copy, ShortcutText = "⌘C", IsQuickAction = true, Execute = () => { CopySelected(); return Task.CompletedTask; } });
        actions.Add(new ContextMenuAction { Label = "剪切", IconSvg = Icons.Cut, ShortcutText = "⌘X", IsQuickAction = true, Execute = () => { CutSelected(); return Task.CompletedTask; } });

        actions.Add(new ContextMenuAction { Label = "重命名", IconSvg = Icons.Rename, ShortcutText = "↩", IsQuickAction = true, Execute = () => { _fileOps.RaiseRequestRename(entry); return Task.CompletedTask; } });

        // Batch rename (available when multiple items are selected)
        if (SelectedEntries.Count > 1)
        {
            actions.Add(new ContextMenuAction { Label = "批量重命名", IconSvg = Icons.Rename, Execute = () => { RaiseRequestBatchRename(); return Task.CompletedTask; } });
        }

        var contextEntries = GetContextEntries(entry);
        var canUseLocalFileTools = !isRemote
            && !entry.IsVirtual
            && !IsArchiveView
            && contextEntries.All(IsUsableLocalEntry);

        actions.Add(BuildMoveToContextMenuAction(entry, canUseLocalFileTools));

        actions.Add(new ContextMenuAction
        {
            Label = "删除",
            IconSvg = Icons.Delete,
            ShortcutText = "⌘⌫",
            IsQuickAction = true,
            Execute = () =>
        {
            if (!_selectedEntriesSet.Contains(entry)) { ClearSelection(); SelectedEntries.Add(entry); }
            ShowDeleteConfirmDialogCommand.Execute(null);
            return Task.CompletedTask;
        }
        });

        actions.Add(new ContextMenuAction
        {
            Label = "永久删除",
            IconSvg = Icons.Trash,
            IsEnabled = canUseLocalFileTools,
            Execute = () => PermanentlyDeleteContextEntriesAsync(entry)
        });

        actions.Add(new ContextMenuAction
        {
            Label = "哈希校验",
            IconSvg = Icons.Info,
            IsEnabled = canUseLocalFileTools && contextEntries.All(item => !item.IsDirectory),
            Execute = () => CopySha256HashesAsync(entry)
        });

        actions.Add(ContextMenuAction.Separator);

        // Archive (skip for remote paths)
        if (!isRemote)
        {
            if (_archiveService?.IsArchiveFile(entry.FullPath) == true)
                actions.Add(BuildExtractContextMenuAction(entry));
            else
                actions.Add(new ContextMenuAction { Label = "压缩", IconSvg = AppIcons.Compress, Execute = () => { ShowCompressDialog(); return Task.CompletedTask; } });
        }

        actions.Add(ContextMenuAction.Separator);
        actions.Add(new ContextMenuAction
        {
            Label = "复制路径",
            IconSvg = Icons.CopyPath,
            ShortcutText = "⇧⌘C",
            Execute = () => _clipboardService?.CopyTextAsync(entry.FullPath) ?? Task.CompletedTask
        });

        // Finder/Terminal actions (skip for remote paths)
        if (!isRemote)
        {
            actions.Add(ContextMenuAction.Separator);
            if (_launcherService != null)
            {
                actions.Add(new ContextMenuAction
                {
                    Label = "在 Finder 中显示",
                    IconSvg = Icons.Finder,
                    LoadIconBase64Async = _openWithAppService == null
                        ? null
                        : () => _openWithAppService.GetAppIconBase64ByPathAsync("/System/Library/CoreServices/Finder.app"),
                    Execute = () => _launcherService.RevealInFinderAsync(entry.FullPath)
                });
                var terminalPath = entry.IsDirectory ? entry.FullPath : Path.GetDirectoryName(entry.FullPath) ?? entry.FullPath;
                actions.Add(new ContextMenuAction { Label = "在终端中打开", IconSvg = Icons.Terminal, Execute = () => _launcherService.OpenInTerminalAsync(terminalPath) });
            }
            if (_contextMenuService != null && includeDynamicActions)
                actions.AddRange(await _contextMenuService.GetTopLevelOpenWithActionsAsync(entry.FullPath));
        }
        else if (includeDynamicActions)
        {
            var topLevelOpenWith = await BuildTopLevelOpenWithActionsForRemoteAsync(entry);
            if (topLevelOpenWith.Count > 0)
            {
                actions.Add(ContextMenuAction.Separator);
                actions.AddRange(topLevelOpenWith);
            }
        }

        // Pin (skip for remote paths)
        if (entry.IsDirectory && includeDynamicActions && !isRemote)
        {
            actions.Add(ContextMenuAction.Separator);
            var isPinned = await _fileOps.IsFolderPinnedAsync(entry.FullPath);
            actions.Add(new ContextMenuAction
            {
                Label = isPinned ? "取消Pin" : "Pin到收藏",
                IconSvg = Icons.Pin,
                Execute = isPinned
                    ? () => UnpinFolderAsync(entry.FullPath)
                    : () => PinFolderAsync(entry.FullPath, entry.Name)
            });
        }

        // Add to collection
        if (Collections.Count > 0 && !entry.IsDirectory)
        {
            actions.Add(new ContextMenuAction
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

        if (IsCollectionView && _navigation.CurrentCollectionId != null)
        {
            actions.Add(ContextMenuAction.Separator);
            actions.Add(new ContextMenuAction { Label = "从收藏夹中移除", IconSvg = Icons.Delete, Execute = () => RemoveFromCollectionAsync(entry.FullPath) });
        }

        // Info
        actions.Add(ContextMenuAction.Separator);
        actions.Add(new ContextMenuAction { Label = "查看文件信息", IconSvg = Icons.Info, ShortcutText = "⌘I", Execute = () => ShowMetadataCommand.ExecuteAsync(entry) });

        return actions;
    }

    private async Task<IReadOnlyList<ContextMenuAction>> BuildOpenWithActionsForRemoteAsync(FileSystemEntry entry)
    {
        if (_remoteFileEditService == null || _remoteConnectionService == null || _openWithAppService == null || _launcherService == null)
            return [];

        try
        {
            var actions = new List<ContextMenuAction>();

            foreach (var app in await _openWithAppService.GetSubmenuAppsAsync())
            {
                actions.Add(BuildRemoteOpenWithAction(entry, app, app.Label));
            }
            return actions;
        }
        catch (Exception ex)
        {
            StatusText = $"准备文件失败: {ex.Message}";
            return [];
        }
    }

    private async Task<IReadOnlyList<ContextMenuAction>> BuildTopLevelOpenWithActionsForRemoteAsync(
        FileSystemEntry entry)
    {
        if (_remoteFileEditService == null || _remoteConnectionService == null
            || _openWithAppService == null || _launcherService == null)
            return [];

        try
        {
            return (await _openWithAppService.GetTopLevelAppsAsync())
                .Select(app => BuildRemoteOpenWithAction(entry, app, $"在 {app.Label} 中打开"))
                .ToList();
        }
        catch (Exception ex)
        {
            StatusText = $"准备打开方式失败: {ex.Message}";
            return [];
        }
    }

    private ContextMenuAction BuildRemoteOpenWithAction(
        FileSystemEntry entry,
        OpenWithApp app,
        string label)
    {
        var bundleId = app.BundleId;
        return new ContextMenuAction
        {
            Label = label,
            IconSvg = Icons.Open,
            LoadIconBase64Async = () => _openWithAppService!.GetAppIconBase64Async(bundleId),
            Execute = async () =>
            {
                try
                {
                    var (serverId, remotePath) = VirtualPath.ParseRemotePath(entry.FullPath);
                    StatusText = $"正在下载 {entry.Name}...";
                    var localPath = await _remoteFileEditService!.DownloadForEditAsync(remotePath, serverId);
                    _remoteFileEditService.WatchForChanges(localPath, remotePath, serverId);
                    StatusText = "";
                    await _launcherService!.OpenFileWithAppAsync(localPath, bundleId);
                }
                catch (Exception ex)
                {
                    StatusText = $"打开失败: {ex.Message}";
                }
            }
        };
    }

    private async Task<List<ContextMenuAction>> BuildBackgroundContextMenuAsync()
    {
        var actions = new List<ContextMenuAction>();
        var currentPath = _navigation.CurrentPath;

        actions.Add(new ContextMenuAction { Label = "新建文件夹", IconSvg = Icons.NewFolder, ShortcutText = "⇧⌘N", Execute = () => CreateNewFolderCommand.ExecuteAsync(null) });
        actions.Add(new ContextMenuAction { Label = "新建文件", IconSvg = Icons.NewFile, Execute = () => CreateNewFileCommand.ExecuteAsync(null) });

        actions.Add(ContextMenuAction.Separator);

        actions.Add(new ContextMenuAction { Label = "粘贴", IconSvg = Icons.Paste, ShortcutText = "⌘V", IsQuickAction = true, IsEnabled = _clipboardService?.HasClipboardFiles ?? false, Execute = () => PasteCommand.ExecuteAsync(null) });

        actions.Add(ContextMenuAction.Separator);

        actions.Add(new ContextMenuAction { Label = "刷新", IconSvg = Icons.Refresh, ShortcutText = "⌘R", Execute = () => RefreshCommand.ExecuteAsync(null) });

        if (_launcherService != null)
        {
            actions.Add(ContextMenuAction.Separator);
            actions.Add(new ContextMenuAction { Label = "在终端中打开", IconSvg = Icons.Terminal, Execute = () => _launcherService.OpenInTerminalAsync(currentPath) });
        }

        if (_contextMenuService != null)
            actions.AddRange(await _contextMenuService.GetTopLevelOpenWithActionsAsync(currentPath));

        actions.Add(ContextMenuAction.Separator);
        actions.Add(new ContextMenuAction
        {
            Label = "复制路径",
            IconSvg = Icons.CopyPath,
            ShortcutText = "⇧⌘C",
            Execute = () => _clipboardService?.CopyTextAsync(currentPath) ?? Task.CompletedTask
        });

        return actions;
    }

    private List<ContextMenuAction> BuildTrashFileContextMenu(FileSystemEntry entry)
    {
        return
        [
            new ContextMenuAction
            {
                Label = "永久删除",
                IconSvg = Icons.Delete,
                Execute = () => PermanentlyDeleteContextEntriesAsync(entry)
            }
        ];
    }

    private IReadOnlyList<FileSystemEntry> GetContextEntries(FileSystemEntry entry)
        => _selectedEntriesSet.Contains(entry) && SelectedEntries.Count > 0
            ? SelectedEntries.ToArray()
            : [entry];

    private static bool IsUsableLocalEntry(FileSystemEntry entry)
        => !string.IsNullOrWhiteSpace(entry.FullPath)
            && !entry.IsVirtual
            && !VirtualPath.IsRemotePath(entry.FullPath);

    private ContextMenuAction BuildMoveToContextMenuAction(FileSystemEntry entry, bool isEnabled)
    {
        var destinations = BuildMoveDestinationActions(entry, isEnabled);
        return new ContextMenuAction
        {
            Label = "移动到",
            IconSvg = Icons.Folder,
            IsEnabled = isEnabled,
            SubItems = destinations
        };
    }

    private IReadOnlyList<ContextMenuAction> BuildMoveDestinationActions(FileSystemEntry entry, bool isEnabled)
    {
        if (!isEnabled)
            return [new ContextMenuAction { Label = "此项目无法移动", IsEnabled = false }];

        var selectedEntries = GetContextEntries(entry);
        var destinations = new List<(string Path, string DisplayName)>();

        foreach (var pinnedFolder in PinnedFolders)
            AddMoveDestination(destinations, pinnedFolder.FolderPath, pinnedFolder.DisplayName);

        var home = _fileService.HomeDirectory;
        AddMoveDestination(destinations, Path.Combine(home, "Desktop"), "桌面");
        AddMoveDestination(destinations, Path.Combine(home, "Documents"), "文稿");
        AddMoveDestination(destinations, Path.Combine(home, "Downloads"), "下载");

        var actions = destinations
            .Where(destination => CanMoveEntriesTo(selectedEntries, destination.Path))
            .GroupBy(destination => destination.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(destination => new ContextMenuAction
            {
                Label = destination.DisplayName,
                IconSvg = Icons.Folder,
                Execute = () => MoveContextEntriesToAsync(entry, destination.Path)
            })
            .ToList();

        if (actions.Count > 0)
            actions.Add(ContextMenuAction.Separator);

        actions.Add(new ContextMenuAction
        {
            Label = "选取…",
            IconSvg = Icons.Folder,
            Execute = () => PickMoveDestinationAsync(entry)
        });

        return actions;
    }

    private static void AddMoveDestination(
        ICollection<(string Path, string DisplayName)> destinations,
        string path,
        string displayName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        destinations.Add((path, string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(path) : displayName));
    }

    private static bool CanMoveEntriesTo(IReadOnlyList<FileSystemEntry> entries, string targetPath)
    {
        var normalizedTarget = Path.GetFullPath(targetPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return entries.All(entry =>
        {
            var normalizedSource = Path.GetFullPath(entry.FullPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                return false;

            return !entry.IsDirectory
                || !normalizedTarget.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }

    private async Task PickMoveDestinationAsync(FileSystemEntry entry)
    {
        var storage = _topLevelWindow?.StorageProvider;
        if (storage == null)
        {
            StatusText = "无法打开文件夹选择器";
            return;
        }

        IsContextMenuVisible = false;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "移动到",
            AllowMultiple = false
        });
        var targetPath = folders.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(targetPath))
            return;

        await MoveContextEntriesToAsync(entry, targetPath);
    }

    private async Task MoveContextEntriesToAsync(FileSystemEntry entry, string targetPath)
    {
        var entries = GetContextEntries(entry);
        if (!Directory.Exists(targetPath) || !CanMoveEntriesTo(entries, targetPath))
        {
            StatusText = "无法移动到这个位置";
            return;
        }

        IsContextMenuVisible = false;
        await MoveEntriesAsync(entries, new FileSystemEntry
        {
            FullPath = targetPath,
            Name = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            IsDirectory = true,
            IconKey = "folder"
        });
    }

    private async Task PermanentlyDeleteContextEntriesAsync(FileSystemEntry entry)
    {
        var entries = GetContextEntries(entry);
        if (entries.Count == 0 || entries.Any(item => !IsUsableLocalEntry(item)))
        {
            StatusText = "此项目无法永久删除";
            return;
        }

        IsContextMenuVisible = false;
        if (_topLevelWindow == null)
        {
            StatusText = "无法显示永久删除确认窗口";
            return;
        }

        var dialog = new DeleteConfirmDialog();
        var subject = entries.Count == 1 ? $"“{entries[0].Name}”" : $"选中的 {entries.Count} 个项目";
        dialog.Configure("永久删除", $"{subject} 将被永久删除，且无法恢复。", "永久删除");
        if (!await dialog.ShowDialogAsync(_topLevelWindow))
            return;

        try
        {
            foreach (var item in entries)
                await _fileService.DeletePermanentlyAsync(item.FullPath);

            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
            StatusText = "已永久删除";
        }
        catch (Exception ex)
        {
            StatusText = $"永久删除失败: {ex.Message}";
        }
    }

    private async Task CopySha256HashesAsync(FileSystemEntry entry)
    {
        var entries = GetContextEntries(entry);
        if (entries.Count == 0 || entries.Any(item => !IsUsableLocalEntry(item) || item.IsDirectory))
        {
            StatusText = "哈希校验仅支持本地文件";
            return;
        }

        IsContextMenuVisible = false;
        try
        {
            var resultLines = new List<string> { "SHA-256" };
            foreach (var item in entries)
            {
                await using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = await SHA256.HashDataAsync(stream);
                resultLines.Add($"{Convert.ToHexString(hash)}  {item.Name}");
            }

            var result = string.Join(Environment.NewLine, resultLines);
            if (_clipboardService != null)
                await _clipboardService.CopyTextAsync(result);

            StatusText = entries.Count == 1
                ? $"SHA-256 已复制: {resultLines[1].Split("  ")[0]}"
                : $"已将 {entries.Count} 个文件的 SHA-256 哈希值复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText = $"哈希校验失败: {ex.Message}";
        }
    }

    private List<ContextMenuAction> BuildTrashBackgroundContextMenu()
    {
        return
        [
            new ContextMenuAction
            {
                Label = "清倒废纸篓",
                IconSvg = Icons.Delete,
                Execute = async () =>
                {
                    await _fileService.EmptyTrashAsync();
                    await LoadDirectoryContentsAsync(forceRefresh: true);
                }
            }
        ];
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
                    IconBase64 = action.IconBase64,
                    LoadIconBase64Async = action.LoadIconBase64Async,
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
                        if (!_selectedEntriesSet.Contains(entry))
                        {
                            SelectedEntries.Clear();
                            SelectedEntries.Add(entry);
                        }
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
            else if (action.Label == "解压")
            {
                result.Add(BuildExtractContextMenuAction(entry));
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
                        ? () => UnpinFolderAsync(entry.FullPath)
                        : () => PinFolderAsync(entry.FullPath, entry.Name)
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
                    Execute = () => PermanentlyDeleteContextEntriesAsync(entry)
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
        if (SelectedEntries.Count != 1 || !ReferenceEquals(SelectedEntries[0], entry))
            ReplaceSelection([entry], entry);

        IsInfoPanelVisible = true;
        await LoadMetadataAsync(entry);
    }

    private Task LoadMetadataAsync(FileSystemEntry entry)
    {
        if (_metadataService == null)
            return Task.CompletedTask;

        if (entry.IsDirectory)
        {
            _metadataLoadGeneration++;
            _metadataLoadPath = null;
            _metadataLoadTask = null;
            CurrentMetadata = null;
            return Task.CompletedTask;
        }

        if (CurrentMetadata?.FullPath == entry.FullPath)
            return Task.CompletedTask;

        if (string.Equals(_metadataLoadPath, entry.FullPath, StringComparison.Ordinal)
            && _metadataLoadTask is { IsCompleted: false } inFlight)
            return inFlight;

        var generation = ++_metadataLoadGeneration;
        _metadataLoadPath = entry.FullPath;
        _metadataLoadTask = LoadMetadataCoreAsync(entry, generation);
        return _metadataLoadTask;
    }

    private void QueueMetadataLoad(FileSystemEntry entry)
    {
        CancelQueuedMetadataLoad();
        _metadataLoadGeneration++;
        _metadataLoadPath = null;
        _metadataLoadTask = null;

        var cts = new CancellationTokenSource();
        _metadataLoadDebounceCts = cts;
        _ = QueueMetadataLoadCoreAsync(entry, cts);
    }

    private async Task QueueMetadataLoadCoreAsync(FileSystemEntry entry, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SelectionMetadataDelayMs, cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (!cts.IsCancellationRequested && IsInfoPanelVisible && SelectedEntries.Count == 1
                    && ReferenceEquals(SelectedEntries[0], entry))
                    _ = LoadMetadataAsync(entry);
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_metadataLoadDebounceCts, cts))
                    _metadataLoadDebounceCts = null;
                cts.Dispose();
            }, DispatcherPriority.Background);
        }
    }

    private void CancelQueuedMetadataLoad()
    {
        _metadataLoadDebounceCts?.Cancel();
    }

    private async Task LoadMetadataCoreAsync(FileSystemEntry entry, int generation)
    {
        try
        {
            var metadata = await _metadataService!.GetMetadataAsync(entry.FullPath).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _metadataLoadGeneration)
                    CurrentMetadata = metadata;
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _metadataLoadGeneration)
                    StatusText = $"获取元数据失败: {ex.Message}";
            }, DispatcherPriority.Background);
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _metadataLoadGeneration)
                {
                    _metadataLoadPath = null;
                    _metadataLoadTask = null;
                }
            }, DispatcherPriority.Background);
        }
    }

    [RelayCommand]
    public void CloseMetadata()
    {
        IsMetadataPanelVisible = false;
        CancelQueuedMetadataLoad();
        _metadataLoadGeneration++;
        _metadataLoadPath = null;
        _metadataLoadTask = null;
        CurrentMetadata = null;
    }

    // ── File Operations ──

    [RelayCommand]
    public void CopySelected()
    {
        if (SelectedEntries.Count == 0) return;
        _fileOps.CopySelectedCommand.Execute(SelectedEntries.ToList());
        StatusText = $"已拷贝 {SelectedEntries.Count} 项";
    }

    public async Task CopyPathAsync()
    {
        if (_clipboardService == null) return;

        var path = SelectedEntries.Count == 1
            ? SelectedEntries[0].FullPath
            : _navigation.CurrentPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        await _clipboardService.CopyTextAsync(path);
        StatusText = "已复制路径";
    }

    [RelayCommand]
    public void CutSelected()
    {
        if (SelectedEntries.Count == 0) return;
        _fileOps.CutSelectedCommand.Execute(SelectedEntries.ToList());
        StatusText = $"已剪切 {SelectedEntries.Count} 项";
    }

    // Paste conflict dialog state
    [ObservableProperty]
    private bool _isPasteConfirmDialogVisible;

    public List<string> PasteConflictNames { get; private set; } = [];

    // Move (drag-drop) conflict dialog state
    [ObservableProperty]
    private bool _isMoveConfirmDialogVisible;

    public List<string> MoveConflictNames { get; private set; } = [];

    private IReadOnlyList<FileSystemEntry>? _pendingMoveEntries;
    private FileSystemEntry? _pendingMoveTarget;

    [RelayCommand]
    public async Task PasteAsync()
    {
        if (_clipboardService?.HasClipboardFiles != true) return;

        try
        {
            var conflicts = _fileOps.GetPasteConflicts(_navigation.CurrentPath);
            if (conflicts.Count > 0)
            {
                PasteConflictNames = conflicts;
                IsPasteConfirmDialogVisible = true;
                return;
            }

            await _fileOps.PasteAsync(_navigation.CurrentPath, IsCollectionView, _navigation.CurrentCollectionId);
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
            StatusText = "已粘贴";
        }
        catch (Exception ex) { StatusText = $"粘贴失败: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task ConfirmPasteAsync()
    {
        IsPasteConfirmDialogVisible = false;
        try
        {
            await _fileOps.PasteAsync(_navigation.CurrentPath, IsCollectionView, _navigation.CurrentCollectionId, overwrite: true);
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
            StatusText = "已粘贴";
        }
        catch (Exception ex) { StatusText = $"粘贴失败: {ex.Message}"; }
    }

    [RelayCommand]
    public void CancelPasteConfirmDialog()
    {
        IsPasteConfirmDialogVisible = false;
        PasteConflictNames.Clear();
    }

    // Delete confirmation dialog state
    [ObservableProperty]
    private bool _isDeleteConfirmDialogVisible;

    [ObservableProperty]
    private bool _isCollectionDeleteConfirmDialogVisible;

    public int DeleteConfirmItemCount { get; private set; }
    public string DeleteConfirmFirstItemName { get; private set; } = "";
    public int? PendingDeleteCollectionId { get; private set; }
    public string PendingDeleteCollectionName { get; private set; } = "";

    [RelayCommand]
    public void ShowDeleteConfirmDialog()
    {
        if (SelectedEntries.Count == 0) return;
        IsContextMenuVisible = false;
        DeleteConfirmItemCount = SelectedEntries.Count;
        DeleteConfirmFirstItemName = SelectedEntries.First().Name;
        IsDeleteConfirmDialogVisible = true;
    }

    [RelayCommand]
    public async Task ConfirmDeleteSelectedAsync()
    {
        IsDeleteConfirmDialogVisible = false;
        DeleteConfirmItemCount = 0;
        DeleteConfirmFirstItemName = "";
        await ExecuteDeleteSelectedAsync();
    }

    [RelayCommand]
    public void CancelDeleteConfirmDialog()
    {
        IsDeleteConfirmDialogVisible = false;
        DeleteConfirmItemCount = 0;
        DeleteConfirmFirstItemName = "";
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
                msg => StatusText = msg,
                this
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
                    entries => { ApplyEntries(entries); ResolveRealEntries(entries); },
                    msg => StatusText = msg,
                    () => _navigation.UpdateBreadcrumbs(),
                    folders => { }
                );
            else
                await LoadDirectoryContentsAsync(forceRefresh: true);

            RefreshLocationStatus();
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
                async (createdName) =>
                {
                    ScrollBehaviorAfterLoad = ScrollMode.ScrollToSelected;
                    await LoadDirectoryContentsAsync(forceRefresh: true);
                    var newEntry = Entries.FirstOrDefault(e => e.Name == createdName);
                    if (newEntry != null)
                    {
                        SelectedEntries.Clear();
                        SelectedEntries.Add(newEntry);
                        _fileOps.RaiseRequestRename(newEntry);
                    }
                    RefreshLocationStatus();
                    _directoryChangeNotifier?.NotifyChanged([_navigation.CurrentPath], this);
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
                async (createdName) =>
                {
                    ScrollBehaviorAfterLoad = ScrollMode.ScrollToSelected;
                    await LoadDirectoryContentsAsync(forceRefresh: true);
                    var newEntry = Entries.FirstOrDefault(e => e.Name == createdName);
                    if (newEntry != null)
                    {
                        SelectedEntries.Clear();
                        SelectedEntries.Add(newEntry);
                        _fileOps.RaiseRequestRename(newEntry);
                    }
                    RefreshLocationStatus();
                    _directoryChangeNotifier?.NotifyChanged([_navigation.CurrentPath], this);
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
            var conflicts = _fileOps.GetMoveConflicts(entries, targetFolder.FullPath);
            if (conflicts.Count > 0)
            {
                _pendingMoveEntries = entries;
                _pendingMoveTarget = targetFolder;
                MoveConflictNames = conflicts;
                IsMoveConfirmDialogVisible = true;
                return;
            }

            await _fileOps.MoveEntriesAsync(
                entries,
                targetFolder,
                msg => StatusText = msg
            );
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
        }
        catch (Exception ex) { StatusText = $"移动失败: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task ConfirmMoveAsync()
    {
        IsMoveConfirmDialogVisible = false;
        if (_pendingMoveEntries == null || _pendingMoveTarget == null) return;

        try
        {
            await _fileOps.MoveEntriesAsync(
                _pendingMoveEntries,
                _pendingMoveTarget,
                msg => StatusText = msg,
                overwrite: true
            );
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);
            StatusText = "已移动";
        }
        catch (Exception ex) { StatusText = $"移动失败: {ex.Message}"; }
        finally
        {
            _pendingMoveEntries = null;
            _pendingMoveTarget = null;
        }
    }

    [RelayCommand]
    public void CancelMoveConfirmDialog()
    {
        IsMoveConfirmDialogVisible = false;
        MoveConflictNames.Clear();
        _pendingMoveEntries = null;
        _pendingMoveTarget = null;
    }

    public event Action<FileSystemEntry>? RenameRequested;

    public void RequestRename(FileSystemEntry entry)
    {
        RenameRequested?.Invoke(entry);
    }

    public async Task<bool> RenameEntryAsync(FileSystemEntry entry, string newName)
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
                    ReplaceSelection([Entries[i]], Entries[i]);
                    break;
                }
            }
            return true;
        }

        // Other virtual entries cannot be renamed
        if (entry.IsVirtual)
            return false;

        try
        {
            var oldPath = entry.FullPath;
            var wasPinned = entry.IsDirectory && await _fileOps.IsFolderPinnedAsync(oldPath);
            await _fileOps.RenameEntryAsync(entry, newName, IsAiView, msg => StatusText = msg);

            if (wasPinned)
                await _collection.LoadPinnedFoldersAsync();

            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
            await LoadDirectoryContentsAsync(forceRefresh: true);

            var renamed = Entries.FirstOrDefault(e => e.Name == newName);
            if (renamed != null)
                ReplaceSelection([renamed], renamed);

            RefreshLocationStatus();
            _directoryChangeNotifier?.NotifyChanged([_navigation.CurrentPath], this);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"重命名失败: {ex.Message}";
            return false;
        }
    }

    // ── Archive Operations ──

    private ContextMenuAction BuildExtractContextMenuAction(FileSystemEntry entry)
    {
        var archiveName = ArchiveExtractionPathHelper.GetArchiveFolderName(entry.FullPath);
        return new ContextMenuAction
        {
            Label = "解压",
            IconSvg = AppIcons.Compress,
            SubItems =
            [
                new ContextMenuAction
                {
                    Label = "解压到当前文件夹",
                    IconSvg = AppIcons.Folder,
                    Execute = () =>
                    {
                        ExtractHere(entry);
                        return Task.CompletedTask;
                    }
                },
                new ContextMenuAction
                {
                    Label = $"解压到 {archiveName}",
                    IconSvg = AppIcons.NewFolder,
                    Execute = () =>
                    {
                        ExtractToNamedFolder(entry);
                        return Task.CompletedTask;
                    }
                }
            ]
        };
    }

    public void ExtractHere(FileSystemEntry entry)
    {
        _ = _archive.ExtractHereAsync(
            entry,
            _navigation.CurrentPath,
            msg => StatusText = msg,
            async () => await RefreshAsync(),
            PromptPasswordAsync
        );
    }

    public void ExtractToNamedFolder(FileSystemEntry entry)
    {
        _ = _archive.ExtractToNamedFolderAsync(
            entry,
            _navigation.CurrentPath,
            msg => StatusText = msg,
            async () => await RefreshAsync(),
            PromptPasswordAsync
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
            _directoryChangeNotifier,
            async () => await RefreshAsync(),
            msg => StatusText = msg
        );
    }

    public void CancelCompressDialog() => _archive.CancelCompressDialog();

    private async Task<string?> PromptPasswordAsync()
    {
        return await RunOnUiThreadAsync(async () =>
        {
            if (_topLevelWindow == null) return null;
            var dialog = new PasswordDialog();
            using var modalBlock = _topLevelWindow is MacExplorer.Views.MainWindow mainWindow
                ? mainWindow.BlockModalParentInteraction()
                : null;
            return await dialog.ShowDialogAsync(_topLevelWindow);
        });
    }

    private static async Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return await completion.Task;
    }

    // ── Search ──

    [RelayCommand]
    public async Task SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            await ExitSearchAsync();
            return;
        }

        CaptureCurrentNavigationViewState();
        var wasHomePage = IsHomePage;
        var searchCurrentPath = wasHomePage || IsTagView ? null : _navigation.CurrentPath;
        var searchRoot = string.IsNullOrEmpty(searchCurrentPath) ? HomeDirectory : searchCurrentPath;
        CancelDirectoryWork();
        _search.EnterSearchMode(wasHomePage);
        _navigation.SetWatchedDirectory(null);
        _navigation.IsHomePage = false;
        _navigation.IsArchiveView = false;
        _navigation.IsCollectionView = false;
        _navigation.IsAiView = false;
        _navigation.IsRemoteView = false;
        _navigation.CurrentArchivePath = null;
        _navigation.CurrentArchiveInternalPath = "";
        _navigation.CurrentCollectionId = null;
        _navigation.CurrentCollectionName = null;
        _navigation.CurrentFaceClusterId = null;
        _navigation.CurrentAiContextLabel = null;
        _navigation.CurrentRemoteServerId = null;
        _navigation.IsSearchMode = true;
        _navigation.SearchQuery = query;
        _navigation.PushOrReplaceSearchHistory(query, searchRoot, wasHomePage, []);
        ScrollBehaviorAfterLoad = ScrollMode.ResetToTop;
        IsLoading = true;

        try
        {
            await _search.SearchAsync(
                query,
                HomeDirectory,
                searchCurrentPath,
                entries =>
                {
                    ApplyEntries(entries);
                    SelectedEntries.Clear();
                    _navigation.UpdateCurrentSearchResults(Entries.ToArray());
                },
                msg => StatusText = msg
            );
            _navigation.UpdateCurrentSearchResults(Entries.ToArray());
        }
        finally { IsLoading = false; }
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetSearchSuggestionsAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var searchRoot = IsHomePage
            || string.IsNullOrEmpty(_navigation.CurrentPath)
            || VirtualPath.IsRemotePath(_navigation.CurrentPath)
            || AiPathHelper.IsAiPath(_navigation.CurrentPath)
            || TagPathHelper.IsTagPath(_navigation.CurrentPath)
            || ArchivePathHelper.IsArchivePath(_navigation.CurrentPath)
                ? HomeDirectory
                : _navigation.CurrentPath;

        return _search.GetSuggestionsAsync(
            searchRoot,
            query,
            maxResults,
            cancellationToken);
    }

    public async Task ExitSearchAsync()
    {
        CaptureCurrentNavigationViewState();
        var wasHomePage = _search.WasHomePageBeforeSearch;
        _search.ExitSearchMode(true);
        _navigation.RemoveCurrentSearchHistory();
        _navigation.IsSearchMode = false;
        _navigation.SearchQuery = string.Empty;

        if (wasHomePage)
        {
            _navigation.IsHomePage = true;
            SelectedEntries.Clear();
            return;
        }

        await RefreshAsync();
    }

    private void RestoreSearchHistoryEntry(NavigationHistoryEntry entry)
    {
        CancelDirectoryWork();
        _navigation.SetWatchedDirectory(null);
        _search.RestoreSearchMode(entry.SearchQuery, entry.WasHomePageBeforeSearch);
        _navigation.IsHomePage = false;
        _navigation.IsArchiveView = false;
        _navigation.IsCollectionView = false;
        _navigation.IsAiView = false;
        _navigation.IsRemoteView = false;
        _navigation.CurrentArchivePath = null;
        _navigation.CurrentArchiveInternalPath = "";
        _navigation.CurrentCollectionId = null;
        _navigation.CurrentCollectionName = null;
        _navigation.CurrentFaceClusterId = null;
        _navigation.CurrentAiContextLabel = null;
        _navigation.CurrentRemoteServerId = null;
        _navigation.IsSearchMode = true;
        _navigation.SearchQuery = entry.SearchQuery;
        ApplyEntries(entry.SearchResults);
        StatusText = $"搜索 \"{entry.SearchQuery}\" — 找到 {entry.SearchResults.Count} 项";
    }

    // ── Tags ──

    public Task NavigateToTagAsync(FileTag tag) => NavigateToAsync(tag.VirtualPath);

    private async Task HandleTagNavigationAsync(string sentinelPath, bool recordHistory = true)
    {
        if (_fileTagService == null || !TagPathHelper.TryParse(sentinelPath, out var tag))
        {
            StatusText = "标签服务未初始化";
            return;
        }

        CancelDirectoryWork();
        _navigation.SetWatchedDirectory(null);
        _navigation.IsHomePage = false;
        _navigation.IsArchiveView = false;
        _navigation.IsCollectionView = false;
        _navigation.IsAiView = false;
        _navigation.IsRemoteView = false;
        _navigation.IsSearchMode = false;
        _navigation.SearchQuery = string.Empty;
        _navigation.CurrentArchivePath = null;
        _navigation.CurrentArchiveInternalPath = string.Empty;
        _navigation.CurrentCollectionId = null;
        _navigation.CurrentCollectionName = null;
        _navigation.CurrentFaceClusterId = null;
        _navigation.CurrentAiContextLabel = null;
        _navigation.CurrentRemoteServerId = null;
        _ai.Reset();
        _search.Reset();
        _navigation.CurrentPath = sentinelPath;
        _navigation.UpdateBreadcrumbs();
        if (recordHistory)
            _navigation.UpdateHistoryForSentinelPath(sentinelPath);
        ScrollBehaviorAfterLoad = ScrollMode.ResetToTop;
        IsLoading = true;

        try
        {
            var paths = await _fileTagService.FindFilePathsAsync(tag);
            var entries = new List<FileSystemEntry>(Math.Min(paths.Count, 2000));
            foreach (var path in paths.Distinct(StringComparer.Ordinal).Take(2000))
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    continue;

                var entry = await _fileService.GetEntryAsync(path);
                if (entry != null)
                    entries.Add(entry);
            }

            if (!string.Equals(_navigation.CurrentPath, sentinelPath, StringComparison.Ordinal))
                return;

            ApplyEntries(entries);
            ResolveRealEntries(entries);
            StatusText = $"标签“{tag.Name}” · {entries.Count} 项";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"加载标签失败: {ex.Message}";
            _logger?.LogError(ex, "Failed to load tag {Tag}", tag.Name);
        }
        finally
        {
            if (string.Equals(_navigation.CurrentPath, sentinelPath, StringComparison.Ordinal))
                IsLoading = false;
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
                entries => { ApplyEntries(entries); ResolveRealEntries(entries); },
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
                    entries => { ApplyEntries(entries); ResolveRealEntries(entries); },
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
        CancelDirectoryWork();
        _navigation.SetWatchedDirectory(null);
        // Clear current path so sidebar folder items deselect properly
        _navigation.CurrentPath = "";

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
            entries => { ApplyEntries(entries); ResolveRealEntries(entries); },
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
        await _ai.SearchAiTagsAsync(query, entries => { ApplyEntries(entries); ResolveRealEntries(entries); }, msg => StatusText = msg);
    }

    public void ClearTextSearchQuery() => _ai.ClearTextSearchQuery();

    // ── Sort/Filter ──

    public void NotifySidebarVisibilityChanged() => OnPropertyChanged("SidebarVisibilityChanged");

    // ── Sidebar Commands ──

    [RelayCommand]
    public async Task EjectVolumeAsync(VolumeInfo vol)
    {
        if (_volumeMonitorService == null) return;
        var success = await _volumeMonitorService.EjectVolumeAsync(vol.Path);
        if (!success)
            StatusText = $"无法弹出「{vol.DisplayName}」，请关闭正在使用的文件后重试";
    }

    [RelayCommand]
    public void ToggleAiCollapsed()
    {
        IsAiSectionCollapsed = !IsAiSectionCollapsed;
    }

    [RelayCommand]
    public void ToggleCollectionsCollapsed()
    {
        IsCollectionsSectionCollapsed = !IsCollectionsSectionCollapsed;
    }

    [RelayCommand]
    public void ToggleTagsCollapsed()
    {
        IsTagsSectionCollapsed = !IsTagsSectionCollapsed;
    }

    // ── Sidebar Helpers ──

    public void LoadSidebarVisibility()
    {
        if (_settingsService == null) return;
        ShowSidebarUsername = _settingsService.Get("sidebar_show_username", true);
        ShowSidebarDesktop = _settingsService.Get("sidebar_show_desktop", true);
        ShowSidebarDocuments = _settingsService.Get("sidebar_show_documents", true);
        ShowSidebarDownloads = _settingsService.Get("sidebar_show_downloads", true);
        ShowSidebarPictures = _settingsService.Get("sidebar_show_pictures", true);
        ShowSidebarMusic = _settingsService.Get("sidebar_show_music", true);
        ShowSidebarMacintoshHd = _settingsService.Get("sidebar_show_macintosh_hd", true);
        ShowSidebarApplications = _settingsService.Get("sidebar_show_applications", true);
        ShowSidebarTrash = _settingsService.Get("sidebar_show_trash", true);
        ShowSidebarAiPeople = _settingsService.Get("sidebar_show_ai_people", true);
        ShowSidebarAiCategories = _settingsService.Get("sidebar_show_ai_categories", true);
        ShowSidebarAiLocations = _settingsService.Get("sidebar_show_ai_locations", true);
        ShowSidebarAiDates = _settingsService.Get("sidebar_show_ai_dates", true);
        ShowSidebarAiTextSearch = _settingsService.Get("sidebar_show_ai_text_search", true);
        OnPropertyChanged(nameof(ShowSidebarUsername));
        OnPropertyChanged(nameof(ShowSidebarDesktop));
        OnPropertyChanged(nameof(ShowSidebarDocuments));
        OnPropertyChanged(nameof(ShowSidebarDownloads));
        OnPropertyChanged(nameof(ShowSidebarPictures));
        OnPropertyChanged(nameof(ShowSidebarMusic));
        OnPropertyChanged(nameof(ShowSidebarMacintoshHd));
        OnPropertyChanged(nameof(ShowSidebarApplications));
        OnPropertyChanged(nameof(ShowSidebarTrash));
        OnPropertyChanged(nameof(ShowSidebarAiPeople));
        OnPropertyChanged(nameof(ShowSidebarAiCategories));
        OnPropertyChanged(nameof(ShowSidebarAiLocations));
        OnPropertyChanged(nameof(ShowSidebarAiDates));
        OnPropertyChanged(nameof(ShowSidebarAiTextSearch));
    }

    private void RefreshExternalVolumes()
    {
        ExternalVolumes.Clear();
        if (_volumeMonitorService?.ExternalVolumes != null)
        {
            foreach (var vol in _volumeMonitorService.ExternalVolumes)
                ExternalVolumes.Add(vol);
        }
    }

    private async void OnVolumesChanged()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshExternalVolumes();
            RefreshLocationStatus();

            // If currently browsing a removed volume, navigate home
            if (!string.IsNullOrEmpty(CurrentPath) && CurrentPath.StartsWith("/Volumes/", StringComparison.Ordinal))
            {
                var stillMounted = ExternalVolumes.Any(v =>
                    CurrentPath.StartsWith(v.Path, StringComparison.Ordinal));
                if (!stillMounted)
                    _ = NavigateToAsync("/");
            }
        });
    }

    [RelayCommand]
    public void ToggleViewMode()
    {
        SetViewMode(_sortFilter.ViewMode == ViewMode.Grid ? ViewMode.List : ViewMode.Grid);
    }

    public void SetViewMode(ViewMode viewMode)
    {
        if (_sortFilter.ViewMode == viewMode) return;
        _sortFilter.ViewMode = viewMode;
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
    }

    [RelayCommand]
    public void ToggleMetadataPanel()
    {
        IsMetadataPanelVisible = !IsMetadataPanelVisible;
        if (IsMetadataPanelVisible && SelectedEntries.Count == 1)
            _ = LoadMetadataAsync(SelectedEntries[0]);
        else if (!IsMetadataPanelVisible)
            CurrentMetadata = null;
    }

    [RelayCommand]
    public void ToggleInfoPanel()
    {
        IsInfoPanelVisible = !IsInfoPanelVisible;
        if (IsInfoPanelVisible && SelectedEntries.Count == 1)
            QueueMetadataLoad(SelectedEntries[0]);
        else if (!IsInfoPanelVisible)
        {
            CancelQueuedMetadataLoad();
            _metadataLoadGeneration++;
            _metadataLoadPath = null;
            _metadataLoadTask = null;
            CurrentMetadata = null;
        }
    }

    public Task QuickLookSelectedAsync()
    {
        if (_quickLookService == null || SelectedEntries.Count != 1)
            return Task.CompletedTask;

        return _quickLookService.PreviewFileAsync(SelectedEntries[0].FullPath);
    }

    /// <summary>
    /// Lets in-window preview surfaces reuse the same archive password prompt
    /// as normal archive navigation.
    /// </summary>
    public Task<string?> RequestArchivePasswordAsync() => PromptPasswordAsync();

    public Task<byte[]?> GetPreviewThumbnailAsync(
        FileSystemEntry entry,
        int maxPixelSize = 512,
        CancellationToken cancellationToken = default)
    {
        if (_thumbnailService == null || entry.IsDirectory)
            return Task.FromResult<byte[]?>(null);

        return _thumbnailService.GetThumbnailAsync(entry.FullPath, maxPixelSize, cancellationToken);
    }

    partial void OnIsPreviewPaneVisibleChanged(bool value)
    {
        _settingsService?.Set(PreviewPaneVisibleSettingKey, value);
    }

    partial void OnIsMetadataPanelVisibleChanged(bool value)
    {
        _settingsService?.Set(MetadataPanelVisibleSettingKey, value);
    }

    partial void OnIsInfoPanelVisibleChanged(bool value)
    {
        _settingsService?.Set("IsInfoPanelVisible", value);
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

    public Task ReorderPinnedFolderAsync(string sourcePath, string targetPath)
        => _collection.ReorderPinnedFolderAsync(sourcePath, targetPath);

    public bool IsCollectionNameDuplicate(string name, int? excludeId = null)
    {
        return _collection.IsCollectionNameDuplicate(name, excludeId);
    }

    // ── Load Directory Contents ──

    private async Task LoadDirectoryContentsAsync(bool forceRefresh = false)
    {
        var work = BeginDirectoryWork();
        var selectionState = CaptureEntryLoadSelectionState();
        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            // Skip index for /Applications paths because they need to merge /System/Applications counterpart
            // This ensures Utilities and other folders show both user and system applications
            var shouldUseIndex = !IsApplicationsPath(_navigation.CurrentPath);
            if (!forceRefresh && shouldUseIndex && _fileIndex != null && _indexConfig.ShouldIndex(_navigation.CurrentPath))
            {
                entries = await _fileIndex.GetDirectoryContentsAsync(_navigation.CurrentPath);
                if (!IsCurrentDirectoryWork(work)) return;
                if (entries.Count > 0)
                {
                    ApplyEntriesCore(entries);
                    FinalizeEntriesLoad(selectionState, completed: false);
                    StartDirectoryBackgroundWork(entries, work, includeAnalysis: false);
                }
            }
        }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to load directory contents from index for {Path}", _navigation.CurrentPath); }

        try
        {
            entries = await StreamDirectoryEntriesAsync(
                _fileService,
                _navigation.CurrentPath,
                work,
                selectionState);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!IsCurrentDirectoryWork(work)) return;

        StartDirectoryBackgroundWork(entries, work, includeAnalysis: true);
        QueueDirectoryIndexUpdate(_navigation.CurrentPath, entries, work);
        RefreshLocationStatus();

        // Batch load ratings for current directory
        _ = _collection.GetRating(_navigation.CurrentPath); // Just to initialize
    }

    private void QueueDirectoryIndexUpdate(string directoryPath, IReadOnlyList<FileSystemEntry> entries, DirectoryWork work)
    {
        if (_fileIndexWriter == null || !_indexConfig.ShouldIndex(directoryPath) || entries.Count == 0)
            return;

        var snapshot = entries.ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await _fileIndexWriter.UpdateDirectoryAsync(directoryPath, snapshot, work.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to update index for directory {Path}", directoryPath);
            }
        }, work.Token);
    }

    private void StartDirectoryBackgroundWork(
        IReadOnlyList<FileSystemEntry> entries,
        DirectoryWork work,
        bool includeAnalysis)
    {
        _ = ResolveIconsInBackgroundAsync(entries, work);
        _ = ResolveGitStatusAsync(work);
        if (includeAnalysis)
            _ = TriggerImageAnalysisAsync(entries, work);
    }

    private async Task ResolveIconsInBackgroundAsync(IReadOnlyList<FileSystemEntry> entries, DirectoryWork work)
    {
        try
        {
            await _fileService.ResolveAppIconsAsync(entries, () =>
            {
                if (IsCurrentDirectoryWork(work))
                    OnPropertyChanged(nameof(Entries));
            }, work.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to resolve app icons"); }
    }

    private async Task TriggerImageAnalysisAsync(IReadOnlyList<FileSystemEntry> entries, DirectoryWork work)
    {
        await _ai.TriggerImageAnalysisAsync(
            entries,
            _navigation.CurrentPath,
            work.Token
        );
    }

    private void ResolveRealEntries(IReadOnlyList<FileSystemEntry> entries)
    {
        if (!entries.Any(e => !e.IsVirtual)) return;
        var work = BeginDirectoryWork();
        _ = ResolveIconsInBackgroundAsync(entries, work);
    }

    private async Task<IReadOnlyList<FileSystemEntry>> StreamDirectoryEntriesAsync(
        IFileService service,
        string path,
        DirectoryWork work,
        EntryLoadSelectionState selectionState)
    {
        var accumulated = new List<FileSystemEntry>();
        var receivedBatch = false;
        await foreach (var batch in service.EnumerateDirectoryBatchesAsync(path, 256, work.Token))
        {
            if (!IsCurrentDirectoryWork(work))
                throw new OperationCanceledException(work.Token);

            accumulated.AddRange(batch);
            _sortFilter.SetRawEntries(accumulated);
            if (!receivedBatch)
            {
                ScrollBehaviorAfterLoad = selectionState.ScrollBehavior;
                ApplyEntriesCore(accumulated);
                FinalizeEntriesLoad(selectionState, completed: false);
                receivedBatch = true;
            }
            else if (receivedBatch)
            {
                _sortFilter.InsertBatch(batch, Entries);
                FinalizeEntriesLoad(selectionState, completed: false);
            }
        }

        if (!IsCurrentDirectoryWork(work))
            throw new OperationCanceledException(work.Token);

        if (receivedBatch)
        {
            FinalizeEntriesLoad(selectionState, completed: true);
        }
        else
        {
            ScrollBehaviorAfterLoad = selectionState.ScrollBehavior;
            ApplyEntriesCore(accumulated);
            FinalizeEntriesLoad(selectionState, completed: true);
        }

        return accumulated;
    }

    private void ApplyEntries(IReadOnlyList<FileSystemEntry> entries)
    {
        var selectionState = CaptureEntryLoadSelectionState();
        ApplyEntriesCore(entries);
        FinalizeEntriesLoad(selectionState, completed: true);
    }

    private void ApplyEntriesCore(IReadOnlyList<FileSystemEntry> entries)
    {
        _sortFilter.SetRawEntries(entries);
        _sortFilter.ApplySortAndGroup(sortedEntries => Entries = sortedEntries);
    }

    private EntryLoadSelectionState CaptureEntryLoadSelectionState() => new(
        SelectedEntries.Select(entry => entry.FullPath).ToHashSet(StringComparer.Ordinal),
        _lastClickedPath,
        PendingSelectFileName,
        ScrollBehaviorAfterLoad);

    private void FinalizeEntriesLoad(EntryLoadSelectionState state, bool completed)
    {

        var restoredSelection = new List<FileSystemEntry>();
        FileSystemEntry? restoredAnchor = null;

        // Restore selected entry when navigating back/forward
        if (IsRestoringNavigation)
        {
            var savedPath = _navigation.CurrentHistoryEntry?.SelectedEntryPath
                ?? _navigation.GetSavedSelectedEntryPath(_navigation.CurrentPath);
            var savedName = _navigation.CurrentHistoryEntry?.SelectedEntryName
                ?? _navigation.GetSavedSelectedEntryName(_navigation.CurrentPath);
            FileSystemEntry? entry = null;
            if (savedPath != null)
                entry = Entries.FirstOrDefault(e => string.Equals(e.FullPath, savedPath, StringComparison.Ordinal));
            if (entry == null && savedName != null)
                entry = Entries.FirstOrDefault(e => e.Name == savedName);
            if (entry != null)
            {
                restoredSelection.Add(entry);
                restoredAnchor = entry;
            }
        }

        // Auto-select a specific file (e.g. from breadcrumb search suggestion)
        if (state.PendingSelectFileName != null)
        {
            var target = Entries.FirstOrDefault(e => e.Name == state.PendingSelectFileName);
            if (target != null)
            {
                restoredSelection.Clear();
                restoredSelection.Add(target);
                restoredAnchor = target;
            }
            if (completed && string.Equals(PendingSelectFileName, state.PendingSelectFileName, StringComparison.Ordinal))
                PendingSelectFileName = null;
        }
        else if (restoredSelection.Count == 0 && state.SelectedPaths.Count > 0)
        {
            restoredSelection = GetSelectableEntries()
                .Where(e => state.SelectedPaths.Contains(e.FullPath))
                .ToList();

            restoredAnchor = state.AnchorPath == null
                ? restoredSelection.FirstOrDefault()
                : restoredSelection.FirstOrDefault(e => string.Equals(e.FullPath, state.AnchorPath, StringComparison.Ordinal))
                    ?? restoredSelection.FirstOrDefault();
        }

        if (restoredSelection.Count > 0 || completed)
            ReplaceSelection(restoredSelection, restoredAnchor);

        // Reset to PreservePosition so background icon/thumbnail updates don't affect scroll.
        // During history restore, keep the mode alive until the view actually scrolls to the target.
        if (completed && ScrollBehaviorAfterLoad is not ScrollMode.RestoreNavigation and not ScrollMode.ScrollToSelected)
        {
            ScrollBehaviorAfterLoad = ScrollMode.PreservePosition;
        }
    }

    private async Task ResolveGitStatusAsync(DirectoryWork work)
    {
        if (_gitStatusService == null) return;
        try
        {
            work.Token.ThrowIfCancellationRequested();
            var directoryPath = _navigation.CurrentPath;
            var snapshot = new List<FileSystemEntry>(Entries);
            _logger?.LogDebug("[GIT] ResolveGitStatusAsync start, path={Path}, entries={Count}", directoryPath, snapshot.Count);
            var repoStatus = await _gitStatusService.GetRepoStatusAsync(directoryPath);
            if (repoStatus == null || !IsCurrentDirectoryWork(work)) { _logger?.LogDebug("[GIT] repoStatus is null or stale"); return; }

            var repoRoot = repoStatus.RepoRoot;
            var changed = false;

            // Collect candidates: entries under repo root not in FileStatuses
            var candidates = new List<(int index, string relativePath)>();
            for (int i = 0; i < snapshot.Count; i++)
            {
                var entry = snapshot[i];
                if (entry.IsVirtual || entry.GitStatus != GitFileStatus.None) continue;
                if (!entry.FullPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)) continue;

                var relativePath = entry.FullPath[repoRoot.Length..].TrimStart('/');

                if (repoStatus.FileStatuses.TryGetValue(relativePath, out var status))
                {
                    if (status == GitFileStatus.Ignored) continue;
                    snapshot[i] = CopyWithGit(entry, gitStatus: status);
                    changed = true;
                }
                else
                {
                    candidates.Add((i, relativePath));
                }
            }

            // Distinguish ignored, untracked, and tracked-clean among candidates
            if (candidates.Count > 0)
            {
                var ignoredPaths = await Task.Run(() => Services.Impl.GitStatusService.GetIgnoredPaths(repoRoot), work.Token);
                if (!IsCurrentDirectoryWork(work)) return;

                foreach (var (index, relativePath) in candidates)
                {
                    // Skip files inside ignored directories (e.g. node_modules/express/index.js)
                    if (ignoredPaths.Contains(relativePath)
                        || HasAncestorPath(relativePath, ignoredPaths))
                        continue;

                    var entry = snapshot[index];
                    if (entry.IsDirectory)
                    {
                        if (repoStatus.HasAnyChange(relativePath))
                            snapshot[index] = CopyWithGit(entry, hasGitChanges: true);
                        else
                            snapshot[index] = CopyWithGit(entry, gitStatus: GitFileStatus.Unmodified);
                    }
                    else
                    {
                        snapshot[index] = CopyWithGit(entry, gitStatus: GitFileStatus.Unmodified);
                    }
                    changed = true;
                }
            }

            if (changed && IsCurrentDirectoryWork(work))
                ApplyEntriesPreservingSelection(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger?.LogWarning(ex, "Git status resolve failed"); }
    }

    public void StopDirectoryWork()
    {
        CancelDirectoryWork();
        _navigation.SetWatchedDirectory(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _startupSidebarCts.Cancel();
            lock (_directoryWorkLock)
            {
                _directoryWorkCts.Cancel();
                _directoryWorkCts.Dispose();
                _directoryWorkGeneration++;
            }
            CancelQueuedMetadataLoad();
            _navigation.SetWatchedDirectory(null);
        }
        catch { }
        _fileOps.RequestRename -= OnFileOpsRenameRequested;
        Entries.CollectionChanged -= OnEntriesCollectionChanged;
        SelectedEntries.CollectionChanged -= OnSelectedEntriesCollectionChanged;
        _navigation.PropertyChanged -= OnNavigationPropertyChanged;
        _ai.PropertyChanged -= OnAiPropertyChanged;
        _archive.PropertyChanged -= OnArchivePropertyChanged;
        _collection.PropertyChanged -= OnCollectionPropertyChanged;
        _sortFilter.PropertyChanged -= OnSortFilterPropertyChanged;
        _fileOps.PropertyChanged -= OnFileOpsPropertyChanged;
        if (_fileTagService != null)
            _fileTagService.TagsChanged -= OnTagsChanged;
        SetLocationRemoteServer(null);
        if (_volumeMonitorService != null)
            _volumeMonitorService.VolumesChanged -= OnVolumesChanged;
        _metadataLoadDebounceCts?.Dispose();
        _startupSidebarCts.Dispose();
    }

    private DirectoryWork BeginDirectoryWork()
    {
        lock (_directoryWorkLock)
        {
            _directoryWorkCts.Cancel();
            _directoryWorkCts.Dispose();
            _directoryWorkCts = new CancellationTokenSource();
            return new DirectoryWork(++_directoryWorkGeneration, _directoryWorkCts.Token);
        }
    }

    private void CancelDirectoryWork()
    {
        lock (_directoryWorkLock)
        {
            _directoryWorkCts.Cancel();
            _directoryWorkCts.Dispose();
            _directoryWorkCts = new CancellationTokenSource();
            _directoryWorkGeneration++;
        }
        IsLoading = false;
    }

    private bool IsCurrentDirectoryWork(DirectoryWork work)
    {
        return !work.Token.IsCancellationRequested
            && Volatile.Read(ref _directoryWorkGeneration) == work.Generation;
    }

    private readonly record struct DirectoryWork(int Generation, CancellationToken Token);
    private sealed record EntryLoadSelectionState(
        HashSet<string> SelectedPaths,
        string? AnchorPath,
        string? PendingSelectFileName,
        ScrollMode ScrollBehavior);

    private void ApplyEntriesPreservingSelection(IReadOnlyList<FileSystemEntry> entries)
    {
        ApplyEntries(entries);
    }

    private static bool HasAncestorPath(string relativePath, HashSet<string> paths)
    {
        var slash = relativePath.LastIndexOf('/');
        while (slash > 0)
        {
            if (paths.Contains(relativePath[..slash]))
                return true;
            slash = relativePath.LastIndexOf('/', slash - 1);
        }
        return false;
    }

    private static FileSystemEntry CopyWithGit(FileSystemEntry src, GitFileStatus gitStatus = GitFileStatus.None, bool hasGitChanges = false) => new()
    {
        FullPath = src.FullPath,
        Name = src.Name,
        IsDirectory = src.IsDirectory,
        Size = src.Size,
        LastModified = src.LastModified,
        Created = src.Created,
        Extension = src.Extension,
        IsHidden = src.IsHidden,
        IsSymbolicLink = src.IsSymbolicLink,
        IsReadable = src.IsReadable,
        IsWritable = src.IsWritable,
        IconKey = src.IconKey,
        IconUrl = src.IconUrl,
        ThumbnailUrl = src.ThumbnailUrl,
        IsVirtual = src.IsVirtual,
        VirtualFolderType = src.VirtualFolderType,
        VirtualFolderKey = src.VirtualFolderKey,
        VirtualItemCount = src.VirtualItemCount,
        GitStatus = gitStatus,
        HasGitChanges = hasGitChanges
    };

    /// <summary>
    /// Check if a path is under /Applications and needs to merge /System/Applications counterpart.
    /// These paths should not use the index because the merge logic is in GetDirectoryContentsAsync.
    /// </summary>
    private static bool IsApplicationsPath(string path)
    {
        return path == "/Applications" || path.StartsWith("/Applications/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Activates the app window so that subsequent clicks (e.g. context menu items)
    /// are processed immediately without needing an extra focus click.
    /// </summary>
    private void ActivateAppWindow()
    {
        // Handled natively by Avalonia's window management
    }

    private List<SidebarItem> BuildSidebarFavorites()
    {
        var home = _fileService.HomeDirectory;
        return new()
        {
            new() { Name = UserName, Path = home, IconKey = "person", IconData = Icons.Person, IconColor = "#54A3F7" },
            new() { Name = DesktopName, Path = home + "/Desktop", IconKey = "desktop", IconData = Icons.Desktop, IconColor = "#54A3F7" },
            new() { Name = DocumentsName, Path = home + "/Documents", IconKey = "filetext", IconData = Icons.FileText, IconColor = "#3B82F6" },
            new() { Name = DownloadsName, Path = home + "/Downloads", IconKey = "download", IconData = Icons.Download, IconColor = "#22C55E" },
            new() { Name = PicturesName, Path = home + "/Pictures", IconKey = "image", IconData = Icons.Image, IconColor = "#E8912D" },
            new() { Name = MusicName, Path = home + "/Music", IconKey = "music", IconData = Icons.Music, IconColor = "#EC4899" },
        };
    }

    private List<SidebarItem> BuildSidebarLocations()
    {
        return new()
        {
            new() { Name = VolumeName, Path = "/", IconKey = "folder", IconData = Icons.ExternalDrive, IconColor = "#22D3EE" },
        };
    }

    private List<SidebarItem> BuildSidebarAiItems()
    {
        return new()
        {
            new() { Name = "人物", Path = VirtualPath.AiPeople, IconKey = "people", IconData = Icons.People, IconColor = "#EC4899" },
            new() { Name = "分类", Path = VirtualPath.AiCategories, IconKey = "grid", IconData = Icons.Grid, IconColor = "#8B5CF6" },
            new() { Name = "地点", Path = VirtualPath.AiLocations, IconKey = "location", IconData = Icons.Location, IconColor = "#EF4444" },
            new() { Name = "日期", Path = VirtualPath.AiDates, IconKey = "calendar", IconData = Icons.Calendar, IconColor = "#F59E0B" },
            new() { Name = "文字搜索", Path = VirtualPath.AiTextSearch, IconKey = "search", IconData = Icons.Search, IconColor = "#3B82F6" },
        };
    }

}

// Enums - kept here for backward compatibility
public enum ViewMode { Grid, List }
public enum SortField { Name, Modified, Size, Type }
public enum GroupField { None, Type, Modified, Size }

public class FileGroup
{
    public string Name { get; init; } = "";
    public List<FileSystemEntry> Entries { get; init; } = [];
}
