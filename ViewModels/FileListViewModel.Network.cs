using System.Collections.ObjectModel;
using Avalonia.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    public bool IsNetworkPage => CurrentPath == VirtualPath.Network;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNetworkError))]
    private string? _networkError;
    public bool HasNetworkError => !string.IsNullOrEmpty(NetworkError);

    partial void OnNetworkErrorChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _topLevelWindow != null)
            MacExplorer.Controls.AppToast.ShowError(_topLevelWindow, value);
    }

    private void NavigateToNetwork()
    {
        NetworkError = null;
        BeginDirectoryWork();
        _navigation.SetWatchedDirectory(null);
        _ai.Reset(); _search.Reset();
        _navigation.IsHomePage = false; _navigation.IsSearchMode = false;
        _navigation.IsAiView = false; _navigation.IsArchiveView = false; _navigation.IsRemoteView = false;
        _navigation.CurrentRemoteServerId = null;
        _navigation.CurrentPath = VirtualPath.Network;
        _navigation.Breadcrumbs = new ObservableCollection<BreadcrumbSegment>
            { new() { DisplayName = LocalizationText.Get("network.title"), FullPath = VirtualPath.Network } };
        _navigation.UpdateHistoryForSentinelPath(VirtualPath.Network);
        ApplyEntries([]); ClearSelection();
        IsDirectoryLoading = IsLoading = false;
        StatusText = LocalizationText.Get("network.title");
    }

    internal async Task<bool> ConfirmNetworkUnmountAsync()
    {
        if (_topLevelWindow == null) return false;
        return await new ConfirmDialog(LocalizationText.Get("network.disconnect"), LocalizationText.Get("network.disconnect-confirm"),
            LocalizationText.Get("network.disconnect")).ShowDialog<bool>(_topLevelWindow);
    }

    public async Task DisconnectServerAsync(RemoteServerInfo server)
    {
        if (server.ServiceType == RemoteServiceType.Sftp) { DisconnectServer(server.Id); return; }
        if (!await ConfirmNetworkUnmountAsync()) return;
        NetworkError = null;
        var coordinator = App.Services?.GetService<IServerConnectionCoordinator>();
        if (coordinator != null && !await coordinator.DisconnectAsync(server))
            StatusText = NetworkError = LocalizationText.Get("network.eject-error", server.DisplayName);
    }
}
