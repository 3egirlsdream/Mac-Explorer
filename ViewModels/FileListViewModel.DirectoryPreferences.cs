using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.Logging;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private IDirectoryPreferencesService? _directoryPreferences;
    private DirectoryPreferences? _directoryDefaults;
    private bool _restoringDirectoryPreferences;
    private DirectoryPreferences? _pendingRestoredPreferences;
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
    internal void PrepareDirectoryPreferences(DirectoryPreferences? preferences) => _pendingRestoredPreferences = preferences;
    private static bool IsPreferenceDirectory(string path) => Path.IsPathFullyQualified(path) || VirtualPath.IsRemotePath(path);
    private void SaveDirectoryPreferences()
    {
        if (_directoryPreferences == null || _restoringDirectoryPreferences || _disposed || _restoredPreferencePath != CurrentPath || !IsPreferenceDirectory(CurrentPath) || IsSearchMode) return;
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
        if (_directoryPreferences == null || !IsPreferenceDirectory(path)) return;
        if (explicitState == null && _pendingRestoredPreferences == null && _restoredPreferencePath == path) return;
        var state = explicitState ?? _pendingRestoredPreferences ?? await _directoryPreferences.ReadAsync(path) ?? _directoryDefaults;
        _pendingRestoredPreferences = null;
        if (CurrentPath != path || state == null || _disposed) return;
        _restoredPreferencePath = path;
        ApplyDirectoryPreferences(state);
    }
    private void ApplyDirectoryPreferences(DirectoryPreferences state)
    {
        _restoringDirectoryPreferences = true;
        try
        {
            if (Enum.IsDefined(state.Sort)) _sortFilter.SortField = state.Sort;
            _sortFilter.SortAscending = state.Ascending;
            if (Enum.IsDefined(state.Priority)) _sortFilter.SortPriority = state.Priority;
            _sortFilter.ColumnFilters = state.Filters ?? Models.FileListFilterState.Empty;
            _columnLayoutService.Restore(state.Columns ?? []);
            IsPreviewPaneVisible = state.PreviewPane; IsMetadataPanelVisible = state.MetadataPanel; IsInfoPanelVisible = state.InfoPanel;
            OnPropertyChanged(nameof(ColumnFilters)); OnPropertyChanged(nameof(FileNameFilter)); OnPropertyChanged(nameof(HasFileListFilters));
        }
        finally { _restoringDirectoryPreferences = false; }
    }
    public async Task ResetDirectoryPreferencesAsync()
    {
        if (_directoryPreferences == null || _directoryDefaults == null || !IsPreferenceDirectory(CurrentPath)) return;
        await _directoryPreferences.RemoveAsync(CurrentPath);
        ApplyDirectoryPreferences(_directoryDefaults); await RefreshAsync();
    }
}
