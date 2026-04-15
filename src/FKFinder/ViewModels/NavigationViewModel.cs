using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FKFinder.Models;
using FKFinder.Services;

namespace FKFinder.ViewModels;

public partial class NavigationViewModel : ObservableObject
{
    private readonly IFileService _fileService;
    private readonly IFrequentFolderService? _frequentFolderService;
    private readonly IFSEventsWatcher? _fsEventsWatcher;

    // Navigation history
    private readonly List<string> _historyStack = [];
    private int _historyIndex = -1;
    private bool _isNavigatingHistory;
    private readonly Dictionary<string, string?> _pathSelectedEntries = new();

    [ObservableProperty]
    private string _currentPath = "";

    [ObservableProperty]
    private bool _isHomePage = true;

    [ObservableProperty]
    private ObservableCollection<BreadcrumbSegment> _breadcrumbs = [];

    public string HomeDirectory => _fileService.HomeDirectory;

    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex < _historyStack.Count - 1;

    // Navigation mode flags - these are checked by the coordinator
    [ObservableProperty]
    private bool _isArchiveView;

    [ObservableProperty]
    private string? _currentArchivePath;

    [ObservableProperty]
    private string _currentArchiveInternalPath = "";

    [ObservableProperty]
    private bool _isCollectionView;

    [ObservableProperty]
    private int? _currentCollectionId;

    [ObservableProperty]
    private string? _currentCollectionName;

    [ObservableProperty]
    private bool _isAiView;

    [ObservableProperty]
    private int? _currentFaceClusterId;

    [ObservableProperty]
    private string? _currentAiContextLabel;

    [ObservableProperty]
    private bool _isSearchMode;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    /// <summary>被导航到的文件名称，用于加载后自动选中</summary>
    public string? PendingSelectFileName { get; set; }

    public NavigationViewModel(
        IFileService fileService,
        IFrequentFolderService? frequentFolderService = null,
        IFSEventsWatcher? fsEventsWatcher = null)
    {
        _fileService = fileService;
        _frequentFolderService = frequentFolderService;
        _fsEventsWatcher = fsEventsWatcher;
    }

    public bool NeedsRefreshFromNotification(bool isArchiveView, bool isAiView, bool isCollectionView)
    {
        return !isArchiveView && !isAiView && !isCollectionView && !string.IsNullOrEmpty(CurrentPath);
    }

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Archive sentinel paths are handled by FileListViewModel.NavigateToArchiveAsync
        if (ArchivePathHelper.IsArchivePath(path)) return;

        // AI sentinel paths are handled by FileListViewModel.HandleAiNavigationAsync
        if (AiPathHelper.IsAiPath(path)) return;

        // Validate that the path exists on the filesystem
        // Skip validation for trash directory (macOS SIP blocks .NET Directory.Exists)
        if (path != _fileService.TrashDirectory && !Directory.Exists(path))
        {
            return; // Coordinator will set status
        }

        if (CurrentPath == path) return;

        // When navigating to a normal filesystem path from a special view,
        // reset the special view flags and associated state
        if (IsArchiveView || IsAiView || IsCollectionView)
        {
            IsArchiveView = false;
            IsAiView = false;
            IsCollectionView = false;
            CurrentArchivePath = null;
            CurrentArchiveInternalPath = "";
            CurrentCollectionId = null;
            CurrentCollectionName = null;
            CurrentFaceClusterId = null;
            CurrentAiContextLabel = null;
        }

        // Save selected entry for current path before navigating away
        // Coordinated with ApplyEntries restore logic
        if (!string.IsNullOrEmpty(CurrentPath))
            _pathSelectedEntries[CurrentPath] = null; // Will be set by coordinator if needed

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
        UpdateBreadcrumbs(path);

        // Record folder visit for frequent folders ranking
        _ = _frequentFolderService?.RecordVisitAsync(path);
    }

    [RelayCommand]
    public async Task NavigateUpAsync(string? parentPath)
    {
        if (IsHomePage) return;
        if (parentPath != null && parentPath != CurrentPath)
            await NavigateToAsync(parentPath);
    }

    [RelayCommand]
    public Task NavigateBackAsync()
    {
        if (!CanGoBack) return Task.CompletedTask;
        _historyIndex--;
        _isNavigatingHistory = true;
        CurrentPath = _historyStack[_historyIndex];
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        return Task.CompletedTask;
    }

    [RelayCommand]
    public Task NavigateForwardAsync()
    {
        if (!CanGoForward) return Task.CompletedTask;
        _historyIndex++;
        _isNavigatingHistory = true;
        CurrentPath = _historyStack[_historyIndex];
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by coordinator after back/forward content reload is complete.
    /// </summary>
    public void EndHistoryNavigation()
    {
        _isNavigatingHistory = false;
    }

    [RelayCommand]
    public void GoHome()
    {
        // Unwatch FSEvents when leaving normal directory view
        if (!string.IsNullOrEmpty(CurrentPath))
            _fsEventsWatcher?.UnwatchDirectory(CurrentPath);

        IsHomePage = true;
        CurrentPath = "";
        Breadcrumbs.Clear();
    }

    public void UpdateHistoryForSentinelPath(string sentinelPath)
    {
        if (!_isNavigatingHistory)
        {
            if (_historyIndex < _historyStack.Count - 1)
                _historyStack.RemoveRange(_historyIndex + 1, _historyStack.Count - _historyIndex - 1);
            _historyStack.Add(sentinelPath);
            _historyIndex = _historyStack.Count - 1;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
    }

    public string? GetSavedSelectedEntryName(string path)
    {
        return _pathSelectedEntries.TryGetValue(path, out var name) ? name : null;
    }

    public void SaveSelectedEntryForPath(string path, string? entryName)
    {
        if (!string.IsNullOrEmpty(path))
            _pathSelectedEntries[path] = entryName;
    }

    private void UpdateBreadcrumbs(string path)
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
            // Breadcrumbs updated by AiViewModel separately
            return;
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

    public void UpdateBreadcrumbsForAi(string modeName, string modePath, string? contextLabel)
    {
        var segments = new List<BreadcrumbSegment>
        {
            new() { Name = "AI 智能", FullPath = "", HasDropdown = false },
            new() { Name = modeName, FullPath = modePath, HasDropdown = contextLabel != null }
        };
        if (contextLabel != null)
        {
            segments.Add(new BreadcrumbSegment { Name = contextLabel, FullPath = CurrentPath, HasDropdown = false });
        }
        Breadcrumbs = new ObservableCollection<BreadcrumbSegment>(segments);
    }

    public void UpdateBreadcrumbsForArchive()
    {
        UpdateBreadcrumbs(CurrentPath);
    }

    public void UpdateBreadcrumbs()
    {
        UpdateBreadcrumbs(CurrentPath);
    }

    public void ClearBreadcrumbs()
    {
        Breadcrumbs.Clear();
    }

    public void WatchCurrentDirectory()
    {
        if (!string.IsNullOrEmpty(CurrentPath))
            _fsEventsWatcher?.WatchDirectory(CurrentPath);
    }

    public void UnwatchCurrentDirectory(string? oldPath)
    {
        if (!string.IsNullOrEmpty(oldPath) && oldPath != CurrentPath)
            _fsEventsWatcher?.UnwatchDirectory(oldPath);
    }

    public bool IsInTrash => CurrentPath == _fileService.TrashDirectory;
}