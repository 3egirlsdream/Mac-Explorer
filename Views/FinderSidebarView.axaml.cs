using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
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
    private FileTag? _editingTag;
    private Border? _activeTagEditorRow;
    private TextBox? _activeTagInput;
    private bool _isCommittingTagEdit;
    private Border? _pinnedDropTarget;
    private FileListViewModel? _subscribedViewModel;
    private readonly Dictionary<Control, RailSecondaryState> _railSecondaryStates = [];
    private bool _isRailMode;

    internal bool IsRailMode => _isRailMode;

    public FinderSidebarView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnSidebarKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public void SetRailMode(bool railMode)
    {
        if (_isRailMode == railMode)
            return;
        _isRailMode = railMode;
        foreach (var control in this.GetLogicalDescendants().OfType<Control>())
        {
            control.Classes.Set("rail-text-hidden", railMode && control is TextBlock);
            var compactItem = control as Border;
            var isCompactItem = compactItem?.Classes.Contains("sidebar-item") == true;
            control.Classes.Set("rail-compact-item", railMode && isCompactItem);
            control.Classes.Set("rail-action-hidden",
                railMode && control is Button && control.Classes.Contains("sidebar-item-action"));
            if (isCompactItem)
                SetCompactItemRailMode(compactItem!, railMode);
            if (!control.Classes.Contains("rail-secondary"))
                continue;

            if (railMode)
            {
                _railSecondaryStates.TryAdd(control, new RailSecondaryState(
                    control.MinHeight,
                    control.MaxHeight,
                    control.Opacity,
                    control.Margin,
                    control.IsHitTestVisible));
                control.MinHeight = 0;
                control.MaxHeight = 0;
                control.Opacity = 0;
                control.Margin = default;
                control.IsHitTestVisible = false;
            }
            else if (_railSecondaryStates.Remove(control, out var state))
            {
                control.MinHeight = state.MinHeight;
                control.MaxHeight = state.MaxHeight;
                control.Opacity = state.Opacity;
                control.Margin = state.Margin;
                control.IsHitTestVisible = state.IsHitTestVisible;
            }
        }
    }

    private static void SetCompactItemRailMode(Border item, bool railMode)
    {
        var contentPanel = item.Child as StackPanel;
        if (railMode)
        {
            item.Width = 40;
            item.MinWidth = 40;
            item.MaxWidth = 40;
            item.Margin = new Thickness(0, 1);
            item.HorizontalAlignment = HorizontalAlignment.Center;
            if (contentPanel != null)
                contentPanel.HorizontalAlignment = HorizontalAlignment.Center;
            return;
        }

        item.ClearValue(WidthProperty);
        item.ClearValue(MinWidthProperty);
        item.ClearValue(MaxWidthProperty);
        item.ClearValue(MarginProperty);
        item.ClearValue(HorizontalAlignmentProperty);
        contentPanel?.ClearValue(HorizontalAlignmentProperty);
    }

    private readonly record struct RailSecondaryState(
        double MinHeight,
        double MaxHeight,
        double Opacity,
        Thickness Margin,
        bool IsHitTestVisible);

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PinnedFolders.CollectionChanged -= OnSidebarTagChanged;
            _subscribedViewModel.SidebarTags.CollectionChanged -= OnSidebarTagChanged;
            _subscribedViewModel.ExternalVolumes.CollectionChanged -= OnSidebarTagChanged;
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.NewTagRequested -= OnNewTagRequested;
        }

        base.OnDataContextChanged(e);
        if (ViewModel != null)
        {
            _subscribedViewModel = ViewModel;
            _subscribedViewModel.PinnedFolders.CollectionChanged += OnSidebarTagChanged;
            _subscribedViewModel.SidebarTags.CollectionChanged += OnSidebarTagChanged;
            _subscribedViewModel.ExternalVolumes.CollectionChanged += OnSidebarTagChanged;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _subscribedViewModel.NewTagRequested += OnNewTagRequested;
            UpdateActiveStates();
            UpdateChevronState();
            RefreshRemoteServersList();
        }
        else
        {
            _subscribedViewModel = null;
        }
    }

    private void OnSidebarTagChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(UpdateActiveStates);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.CurrentPath)
            || e.PropertyName == nameof(FileListViewModel.IsAiView)
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
        var hasTags = ViewModel.SidebarTags.Count > 0;
        TagsSectionTitle.Text = "标签";
        TagsSectionHeader.Margin = new Thickness(0, hasTags ? 16 : 8, 0, 2);
        AutomationProperties.SetName(TagsSectionHeader, hasTags ? "展开或收起标签" : "添加标签");
        TagsChevron.IsVisible = hasTags;
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
                FileTag tag => string.Equals(ViewModel.CurrentPath, tag.VirtualPath, StringComparison.Ordinal),
                _ => false
            };

            if (border.Tag is string or VolumeInfo or FileTag)
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
        if (sender is Border border)
            await NavigateSidebarItemAsync(border);
    }

    private async void OnSidebarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space))
            return;

        // Child editors and buttons own their own Enter/Space behavior.
        if (e.Source is TextBox or Button)
            return;
        if (e.Source is Grid header && ViewModel != null)
        {
            if (header == AiSectionHeader) ViewModel.ToggleAiCollapsedCommand.Execute(null);
            else if (header == TagsSectionHeader)
            {
                if (ViewModel.SidebarTags.Count == 0) StartNewTag(sender, e);
                else ViewModel.ToggleTagsCollapsedCommand.Execute(null);
            }
            else return;
            e.Handled = true;
            return;
        }
        var border = e.Source as Border;
        if (border?.Classes.Contains("sidebar-item") != true)
            border = (e.Source as Visual)?.GetVisualAncestors().OfType<Border>()
                .FirstOrDefault(candidate => candidate.Classes.Contains("sidebar-item"));
        if (border == null || !await NavigateSidebarItemAsync(border))
            return;

        e.Handled = true;
    }

    private async Task<bool> NavigateSidebarItemAsync(Border border)
    {
        if (ViewModel == null)
            return false;

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
        else if (border.Tag is string taggedPath) path = taggedPath;
        else if (border.Tag is VolumeInfo volume) path = volume.Path;

        if (path != null)
            await ViewModel.NavigateToCommand.ExecuteAsync(path);
        else if (aiMode.HasValue)
            await ViewModel.NavigateToAiViewAsync(aiMode.Value);
        else if (border.Tag is FileTag tag && !ReferenceEquals(border, _activeTagEditorRow))
            await ViewModel.NavigateToTagAsync(tag);
        else if (border.Tag is RemoteServerInfo server)
        {
            await ViewModel.ConnectToServerAsync(server);
            RefreshRemoteServersList();
        }
        else
            return false;

        UpdateActiveStates();
        return true;
    }

    // ── Collapse toggles ──

    private void OnToggleAiCollapsed(object? sender, PointerPressedEventArgs e)
    {
        ViewModel?.ToggleAiCollapsedCommand.Execute(null);
    }

    private void OnToggleTagsCollapsed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.SidebarTags.Count == 0) StartNewTag(sender, e);
        else ViewModel?.ToggleTagsCollapsedCommand.Execute(null);
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

    private async void OnTagPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (ReferenceEquals(sender, _activeTagEditorRow))
        {
            e.Handled = true;
            return;
        }
        if (sender is not Border { Tag: FileTag col } || ViewModel == null) return;
        await ViewModel.NavigateToTagAsync(col);
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

    // ── Tag management ──

    private void StartNewTag(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        EndTagEditUi();
        _editingTag = null;
        if (ViewModel?.IsTagsSectionCollapsed == true)
            ViewModel.IsTagsSectionCollapsed = false;

        NewTagEditorRow.IsVisible = true;
        _activeTagEditorRow = NewTagEditorRow;
        _activeTagInput = NewTagInput;
        NewTagInput.Text = "新标签";
        FocusTagInput(NewTagInput);
    }

    private void OnTagEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox input && ReferenceEquals(input, _activeTagInput))
            _ = CommitTagEditAsync();
    }

    private async void OnTagEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitTagEditAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTagEdit();
        }
    }

    private async void CommitTagEdit(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await CommitTagEditAsync();
    }

    private async System.Threading.Tasks.Task CommitTagEditAsync()
    {
        if (_isCommittingTagEdit || _activeTagInput == null) return;

        _isCommittingTagEdit = true;
        var name = _activeTagInput.Text?.Trim();
        var tag = _editingTag;
        EndTagEditUi();

        try
        {
            if (!string.IsNullOrWhiteSpace(name) && ViewModel != null)
            {
                if (tag == null)
                    await ViewModel.CreateTagAsync(name);
                else
                    await ViewModel.RenameTagAsync(tag, name);
            }
        }
        finally
        {
            _isCommittingTagEdit = false;
        }
    }

    private void RenameTag(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: FileTag { IsCustom: true } tag } button) return;

        var row = button.GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("tag-row"));
        if (row == null) return;

        CancelTagEdit();
        _editingTag = tag;
        _activeTagEditorRow = row;
        _activeTagInput = FindTagRowControl<TextBox>(row, "tag-name-editor");
        if (_activeTagInput == null)
        {
            CancelTagEdit();
            return;
        }

        SetTagRowEditing(row, true);
        _activeTagInput.Text = tag.Name;
        FocusTagInput(_activeTagInput);
    }

    private void CancelTagEdit()
    {
        ViewModel?.CancelNewTag();
        EndTagEditUi();
    }

    private void EndTagEditUi()
    {
        var editingTag = _editingTag;
        var editorRow = _activeTagEditorRow;
        // Hiding the focused editor raises LostFocus synchronously; clear ownership first so Escape cannot commit.
        _editingTag = null;
        _activeTagEditorRow = null;
        _activeTagInput = null;
        if (editingTag != null && editorRow != null)
            SetTagRowEditing(editorRow, false);
        NewTagEditorRow.IsVisible = false;
        NewTagInput.Text = "";
    }

    private static void SetTagRowEditing(Border row, bool editing)
    {
        var nameLabel = FindTagRowControl<TextBlock>(row, "tag-name-label");
        var nameEditor = FindTagRowControl<TextBox>(row, "tag-name-editor");
        var confirmButton = FindTagRowControl<Button>(row, "tag-confirm-action");

        if (nameLabel != null) nameLabel.IsVisible = !editing;
        if (nameEditor != null) nameEditor.IsVisible = editing;
        if (confirmButton != null) confirmButton.IsVisible = editing;

        foreach (var action in row.GetVisualDescendants()
                     .OfType<Button>()
                     .Where(button => button.Classes.Contains("tag-normal-action")))
        {
            action.IsVisible = !editing && row.Tag is FileTag { IsCustom: true };
        }
    }

    private static T? FindTagRowControl<T>(Border row, string className)
        where T : Control
    {
        return row.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(control => control.Classes.Contains(className));
    }

    private static void FocusTagInput(TextBox input)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!input.IsVisible) return;
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void DeleteTag(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: FileTag tag })
            ViewModel?.ShowTagDeleteConfirmDialog(tag);
    }

    private void OnNewTagRequested() => StartNewTag(this, new RoutedEventArgs());

    private void OnTagDragOver(object? sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e.DataTransfer);
        e.DragEffects = paths.Length > 0 && paths.All(Services.Impl.FileTagService.IsSupportedPath)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTagDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: FileTag tag } || ViewModel == null) return;
        e.Handled = true;
        await ViewModel.SetFileTagAsync(GetDroppedPaths(e.DataTransfer), tag, true);
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
        var connectionService = App.Services?.GetService<IRemoteConnectionService>();
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
