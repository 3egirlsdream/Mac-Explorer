using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.ViewModels;
using MacExplorer.Services;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class FinderSidebarView : UserControl
{
    private Collection? _editingCollection;

    public FinderSidebarView()
    {
        InitializeComponent();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel != null)
        {
            ViewModel.PinnedFolders.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(UpdateActiveStates);
            ViewModel.Collections.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(UpdateActiveStates);
            ViewModel.ExternalVolumes.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(UpdateActiveStates);
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateActiveStates();
            UpdateChevronState();
            RefreshRemoteServersList();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.CurrentPath)
            || e.PropertyName == nameof(FileListViewModel.IsAiView)
            || e.PropertyName == nameof(FileListViewModel.IsCollectionView)
            || e.PropertyName == nameof(FileListViewModel.IsTrashActive)
            || e.PropertyName == nameof(FileListViewModel.AiViewMode))
        {
            UpdateActiveStates();
        }
        else if (e.PropertyName == "SidebarVisibilityChanged")
        {
            ViewModel!.LoadSidebarVisibility();
        }
        else if (e.PropertyName == nameof(FileListViewModel.IsAiSectionCollapsed))
        {
            UpdateChevronState();
        }
        else if (e.PropertyName == nameof(FileListViewModel.IsCollectionsSectionCollapsed))
        {
            UpdateChevronState();
        }
        else if (e.PropertyName == nameof(FileListViewModel.ExternalVolumes))
        {
            // Handled by binding
        }
    }

    private void UpdateChevronState()
    {
        if (ViewModel == null) return;
        UpdateChevron(AiChevron, !ViewModel.IsAiSectionCollapsed);
        UpdateChevron(CollChevron, !ViewModel.IsCollectionsSectionCollapsed);
    }

    private static void UpdateChevron(PathIcon? chevron, bool expanded)
    {
        if (chevron == null) return;
        if (expanded)
        {
            if (chevron.RenderTransform is not RotateTransform)
                chevron.RenderTransform = new RotateTransform(90);
        }
        else
        {
            chevron.RenderTransform = null;
        }
    }

    // ── Active state highlighting ──

    private void UpdateActiveStates()
    {
        if (ViewModel == null) return;
        var current = ViewModel.CurrentPath;
        var home = ViewModel.HomeDirectory;

        ToggleClass(UsernameItem, current == home);
        ToggleClass(DesktopItem, current == home + "/Desktop");
        ToggleClass(DocumentsItem, current == home + "/Documents");
        ToggleClass(DownloadsItem, current == home + "/Downloads");
        ToggleClass(PicturesItem, current == home + "/Pictures");
        ToggleClass(MusicItem, current == home + "/Music");
        ToggleClass(VolumeItem, current == "/");
        ToggleClass(ApplicationsItem, current == "/Applications");
        ToggleClass(TrashItem, ViewModel.IsTrashActive);

        ToggleClass(AiPeopleItem, ViewModel.IsAiView && ViewModel.AiViewMode == AiViewMode.People);
        ToggleClass(AiCategoriesItem, ViewModel.IsAiView && ViewModel.AiViewMode == AiViewMode.Categories);
        ToggleClass(AiLocationsItem, ViewModel.IsAiView && ViewModel.AiViewMode == AiViewMode.Locations);
        ToggleClass(AiDatesItem, ViewModel.IsAiView && ViewModel.AiViewMode == AiViewMode.Dates);
        ToggleClass(AiTextSearchItem, ViewModel.IsAiView && ViewModel.AiViewMode == AiViewMode.TextSearch);
        UpdateDynamicActiveStates();
    }

    private void UpdateDynamicActiveStates()
    {
        if (ViewModel == null) return;

        foreach (var border in this.GetVisualDescendants().OfType<Border>())
        {
            var active = border.Tag switch
            {
                string path => string.Equals(ViewModel.CurrentPath, path, StringComparison.Ordinal),
                VolumeInfo volume => string.Equals(ViewModel.CurrentPath, volume.Path, StringComparison.Ordinal),
                Collection collection => ViewModel.IsCollectionView && ViewModel.CurrentCollectionId == collection.Id,
                _ => false
            };

            if (border.Tag is string or VolumeInfo or Collection)
                ToggleClass(border, active);
        }
    }

    private static void ToggleClass(Border? border, bool active)
    {
        if (border == null) return;
        if (active)
            border.Classes.Add("active");
        else
            border.Classes.Remove("active");
    }

    // ── Sidebar item clicks (Border-based items) ──

    private async void OnSidebarItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel == null) return;
        var border = sender as Border;
        if (border == null) return;

        string? path = null;
        AiViewMode? aiMode = null;

        if (border == UsernameItem) path = ViewModel.HomeDirectory;
        else if (border == DesktopItem) path = ViewModel.HomeDirectory + "/Desktop";
        else if (border == DocumentsItem) path = ViewModel.HomeDirectory + "/Documents";
        else if (border == DownloadsItem) path = ViewModel.HomeDirectory + "/Downloads";
        else if (border == PicturesItem) path = ViewModel.HomeDirectory + "/Pictures";
        else if (border == MusicItem) path = ViewModel.HomeDirectory + "/Music";
        else if (border == VolumeItem) path = "/";
        else if (border == ApplicationsItem) path = "/Applications";
        else if (border == TrashItem) path = ViewModel.TrashPath;
        else if (border == AiPeopleItem) aiMode = AiViewMode.People;
        else if (border == AiCategoriesItem) aiMode = AiViewMode.Categories;
        else if (border == AiLocationsItem) aiMode = AiViewMode.Locations;
        else if (border == AiDatesItem) aiMode = AiViewMode.Dates;
        else if (border == AiTextSearchItem) aiMode = AiViewMode.TextSearch;

        if (path != null)
            await ViewModel.NavigateToCommand.ExecuteAsync(path);
        else if (aiMode.HasValue)
            await ViewModel.NavigateToAiViewAsync(aiMode.Value);
    }

    // ── Collapse toggles ──

    private void OnToggleAiCollapsed(object? sender, PointerPressedEventArgs e)
    {
        ViewModel?.ToggleAiCollapsedCommand.Execute(null);
    }

    private void OnToggleCollectionsCollapsed(object? sender, PointerPressedEventArgs e)
    {
        ViewModel?.ToggleCollectionsCollapsedCommand.Execute(null);
    }

    private async void OnPinnedFolderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string path } || ViewModel == null) return;
        await ViewModel.NavigateToCommand.ExecuteAsync(path);
        UpdateActiveStates();
    }

    private void OnUnpinPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    // ── ListBox selection handlers ──

    private async void OnVolumePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: VolumeInfo vol } || ViewModel == null) return;
        await ViewModel.NavigateToCommand.ExecuteAsync(vol.Path);
        UpdateActiveStates();
    }

    private async void OnCollectionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: Collection col } || ViewModel == null) return;
        await ViewModel.NavigateToCollectionAsync(col.Id);
        UpdateActiveStates();
    }

    // ── Actions ──

    private async void UnpinFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path || ViewModel == null) return;
        await ViewModel.UnpinFolderAsync(path);
    }

    private async void EjectVolume(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || ViewModel == null) return;
        if (btn.Tag is VolumeInfo vol)
            await ViewModel.EjectVolumeCommand.ExecuteAsync(vol);
    }

    // ── Collection management ──

    private void StartNewCollection(object? sender, RoutedEventArgs e)
    {
        _editingCollection = null;
        NewCollectionPanel.IsVisible = true;
        NewCollectionInput.Text = "新收藏夹";
        NewCollectionInput.Focus();
        NewCollectionInput.SelectAll();
    }

    private void OnNewCollectionLostFocus(object? sender, RoutedEventArgs e)
    {
        _ = CommitNewCollection();
    }

    private async void OnNewCollectionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await CommitNewCollection();
        else if (e.Key == Key.Escape)
        {
            NewCollectionPanel.IsVisible = false;
            NewCollectionInput.Text = "";
        }
    }

    private async System.Threading.Tasks.Task CommitNewCollection()
    {
        NewCollectionPanel.IsVisible = false;
        if (!string.IsNullOrWhiteSpace(NewCollectionInput.Text) && ViewModel != null)
        {
            if (_editingCollection == null)
                await ViewModel.CreateCollectionAsync(NewCollectionInput.Text.Trim());
            else
                await ViewModel.RenameCollectionAsync(_editingCollection.Id, NewCollectionInput.Text.Trim());
        }
        _editingCollection = null;
        NewCollectionInput.Text = "";
    }

    private void RenameCollection(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: Collection collection }) return;
        _editingCollection = collection;
        NewCollectionPanel.IsVisible = true;
        NewCollectionInput.Text = collection.Name;
        NewCollectionInput.Focus();
        NewCollectionInput.SelectAll();
    }

    private void DeleteCollection(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: Collection collection })
            ViewModel?.ShowCollectionDeleteConfirmDialog(collection.Id, collection.Name);
    }

    // ── Remote Server ──

    private async void OnAddRemoteServer(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window window) return;

        var dialog = new RemoteConnectionDialog();
        var result = await dialog.ShowDialog<RemoteServerInfo?>(window);
        if (result != null && dialog.Connected && ViewModel != null)
        {
            await ViewModel.ConnectToServerAsync(result);
            RefreshRemoteServersList();
        }
    }

    private async void OnRemoteServerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: RemoteServerInfo server } || ViewModel == null) return;
        await ViewModel.ConnectToServerAsync(server);
        RefreshRemoteServersList();
    }

    private void OnDisconnectRemoteServer(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: RemoteServerInfo server }) return;
        ViewModel?.DisconnectServer(server.Id);
        RefreshRemoteServersList();
    }

    private void RefreshRemoteServersList()
    {
        var connectionService = App.Services.GetService<IRemoteConnectionService>();
        if (connectionService == null)
        {
            RemoteServersHeader.IsVisible = false;
            RemoteServersList.IsVisible = false;
            return;
        }

        var servers = connectionService.GetSavedServers();
        foreach (var server in servers)
        {
            server.IsConnected = connectionService.IsConnected(server.Id);
        }

        var hasServers = servers.Count > 0;
        RemoteServersHeader.IsVisible = hasServers;
        RemoteServersList.IsVisible = hasServers;
        RemoteServersList.ItemsSource = servers;
    }
}
