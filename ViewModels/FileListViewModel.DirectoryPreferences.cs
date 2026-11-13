using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.Logging;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private IDirectoryPreferencesService? _directoryPreferences;
    private DirectoryPreferences? _directoryDefaults;
    private bool _restoringDirectoryPreferences;
    private (string Path, FolderTabViewState? ViewState, DirectoryPreferences? Preferences)? _pendingTabViewState;
    private string? _restoredPreferencePath;
    internal void UseDirectoryPreferences(IDirectoryPreferencesService service)
    {
        _directoryPreferences = service; _directoryDefaults = CaptureDirectoryPreferences();
        _sortFilter.DirectoryScoped = _columnLayoutService.DirectoryScoped = true;
        _columnLayoutService.Committed += SaveDirectoryPreferences;
    }
    internal DirectoryPreferences CaptureDirectoryPreferences()
    {
        var widths = _columnLayoutService.PreferredWidths;
        return new(SortField, SortAscending, SortPriority, ColumnFilters,
            [widths.Name, widths.Modified, widths.Size, widths.Type], IsPreviewPaneVisible, IsMetadataPanelVisible, IsInfoPanelVisible);
    }
    internal FolderTabViewState CaptureTabViewState()
    {
        if (_pendingTabViewState is { } pending && pending.Path == CurrentPath)
            return pending.ViewState ?? new(ViewMode, GroupField, pending.Preferences ?? CaptureDirectoryPreferences());
        return new(ViewMode, GroupField, CaptureDirectoryPreferences());
    }
    internal void PrepareTabViewState(string path, FolderTabViewState? viewState, DirectoryPreferences? preferences = null)
        => _pendingTabViewState = (path, viewState, preferences);
    private static bool IsPreferenceDirectory(string path) => Path.IsPathFullyQualified(path) || VirtualPath.IsRemotePath(path);
    private void SaveDirectoryPreferences()
    {
        if (_directoryPreferences == null || _restoringDirectoryPreferences || _pendingTabViewState != null || _disposed || _restoredPreferencePath != CurrentPath || !IsPreferenceDirectory(CurrentPath) || IsSearchMode) return;
        var path = CurrentPath; var snapshot = CaptureDirectoryPreferences();
        _ = SaveDirectoryPreferencesAsync(path, snapshot);
    }
    private async Task SaveDirectoryPreferencesAsync(string path, DirectoryPreferences snapshot)
    {
        try { await _directoryPreferences!.SaveAsync(path, snapshot); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Unable to save directory preferences"); }
    }
    private async Task RestoreDirectoryPreferencesAsync(string path, DirectoryPreferences? explicitState = null)
    {
        if (!IsPreferenceDirectory(path) || (_directoryPreferences == null && _pendingTabViewState == null)) return;
        var pending = _pendingTabViewState;
        _pendingTabViewState = null;
        if (pending?.Path != path) pending = null;
        if (explicitState == null && pending == null && _restoredPreferencePath == path) return;
        var viewState = pending?.ViewState;
        var state = viewState?.Preferences ?? explicitState ?? pending?.Preferences
            ?? (_directoryPreferences == null ? null : await _directoryPreferences.ReadAsync(path)) ?? _directoryDefaults;
        if (CurrentPath != path || state == null || _disposed) return;
        _restoredPreferencePath = path;
        ApplyDirectoryPreferences(state, viewState);
    }
    private void ApplyDirectoryPreferences(DirectoryPreferences state, FolderTabViewState? viewState = null)
    {
        var previousViewMode = ViewMode;
        var suppressPersistence = _sortFilter.SuppressSettingsPersistence;
        _sortFilter.SuppressSettingsPersistence = true;
        _restoringDirectoryPreferences = true;
        try
        {
            if (viewState != null)
            {
                if (Enum.IsDefined(viewState.ViewMode)) _sortFilter.ViewMode = viewState.ViewMode;
                if (Enum.IsDefined(viewState.GroupField)) _sortFilter.GroupField = viewState.GroupField;
            }
            if (Enum.IsDefined(state.Sort)) _sortFilter.SortField = state.Sort;
            _sortFilter.SortAscending = state.Ascending;
            if (Enum.IsDefined(state.Priority)) _sortFilter.SortPriority = state.Priority;
            _sortFilter.ColumnFilters = state.Filters ?? Models.FileListFilterState.Empty;
            _columnLayoutService.Restore(state.Columns ?? []);
            IsPreviewPaneVisible = state.PreviewPane; IsMetadataPanelVisible = state.MetadataPanel; IsInfoPanelVisible = state.InfoPanel;
            OnPropertyChanged(nameof(ColumnFilters)); OnPropertyChanged(nameof(FileNameFilter)); OnPropertyChanged(nameof(HasFileListFilters));
        }
        finally
        {
            _restoringDirectoryPreferences = false;
            _sortFilter.SuppressSettingsPersistence = suppressPersistence;
        }
        if (ViewMode != previousViewMode) OnTreeModeChanged();
    }
    public async Task ResetDirectoryPreferencesAsync()
    {
        if (_directoryPreferences == null || _directoryDefaults == null || !IsPreferenceDirectory(CurrentPath)) return;
        await _directoryPreferences.RemoveAsync(CurrentPath);
        ApplyDirectoryPreferences(_directoryDefaults); await RefreshAsync();
    }
}
