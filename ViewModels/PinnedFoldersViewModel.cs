using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class PinnedFoldersViewModel : ObservableObject, IDisposable
{
    private readonly IPinnedFolderService? _pinnedFolderService;
    private readonly IRatingService? _ratingService;
    private readonly ISettingsService? _settingsService;
    private const string CollapsedSettingPrefix = "sidebar_group_collapsed_";
    [ObservableProperty] private ObservableCollection<PinnedFolder> _pinnedFolders = [];
    [ObservableProperty] private IReadOnlyList<PinnedFolder> _allPinnedFolders = [];
    [ObservableProperty] private IReadOnlyList<PinnedFolderGroup> _customGroups = [];
    [ObservableProperty] private string? _loadError;
    private int _loadVersion;
    private bool _disposed;

    public PinnedFoldersViewModel(IPinnedFolderService? pinnedFolderService = null, IRatingService? ratingService = null,
        ISettingsService? settingsService = null)
    {
        _pinnedFolderService = pinnedFolderService;
        _ratingService = ratingService;
        _settingsService = settingsService;
        if (_settingsService != null) _settingsService.SettingChanged += OnCollapseSettingChanged;
        if (_pinnedFolderService != null) _pinnedFolderService.Changed += OnPinsChanged;
    }

    // ── Pinned Folders ──────────────────────────────────────────────

    public async Task LoadPinnedFoldersAsync()
    {
        if (_disposed || _pinnedFolderService == null) return;
        var version = Interlocked.Increment(ref _loadVersion);
        var pins = await _pinnedFolderService.GetAllAsync();
        var groups = await _pinnedFolderService.GetGroupsAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || version != Volatile.Read(ref _loadVersion)) return;
            AllPinnedFolders = pins;
            PinnedFolders = new ObservableCollection<PinnedFolder>(pins.Where(pin => pin.GroupId == 0));
            var previous = CustomGroups.ToDictionary(group => group.Id);
            CustomGroups = groups.Select(group => new PinnedFolderGroup
            {
                Id = group.Id,
                Name = group.Name,
                Folders = pins.Where(pin => pin.GroupId == group.Id).ToArray(),
                IsCollapsed = _settingsService?.Get(CollapsedSettingPrefix + group.Id, false)
                    ?? previous.GetValueOrDefault(group.Id)?.IsCollapsed ?? false
            }).ToArray();
            LoadError = null;
        });
    }

    public void ToggleGroupCollapsed(int groupId)
    {
        var group = CustomGroups.FirstOrDefault(group => group.Id == groupId);
        if (group == null) return;
        group.IsCollapsed = !group.IsCollapsed;
        _settingsService?.Set(CollapsedSettingPrefix + group.Id, group.IsCollapsed);
    }

    private void OnCollapseSettingChanged(string key)
    {
        if (!key.StartsWith(CollapsedSettingPrefix, StringComparison.Ordinal)) return;
        void Apply()
        {
            if (_disposed || _settingsService == null) return;
            foreach (var group in CustomGroups)
                group.IsCollapsed = _settingsService.Get(CollapsedSettingPrefix + group.Id, false);
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private async void OnPinsChanged(object? sender, EventArgs e)
    {
        try { await LoadPinnedFoldersAsync(); }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (!_disposed) LoadError = ex.Message; });
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _loadVersion);
        if (_pinnedFolderService != null) _pinnedFolderService.Changed -= OnPinsChanged;
        if (_settingsService != null) _settingsService.SettingChanged -= OnCollapseSettingChanged;
    }

    public async Task PinFolderAsync(string path, string displayName, int groupId = 0)
    {
        if (_pinnedFolderService == null) return;
        await _pinnedFolderService.PinAsync(path, displayName, groupId);
        await LoadPinnedFoldersAsync();
    }

    [RelayCommand]
    public async Task UnpinFolderAsync(string path)
    {
        if (_pinnedFolderService == null) return;
        await _pinnedFolderService.UnpinAsync(path);
        await LoadPinnedFoldersAsync();
    }

    public async Task UnpinFolderAsync(string path, int groupId)
    {
        if (_pinnedFolderService == null) return;
        await _pinnedFolderService.UnpinAsync(path, groupId);
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
