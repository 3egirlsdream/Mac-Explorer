using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.Logging;

namespace MacExplorer.ViewModels;

public partial class CollectionViewModel : ObservableObject
{
    private readonly ICollectionService? _collectionService;
    private readonly IRatingService? _ratingService;
    private readonly IFileIndex? _fileIndex;
    private readonly IFileService? _fileService;
    private readonly IPinnedFolderService? _pinnedFolderService;

    internal ICollectionService? CollectionService => _collectionService;
    private readonly Microsoft.Extensions.Logging.ILogger<CollectionViewModel>? _logger;

    [ObservableProperty]
    private ObservableCollection<Collection> _collections = [];

    [ObservableProperty]
    private bool _isCollectionView;

    [ObservableProperty]
    private int? _currentCollectionId;

    [ObservableProperty]
    private string? _currentCollectionName;

    [ObservableProperty]
    private ObservableCollection<PinnedFolder> _pinnedFolders = [];

    public CollectionViewModel(
        ICollectionService? collectionService = null,
        IRatingService? ratingService = null,
        IFileIndex? fileIndex = null,
        IFileService? fileService = null,
        IPinnedFolderService? pinnedFolderService = null,
        Microsoft.Extensions.Logging.ILogger<CollectionViewModel>? logger = null)
    {
        _collectionService = collectionService;
        _ratingService = ratingService;
        _fileIndex = fileIndex;
        _fileService = fileService;
        _pinnedFolderService = pinnedFolderService;
        _logger = logger;
    }

    [RelayCommand]
    public async Task LoadCollectionsAsync()
    {
        if (_collectionService == null) return;
        try
        {
            var list = await _collectionService.GetAllCollectionsAsync();
            Collections = new ObservableCollection<Collection>(list);
        }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to load collections"); }
    }

    public async Task NavigateToCollectionAsync(
        int collectionId,
        Action<bool> setIsHomePage,
        Action<bool> setIsCollectionView,
        Action<bool> setIsAiView,
        Action<int?> setCurrentFaceClusterId,
        Action<string?> setCurrentAiContextLabel,
        Action<string?> setCurrentArchivePath,
        Action<string> setCurrentArchiveInternalPath,
        Action<bool> setIsSearchMode,
        Action<int?> setCurrentCollectionId,
        Action<string?> setCurrentCollectionName,
        Action<bool> setLoading,
        Action<ObservableCollection<FileSystemEntry>> applyEntries,
        Action<string> setStatus,
        Action updateBreadcrumbs,
        Action<ObservableCollection<PinnedFolder>> setPinnedFolders)
    {
        if (_collectionService == null || _fileIndex == null) return;

        setIsHomePage(false);
        setIsCollectionView(true);
        setIsAiView(false);
        setCurrentFaceClusterId(null);
        setCurrentAiContextLabel(null);
        setCurrentArchivePath(null);
        setCurrentArchiveInternalPath("");
        setIsSearchMode(false);
        setCurrentCollectionId(collectionId);
        setLoading(true);

        try
        {
            var collection = Collections.FirstOrDefault(c => c.Id == collectionId);
            setCurrentCollectionName(collection?.Name ?? "收藏夹");

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

            setCurrentCollectionId(collectionId);
            updateBreadcrumbs();
            applyEntries(new ObservableCollection<FileSystemEntry>(entries));

            if (removed > 0)
                setStatus($"{entries.Count} 项 ({removed} 项已移除)");
            else
                setStatus($"{entries.Count} 项");

            if (_ratingService != null && entries.Count > 0)
            {
                try { await _ratingService.BatchLoadRatingsAsync(entries.Select(e => e.FullPath)); }
                catch (Exception ex) { _logger?.LogError(ex, "Failed to batch load ratings"); }
            }
        }
        catch (Exception ex)
        {
            setStatus($"打开收藏夹失败: {ex.Message}");
        }
        finally
        {
            setLoading(false);
        }
    }

    public async Task AddToCollectionAsync(
        int collectionId,
        string filePath,
        Action<string> setStatus)
    {
        if (_collectionService == null) return;
        await _collectionService.AddFileToCollectionAsync(collectionId, filePath);
        var collection = Collections.FirstOrDefault(c => c.Id == collectionId);
        setStatus($"已添加到 {collection?.Name ?? "收藏夹"}");
    }

    public async Task RemoveFromCollectionAsync(
        int currentCollectionId,
        string filePath,
        Func<Task> navigateCallback)
    {
        if (_collectionService == null) return;
        await _collectionService.RemoveFileFromCollectionAsync(currentCollectionId, filePath);
        await navigateCallback();
    }

    public bool IsCollectionNameDuplicate(string name, int? excludeId = null)
    {
        return Collections.Any(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
            && c.Id != excludeId);
    }

    public async Task CreateCollectionAsync(string name, Action<string> setStatus)
    {
        if (_collectionService == null || string.IsNullOrWhiteSpace(name)) return;
        if (IsCollectionNameDuplicate(name))
        {
            setStatus($"收藏夹 \"{name}\" 已存在");
            return;
        }
        await _collectionService.CreateCollectionAsync(name);
        await LoadCollectionsAsync();
    }

    public async Task RenameCollectionAsync(
        int id,
        string newName,
        bool isCollectionView,
        int? currentCollectionId,
        Action<string?> setCurrentCollectionName,
        Action<string> setStatus)
    {
        if (_collectionService == null || string.IsNullOrWhiteSpace(newName)) return;
        if (IsCollectionNameDuplicate(newName, id))
        {
            setStatus($"收藏夹 \"{newName}\" 已存在");
            return;
        }
        await _collectionService.RenameCollectionAsync(id, newName);
        await LoadCollectionsAsync();
        if (isCollectionView && currentCollectionId == id)
            setCurrentCollectionName(newName);
    }

    public async Task DeleteCollectionAsync(int id, bool isCollectionView, int? currentCollectionId, Action goHome)
    {
        if (_collectionService == null) return;
        await _collectionService.DeleteCollectionAsync(id);
        await LoadCollectionsAsync();
        if (isCollectionView && currentCollectionId == id)
            goHome();
    }

    // ── Pinned Folders ──────────────────────────────────────────────

    public async Task LoadPinnedFoldersAsync()
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

    [RelayCommand]
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

    // ── Star Ratings ──

    public int GetRating(string filePath)
    {
        return _ratingService?.GetRatingCached(filePath) ?? 0;
    }

    public async Task SetRatingAsync(string filePath, int rating, Action? notifyEntriesChanged = null)
    {
        if (_ratingService == null) return;
        await _ratingService.SetRatingAsync(filePath, rating);
        notifyEntriesChanged?.Invoke();
    }

    public void Reset()
    {
        IsCollectionView = false;
        CurrentCollectionId = null;
        CurrentCollectionName = null;
    }
}