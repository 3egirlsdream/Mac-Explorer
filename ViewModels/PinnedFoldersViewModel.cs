using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class PinnedFoldersViewModel : ObservableObject
{
    private readonly IPinnedFolderService? _pinnedFolderService;
    private readonly IRatingService? _ratingService;
    [ObservableProperty] private ObservableCollection<PinnedFolder> _pinnedFolders = [];

    public PinnedFoldersViewModel(IPinnedFolderService? pinnedFolderService = null, IRatingService? ratingService = null)
    {
        _pinnedFolderService = pinnedFolderService;
        _ratingService = ratingService;
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

    public async Task ReorderPinnedFolderAsync(string sourcePath, string targetPath)
    {
        if (_pinnedFolderService == null || string.Equals(sourcePath, targetPath, StringComparison.Ordinal)) return;
        var ordered = PinnedFolders.Select(folder => folder.FolderPath).ToList();
        var sourceIndex = ordered.FindIndex(path => string.Equals(path, sourcePath, StringComparison.Ordinal));
        var targetIndex = ordered.FindIndex(path => string.Equals(path, targetPath, StringComparison.Ordinal));
        if (sourceIndex < 0 || targetIndex < 0) return;
        var item = ordered[sourceIndex];
        ordered.RemoveAt(sourceIndex);
        targetIndex = ordered.FindIndex(path => string.Equals(path, targetPath, StringComparison.Ordinal));
        ordered.Insert(targetIndex, item);
        await _pinnedFolderService.ReorderAsync(ordered);
        await LoadPinnedFoldersAsync();
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

}
