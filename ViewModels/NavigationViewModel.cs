using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public sealed class NavigationHistoryEntry
{
    public string Path { get; set; } = string.Empty;
    public bool IsHomePage { get; set; }
    public bool IsSearchMode { get; set; }
    public string SearchQuery { get; set; } = string.Empty;
    public string? SearchRootPath { get; set; }
    public bool WasHomePageBeforeSearch { get; set; }
    public IReadOnlyList<FileSystemEntry> SearchResults { get; set; } = [];
    public string? SelectedEntryPath { get; set; }
    public string? SelectedEntryName { get; set; }
    public double? SelectedEntryViewportY { get; set; }
    public double? SelectedEntryScrollOffsetY { get; set; }
}

public partial class NavigationViewModel : ObservableObject
{
    private readonly IFileService _fileService;
    private readonly IFrequentFolderService? _frequentFolderService;
    private readonly IFSEventsWatcher? _fsEventsWatcher;
    private readonly IDisplayNameService? _displayNameService;

    // Navigation history
    private readonly List<NavigationHistoryEntry> _historyStack = [];
    private int _historyIndex = -1;
    private bool _isNavigatingHistory;
    private readonly Dictionary<string, string?> _pathSelectedEntries = new();
    private string? _watchedDirectory;
    private readonly Dictionary<string, string> _localizedNames = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string _currentPath = "";

    [ObservableProperty]
    private bool _isHomePage = true;

    [ObservableProperty]
    private ObservableCollection<BreadcrumbSegment> _breadcrumbs = [];

    public string HomeDirectory => _fileService.HomeDirectory;

    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex < _historyStack.Count - 1;
    public NavigationHistoryEntry? CurrentHistoryEntry
        => _historyIndex >= 0 && _historyIndex < _historyStack.Count
            ? _historyStack[_historyIndex]
            : null;

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

    [ObservableProperty]
    private bool _isRemoteView;

    [ObservableProperty]
    private string? _currentRemoteServerId;

    /// <summary>被导航到的文件名称，用于加载后自动选中</summary>
    public string? PendingSelectFileName { get; set; }

    public NavigationViewModel(
        IFileService fileService,
        IFrequentFolderService? frequentFolderService = null,
        IFSEventsWatcher? fsEventsWatcher = null,
        IDisplayNameService? displayNameService = null)
    {
        _fileService = fileService;
        _frequentFolderService = frequentFolderService;
        _fsEventsWatcher = fsEventsWatcher;
        _displayNameService = displayNameService;
    }

    private string Localize(string fullPath, string fallback)
        => _localizedNames.GetValueOrDefault(fullPath, fallback);

    public bool NeedsRefreshFromNotification(bool isArchiveView, bool isAiView, bool isCollectionView)
    {
        return !isArchiveView && !isAiView && !isCollectionView && !IsSearchMode
            && !TagPathHelper.IsTagPath(CurrentPath)
            && !string.IsNullOrEmpty(CurrentPath);
    }

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Archive sentinel paths are handled by FileListViewModel.NavigateToArchiveAsync
        if (ArchivePathHelper.IsArchivePath(path)) return;

        // AI sentinel paths are handled by FileListViewModel.HandleAiNavigationAsync
        if (AiPathHelper.IsAiPath(path)) return;

        // The coordinator validates off-thread while the destination shows its placeholder.

        if (CurrentPath == path && !IsSearchMode) return;

        // When navigating to a normal filesystem path from a special view,
        // reset the special view flags and associated state
        if (IsArchiveView || IsAiView || IsCollectionView || IsRemoteView)
        {
            IsArchiveView = false;
            IsAiView = false;
            IsCollectionView = false;
            IsRemoteView = false;
            CurrentArchivePath = null;
            CurrentArchiveInternalPath = "";
            CurrentCollectionId = null;
            CurrentCollectionName = null;
            CurrentFaceClusterId = null;
            CurrentAiContextLabel = null;
            CurrentRemoteServerId = null;
        }

        // Save selected entry for current path before navigating away
        // Coordinated with ApplyEntries restore logic
        if (!string.IsNullOrEmpty(CurrentPath))
            _pathSelectedEntries[CurrentPath] = null; // Will be set by coordinator if needed

        // Record history for back/forward navigation
        if (!_isNavigatingHistory)
        {
            // Trim forward history when navigating to a new path
            PushHistoryEntry(CreatePathHistoryEntry(path));
        }

        IsHomePage = false;
        IsSearchMode = false;
        SearchQuery = string.Empty;
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
        ApplyHistoryEntry(_historyStack[_historyIndex]);
        NotifyHistoryChanged();
        return Task.CompletedTask;
    }

    [RelayCommand]
    public Task NavigateForwardAsync()
    {
        if (!CanGoForward) return Task.CompletedTask;
        _historyIndex++;
        _isNavigatingHistory = true;
        ApplyHistoryEntry(_historyStack[_historyIndex]);
        NotifyHistoryChanged();
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
        SetWatchedDirectory(null);
        IsArchiveView = false;
        IsAiView = false;
        IsCollectionView = false;
        IsRemoteView = false;
        IsSearchMode = false;
        SearchQuery = string.Empty;
        CurrentArchivePath = null;
        CurrentArchiveInternalPath = "";
        CurrentCollectionId = null;
        CurrentCollectionName = null;
        CurrentFaceClusterId = null;
        CurrentAiContextLabel = null;
        CurrentRemoteServerId = null;
        IsHomePage = true;
        CurrentPath = "";
        Breadcrumbs.Clear();
    }

    public void UpdateHistoryForSentinelPath(string sentinelPath)
    {
        if (!_isNavigatingHistory)
        {
            PushHistoryEntry(CreatePathHistoryEntry(sentinelPath));
        }
    }

    public void PushOrReplaceSearchHistory(
        string query,
        string? searchRootPath,
        bool wasHomePageBeforeSearch,
        IReadOnlyList<FileSystemEntry> searchResults)
    {
        var current = CurrentHistoryEntry;
        if (current?.IsSearchMode == true)
        {
            UpdateSearchHistoryEntry(current, query, searchRootPath, wasHomePageBeforeSearch, searchResults);
            TrimForwardHistory();
            NotifyHistoryChanged();
            return;
        }

        var entry = new NavigationHistoryEntry
        {
            Path = CurrentPath,
            IsHomePage = false,
            IsSearchMode = true,
            SearchQuery = query,
            SearchRootPath = searchRootPath,
            WasHomePageBeforeSearch = wasHomePageBeforeSearch,
            SearchResults = searchResults.ToArray()
        };
        PushHistoryEntry(entry);
    }

    public void UpdateCurrentSearchResults(IReadOnlyList<FileSystemEntry> searchResults)
    {
        if (CurrentHistoryEntry is { IsSearchMode: true } entry)
            entry.SearchResults = searchResults.ToArray();
    }

    public void SaveCurrentHistorySelection(
        string? selectedEntryPath,
        string? selectedEntryName,
        double? selectedEntryViewportY,
        double? selectedEntryScrollOffsetY)
    {
        var entry = CurrentHistoryEntry;
        if (entry == null) return;

        entry.SelectedEntryPath = selectedEntryPath;
        entry.SelectedEntryName = selectedEntryName;
        entry.SelectedEntryViewportY = selectedEntryViewportY;
        entry.SelectedEntryScrollOffsetY = selectedEntryScrollOffsetY;

        if (!entry.IsSearchMode && !string.IsNullOrEmpty(entry.Path))
            _pathSelectedEntries[entry.Path] = selectedEntryName;
    }

    public void RemoveCurrentSearchHistory()
    {
        if (CurrentHistoryEntry?.IsSearchMode != true) return;

        if (_historyIndex > 0)
        {
            _historyStack.RemoveAt(_historyIndex);
            _historyIndex--;
            TrimForwardHistory();
        }
        else
        {
            _historyStack.Clear();
            _historyIndex = -1;
        }

        NotifyHistoryChanged();
    }

    public string? GetSavedSelectedEntryName(string path)
    {
        return _pathSelectedEntries.TryGetValue(path, out var name) ? name : null;
    }

    public string? GetSavedSelectedEntryPath(string path)
    {
        return CurrentHistoryEntry?.SelectedEntryPath;
    }

    public void SaveSelectedEntryForPath(string path, string? entryName)
    {
        if (!string.IsNullOrEmpty(path))
            _pathSelectedEntries[path] = entryName;
    }

    private static NavigationHistoryEntry CreatePathHistoryEntry(string path)
        => new() { Path = path };

    private void PushHistoryEntry(NavigationHistoryEntry entry)
    {
        TrimForwardHistory();

        _historyStack.Add(entry);
        _historyIndex = _historyStack.Count - 1;
        NotifyHistoryChanged();
    }

    private void TrimForwardHistory()
    {
        if (_historyIndex < _historyStack.Count - 1)
            _historyStack.RemoveRange(_historyIndex + 1, _historyStack.Count - _historyIndex - 1);
    }

    private static void UpdateSearchHistoryEntry(
        NavigationHistoryEntry entry,
        string query,
        string? searchRootPath,
        bool wasHomePageBeforeSearch,
        IReadOnlyList<FileSystemEntry> searchResults)
    {
        entry.IsHomePage = false;
        entry.IsSearchMode = true;
        entry.SearchQuery = query;
        entry.SearchRootPath = searchRootPath;
        entry.WasHomePageBeforeSearch = wasHomePageBeforeSearch;
        entry.SearchResults = searchResults.ToArray();
        entry.SelectedEntryPath = null;
        entry.SelectedEntryName = null;
        entry.SelectedEntryViewportY = null;
        entry.SelectedEntryScrollOffsetY = null;
    }

    private void ApplyHistoryEntry(NavigationHistoryEntry entry)
    {
        IsHomePage = entry.IsHomePage;
        IsSearchMode = entry.IsSearchMode;
        SearchQuery = entry.IsSearchMode ? entry.SearchQuery : string.Empty;
        CurrentPath = entry.Path;
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CurrentHistoryEntry));
    }

    private void UpdateBreadcrumbs(string path)
    {
        var segments = new List<BreadcrumbSegment>();

        if (TagPathHelper.TryParse(CurrentPath, out var currentTag))
        {
            segments.Add(new BreadcrumbSegment { Name = "标签", DisplayName = "标签", FullPath = "", HasDropdown = false });
            segments.Add(new BreadcrumbSegment
            {
                Name = currentTag.Name,
                DisplayName = currentTag.Name,
                FullPath = CurrentPath,
                HasDropdown = false
            });
        }
        else if (IsCollectionView && CurrentCollectionName != null)
        {
            segments.Add(new BreadcrumbSegment { Name = "收藏夹", DisplayName = "收藏夹", FullPath = "", HasDropdown = false });
            segments.Add(new BreadcrumbSegment { Name = CurrentCollectionName, DisplayName = CurrentCollectionName, FullPath = "", HasDropdown = false });
        }
        else if (IsArchiveView && CurrentArchivePath != null)
        {
            var archiveDir = Path.GetDirectoryName(CurrentArchivePath) ?? "/";
            segments.Add(new BreadcrumbSegment { Name = "/", DisplayName = Localize("/", "/"), FullPath = "/", HasDropdown = true });
            var dirParts = archiveDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var buildPath = "";
            foreach (var part in dirParts)
            {
                buildPath += "/" + part;
                segments.Add(new BreadcrumbSegment { Name = part, DisplayName = Localize(buildPath, part), FullPath = buildPath, HasDropdown = true });
            }
            var archiveName = Path.GetFileName(CurrentArchivePath);
            segments.Add(new BreadcrumbSegment
            {
                Name = archiveName,
                DisplayName = archiveName,
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
                        DisplayName = internalParts[i],
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
        else if (IsRemoteView && VirtualPath.IsRemotePath(CurrentPath))
        {
            var (_, remotePath) = VirtualPath.ParseRemotePath(CurrentPath);
            UpdateBreadcrumbsForRemote(remotePath);
            return;
        }
        else if (CurrentPath == _fileService.TrashDirectory)
        {
            segments.Add(new BreadcrumbSegment { Name = "废纸篓", DisplayName = Localize(Path.Combine(_fileService.HomeDirectory, ".Trash"), "废纸篓"), FullPath = CurrentPath, HasDropdown = false });
        }
        else if (CurrentPath == "/")
        {
            segments.Add(new BreadcrumbSegment { Name = "/", DisplayName = Localize("/", "/"), FullPath = "/", HasDropdown = false });
        }
        else
        {
            segments.Add(new BreadcrumbSegment { Name = "/", DisplayName = Localize("/", "/"), FullPath = "/", HasDropdown = true });
            var parts = CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var buildPath = "";
            for (int i = 0; i < parts.Length; i++)
            {
                buildPath += "/" + parts[i];
                segments.Add(new BreadcrumbSegment { Name = parts[i], DisplayName = Localize(buildPath, parts[i]), FullPath = buildPath, HasDropdown = i < parts.Length - 1 });
            }
        }
        Breadcrumbs = new ObservableCollection<BreadcrumbSegment>(segments);
        _ = LocalizeBreadcrumbsAsync(Breadcrumbs);
    }

    private async Task LocalizeBreadcrumbsAsync(ObservableCollection<BreadcrumbSegment> breadcrumbs)
    {
        if (_displayNameService == null) return;
        var pending = breadcrumbs.Where(segment => Path.IsPathRooted(segment.FullPath)
            && !_localizedNames.ContainsKey(segment.FullPath)).ToArray();
        if (pending.Length == 0) return;
        var names = await Task.Run(() => pending.Select(segment =>
        {
            try { return _displayNameService.GetDisplayName(segment.FullPath); }
            catch { return segment.DisplayName; }
        }).ToArray());
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            for (var i = 0; i < pending.Length; i++)
                _localizedNames[pending[i].FullPath] = string.IsNullOrWhiteSpace(names[i]) ? pending[i].DisplayName : names[i];
            if (!ReferenceEquals(Breadcrumbs, breadcrumbs)) return;
            Breadcrumbs = new ObservableCollection<BreadcrumbSegment>(breadcrumbs.Select(segment => new BreadcrumbSegment
            {
                Name = segment.Name,
                DisplayName = Localize(segment.FullPath, segment.DisplayName),
                FullPath = segment.FullPath,
                HasDropdown = segment.HasDropdown,
                Siblings = segment.Siblings
            }));
        });
    }

    public void UpdateBreadcrumbsForAi(string modeName, string modePath, string? contextLabel)
    {
        var segments = new List<BreadcrumbSegment>
        {
            new() { Name = "首页", DisplayName = "首页", FullPath = VirtualPath.Home, HasDropdown = false },
            new() { Name = modeName, DisplayName = modeName, FullPath = modePath, HasDropdown = contextLabel != null }
        };
        if (contextLabel != null)
        {
            segments.Add(new BreadcrumbSegment { Name = contextLabel, DisplayName = contextLabel, FullPath = CurrentPath, HasDropdown = false });
        }
        Breadcrumbs = new ObservableCollection<BreadcrumbSegment>(segments);
    }

    public void UpdateBreadcrumbsForArchive()
    {
        UpdateBreadcrumbs(CurrentPath);
    }

    public void UpdateBreadcrumbsForRemote(string remotePath)
    {
        var serverId = CurrentRemoteServerId ?? "";
        var segments = new List<BreadcrumbSegment>
        {
            new() { Name = "远程服务器", DisplayName = "远程服务器", FullPath = "", HasDropdown = false }
        };

        if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
        {
            segments.Add(new() { Name = "/", DisplayName = "/", FullPath = VirtualPath.BuildRemotePath(serverId, "/"), HasDropdown = false });
        }
        else
        {
            segments.Add(new() { Name = "/", DisplayName = "/", FullPath = VirtualPath.BuildRemotePath(serverId, "/"), HasDropdown = true });
            var parts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var buildPath = "";
            for (int i = 0; i < parts.Length; i++)
            {
                buildPath += "/" + parts[i];
                segments.Add(new BreadcrumbSegment
                {
                    Name = parts[i],
                    DisplayName = parts[i],
                    FullPath = VirtualPath.BuildRemotePath(serverId, buildPath),
                    HasDropdown = i < parts.Length - 1
                });
            }
        }

        Breadcrumbs = new ObservableCollection<BreadcrumbSegment>(segments);
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
        SetWatchedDirectory(CurrentPath);
    }

    public void UnwatchCurrentDirectory(string? oldPath)
    {
        if (!string.IsNullOrEmpty(oldPath) && string.Equals(oldPath, _watchedDirectory, StringComparison.Ordinal))
            SetWatchedDirectory(null);
    }

    public void SetWatchedDirectory(string? path)
    {
        var normalized = NormalizeWatchPath(path);
        if (string.Equals(normalized, _watchedDirectory, StringComparison.Ordinal))
            return;

        if (_watchedDirectory != null)
            _fsEventsWatcher?.UnwatchDirectory(_watchedDirectory);

        _watchedDirectory = normalized;
        if (_watchedDirectory != null)
            _fsEventsWatcher?.WatchDirectory(_watchedDirectory);
    }

    private static string? NormalizeWatchPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;

        return path == "/"
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public bool IsInTrash => CurrentPath == _fileService.TrashDirectory;
}
