using System;
using System.Collections.Specialized;
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
    private Border? _activeCollectionEditorRow;
    private TextBox? _activeCollectionInput;
    private bool _isCommittingCollectionEdit;
    private Border? _pinnedDropTarget;
    private FileListViewModel? _subscribedViewModel;

    public FinderSidebarView()
    {
        InitializeComponent();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PinnedFolders.CollectionChanged -= OnSidebarCollectionChanged;
            _subscribedViewModel.Collections.CollectionChanged -= OnSidebarCollectionChanged;
            _subscribedViewModel.SidebarTags.CollectionChanged -= OnSidebarCollectionChanged;
            _subscribedViewModel.ExternalVolumes.CollectionChanged -= OnSidebarCollectionChanged;
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnDataContextChanged(e);
        if (ViewModel != null)
        {
            _subscribedViewModel = ViewModel;
            _subscribedViewModel.PinnedFolders.CollectionChanged += OnSidebarCollectionChanged;
            _subscribedViewModel.Collections.CollectionChanged += OnSidebarCollectionChanged;
            _subscribedViewModel.SidebarTags.CollectionChanged += OnSidebarCollectionChanged;
            _subscribedViewModel.ExternalVolumes.CollectionChanged += OnSidebarCollectionChanged;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateActiveStates();
            UpdateChevronState();
            RefreshRemoteServersList();
        }
        else
        {
            _subscribedViewModel = null;
        }
    }

    private void OnSidebarCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(UpdateActiveStates);

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
        else if (e.PropertyName == nameof(FileListViewModel.IsTagsSectionCollapsed))
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
        UpdateChevron(TagsChevron, !ViewModel.IsTagsSectionCollapsed);
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
                FileTag tag => string.Equals(ViewModel.CurrentPath, tag.VirtualPath, StringComparison.Ordinal),
                _ => false
            };

            if (border.Tag is string or VolumeInfo or Collection or FileTag)
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

    private void OnToggleTagsCollapsed(object? sender, PointerPressedEventArgs e)
    {
        ViewModel?.ToggleTagsCollapsedCommand.Execute(null);
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

    private void OnPinnedFoldersDragOver(object? sender, DragEventArgs e)
    {
        ClearPinnedDropTarget();
        var paths = GetDroppedPaths(e.DataTransfer);
        if (paths.Length == 0 || paths.Any(path => !Directory.Exists(path)))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        _pinnedDropTarget = FindPinnedFolderBorder(e.Source as Visual);
        _pinnedDropTarget?.Classes.Add("pin-drop-target");
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnPinnedFoldersDragLeave(object? sender, RoutedEventArgs e) => ClearPinnedDropTarget();

    private async void OnPinnedFoldersDrop(object? sender, DragEventArgs e)
    {
        var targetPath = _pinnedDropTarget?.Tag as string;
        ClearPinnedDropTarget();
        if (ViewModel == null) return;

        var paths = GetDroppedPaths(e.DataTransfer)
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var path in paths)
        {
            if (!await ViewModel.IsFolderPinnedAsync(path))
                await ViewModel.PinFolderAsync(path, new DirectoryInfo(path).Name);
            if (!string.IsNullOrWhiteSpace(targetPath))
                await ViewModel.ReorderPinnedFolderAsync(path, targetPath);
        }
        e.Handled = true;
    }

    private static string[] GetDroppedPaths(IDataTransfer data) => data.TryGetFiles()?
        .Select(item => item.Path.LocalPath)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .ToArray() ?? [];

    private Border? FindPinnedFolderBorder(Visual? visual)
    {
        for (; visual != null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, PinnedFoldersControl)) break;
            if (visual is Border { Tag: string } border && IsDescendantOf(border, PinnedFoldersControl))
                return border;
        }
        return null;
    }

    private static bool IsDescendantOf(Visual visual, Visual ancestor)
    {
        for (Visual? current = visual; current != null; current = current.GetVisualParent())
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void ClearPinnedDropTarget()
    {
        _pinnedDropTarget?.Classes.Remove("pin-drop-target");
        _pinnedDropTarget = null;
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
        if (ReferenceEquals(sender, _activeCollectionEditorRow))
        {
            e.Handled = true;
            return;
        }
        if (sender is not Border { Tag: Collection col } || ViewModel == null) return;
        await ViewModel.NavigateToCollectionAsync(col.Id);
        UpdateActiveStates();
    }

    private async void OnTagPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: FileTag tag } || ViewModel == null) return;
        await ViewModel.NavigateToTagAsync(tag);
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
        e.Handled = true;
        CancelCollectionEdit();
        _editingCollection = null;
        if (ViewModel?.IsCollectionsSectionCollapsed == true)
            ViewModel.IsCollectionsSectionCollapsed = false;

        NewCollectionEditorRow.IsVisible = true;
        _activeCollectionEditorRow = NewCollectionEditorRow;
        _activeCollectionInput = NewCollectionInput;
        NewCollectionInput.Text = "新收藏夹";
        FocusCollectionInput(NewCollectionInput);
    }

    private void OnCollectionEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox input && ReferenceEquals(input, _activeCollectionInput))
            _ = CommitCollectionEditAsync();
    }

    private async void OnCollectionEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitCollectionEditAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelCollectionEdit();
        }
    }

    private async void CommitCollectionEdit(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await CommitCollectionEditAsync();
    }

    private async System.Threading.Tasks.Task CommitCollectionEditAsync()
    {
        if (_isCommittingCollectionEdit || _activeCollectionInput == null) return;

        _isCommittingCollectionEdit = true;
        var name = _activeCollectionInput.Text?.Trim();
        var collection = _editingCollection;
        EndCollectionEditUi();

        try
        {
            if (!string.IsNullOrWhiteSpace(name) && ViewModel != null)
            {
                if (collection == null)
                    await ViewModel.CreateCollectionAsync(name);
                else
                    await ViewModel.RenameCollectionAsync(collection.Id, name);
            }
        }
        finally
        {
            _isCommittingCollectionEdit = false;
        }
    }

    private void RenameCollection(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: Collection collection } button) return;

        var row = button.GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("collection-row"));
        if (row == null) return;

        CancelCollectionEdit();
        _editingCollection = collection;
        _activeCollectionEditorRow = row;
        _activeCollectionInput = FindCollectionRowControl<TextBox>(row, "collection-name-editor");
        if (_activeCollectionInput == null)
        {
            CancelCollectionEdit();
            return;
        }

        SetCollectionRowEditing(row, true);
        _activeCollectionInput.Text = collection.Name;
        FocusCollectionInput(_activeCollectionInput);
    }

    private void CancelCollectionEdit()
    {
        EndCollectionEditUi();
    }

    private void EndCollectionEditUi()
    {
        if (_editingCollection != null && _activeCollectionEditorRow != null)
            SetCollectionRowEditing(_activeCollectionEditorRow, false);

        NewCollectionEditorRow.IsVisible = false;
        NewCollectionInput.Text = "";
        _editingCollection = null;
        _activeCollectionEditorRow = null;
        _activeCollectionInput = null;
    }

    private static void SetCollectionRowEditing(Border row, bool editing)
    {
        var nameLabel = FindCollectionRowControl<TextBlock>(row, "collection-name-label");
        var nameEditor = FindCollectionRowControl<TextBox>(row, "collection-name-editor");
        var confirmButton = FindCollectionRowControl<Button>(row, "collection-confirm-action");

        if (nameLabel != null) nameLabel.IsVisible = !editing;
        if (nameEditor != null) nameEditor.IsVisible = editing;
        if (confirmButton != null) confirmButton.IsVisible = editing;

        foreach (var action in row.GetVisualDescendants()
                     .OfType<Button>()
                     .Where(button => button.Classes.Contains("collection-normal-action")))
        {
            action.IsVisible = !editing;
        }
    }

    private static T? FindCollectionRowControl<T>(Border row, string className)
        where T : Control
    {
        return row.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control => control.Classes.Contains(className));
    }

    private static void FocusCollectionInput(TextBox input)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!input.IsVisible) return;
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.Input);
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
        using var modalBlock = window is MainWindow mainWindow
            ? mainWindow.BlockModalParentInteraction()
            : null;
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
