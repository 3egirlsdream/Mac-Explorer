using System.Collections.ObjectModel;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    public bool IsDiskManagementPage => CurrentPath == VirtualPath.Disks;
    public event Action? DiskManagementRefreshRequested;

    private void NavigateToDisks()
    {
        BeginDirectoryWork();
        _navigation.SetWatchedDirectory(null);
        _ai.Reset();
        _search.Reset();
        _navigation.IsHomePage = false;
        _navigation.IsSearchMode = false;
        _navigation.IsAiView = false;
        _navigation.IsArchiveView = false;
        _navigation.IsRemoteView = false;
        _navigation.CurrentRemoteServerId = null;
        _navigation.CurrentPath = VirtualPath.Disks;
        _navigation.Breadcrumbs = new ObservableCollection<BreadcrumbSegment>
        {
            new() { DisplayName = LocalizationText.Get("disk.title"), FullPath = VirtualPath.Disks, HasDropdown = false }
        };
        _navigation.UpdateHistoryForSentinelPath(VirtualPath.Disks);
        ApplyEntries([]);
        ClearSelection();
        IsDirectoryLoading = IsLoading = false;
        StatusText = LocalizationText.Get("disk.title");
    }
}
