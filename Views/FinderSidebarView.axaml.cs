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
    private Border? _folderDropTarget;
    private string? _folderDropPath;
    private IDisposable? _folderHoverTimer;
    internal Func<Action, TimeSpan, IDisposable> ScheduleFolderHover { get; set; }
        = (action, delay) => DispatcherTimer.RunOnce(action, delay);
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
        ClearFolderDropTarget();
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PinnedFolders.CollectionChanged -= OnSidebarTagChanged;
            _subscribedViewModel.ExternalVolumes.CollectionChanged -= OnSidebarTagChanged;
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnDataContextChanged(e);
        if (ViewModel != null)
        {
            _subscribedViewModel = ViewModel;
            _subscribedViewModel.PinnedFolders.CollectionChanged += OnSidebarTagChanged;
            _subscribedViewModel.ExternalVolumes.CollectionChanged += OnSidebarTagChanged;
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
        else if (e.PropertyName == nameof(FileListViewModel.ExternalVolumes))
        {
            // Handled by binding
        }
    }

    private void UpdateChevronState()
    {
        if (ViewModel == null) return;
        UpdateChevron(AiChevron, !ViewModel.IsAiSectionCollapsed);
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
        if (RemoteServersList.ItemsSource is IEnumerable<RemoteServerInfo> servers)
            foreach (var server in servers) server.RefreshLocalizedText();
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
        ToggleClass(NetworkItem, ViewModel.IsNetworkPage);

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
        if (border == SidebarSettingsItem)
        {
            if (TopLevel.GetTopLevel(this) is MainWindow window)
                window.OpenSettings();
            return true;
        }

        if (ViewModel == null)
            return false;

        var path = GetSidebarFolderPath(border);
        AiViewMode? aiMode = null;

        if (border == TrashItem) path = ViewModel.TrashPath;
        else if (border == AiPeopleItem) aiMode = AiViewMode.People;
        else if (border == AiCategoriesItem) aiMode = AiViewMode.Categories;
        else if (border == AiLocationsItem) aiMode = AiViewMode.Locations;
        else if (border == AiDatesItem) aiMode = AiViewMode.Dates;
        else if (border == AiTextSearchItem) aiMode = AiViewMode.TextSearch;

        if (path != null)
            await ViewModel.NavigateToCommand.ExecuteAsync(path);
        else if (aiMode.HasValue)
            await ViewModel.NavigateToAiViewAsync(aiMode.Value);
        else if (border.Tag is FileTag tag)
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

    private string? GetSidebarFolderPath(Border? border)
    {
        if (border == null || ViewModel == null) return null;
        if (border == UsernameItem) return ViewModel.HomeDirectory;
        if (border == DesktopItem) return ViewModel.HomeDirectory + "/Desktop";
        if (border == DocumentsItem) return ViewModel.HomeDirectory + "/Documents";
        if (border == DownloadsItem) return ViewModel.HomeDirectory + "/Downloads";
        if (border == PicturesItem) return ViewModel.HomeDirectory + "/Pictures";
        if (border == MusicItem) return ViewModel.HomeDirectory + "/Music";
        if (border == VolumeItem) return "/";
        if (border == ApplicationsItem) return "/Applications";
        return border.Tag switch
        {
            string path => path,
            VolumeInfo volume => volume.Path,
            _ => null
        };
    }

    private Border? FindSidebarDropRow(DragEventArgs e)
    {
        // Resolve the release position again, including when Source is the sidebar itself.
        for (var visual = this.InputHitTest(e.GetPosition(this)) as Visual;
             visual != null && visual != this; visual = visual.GetVisualParent())
            if (visual is Border border && border.Classes.Contains("sidebar-item"))
                return border;
        return null;
    }

    private void OnSidebarDragOver(object? sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e.DataTransfer);
        var row = FindSidebarDropRow(e);
        var targetPath = GetSidebarFolderPath(row);
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (targetPath != null)
        {
            e.DragEffects = FileDropPolicy.GetEffect(paths, targetPath);
            if (e.DragEffects != DragDropEffects.None)
            {
                if (_folderDropTarget != row || _folderDropPath != targetPath)
                {
                    ClearFolderDropTarget();
                    _folderDropTarget = row;
                    _folderDropPath = targetPath;
                    row!.Classes.Add("folder-drop-target");
                    _folderHoverTimer = ScheduleFolderHover(
                        () => OpenHoveredFolderAsync(row, targetPath), TimeSpan.FromMilliseconds(600));
                }
                return;
            }
        }
        ClearFolderDropTarget();
        if (row == null && IsPinnedFolderDropArea(e) && paths.Length > 0 && paths.All(Directory.Exists))
            e.DragEffects = DragDropEffects.Copy;
    }

    private async void OpenHoveredFolderAsync(Border row, string path)
    {
        _folderHoverTimer = null;
        var viewModel = ViewModel;
        if (_folderDropTarget != row || _folderDropPath != path || viewModel == null
            || !row.IsEffectivelyVisible || TopLevel.GetTopLevel(row) == null || viewModel.CurrentPath == path)
            return;
        try
        {
            await viewModel.NavigateToAsync(path);
        }
        catch (Exception ex)
        {
            viewModel.StatusText = $"打开文件夹失败: {ex.Message}";
        }
    }

    private void OnSidebarDragLeave(object? sender, DragEventArgs e) => ClearFolderDropTarget();

    private async void OnSidebarDrop(object? sender, DragEventArgs e)
    {
        var row = FindSidebarDropRow(e);
        var targetPath = GetSidebarFolderPath(row);
        ClearFolderDropTarget();
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        var viewModel = ViewModel;
        if (viewModel == null) return;
        var paths = GetDroppedPaths(e.DataTransfer).Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            if (targetPath != null)
            {
                var effect = FileDropPolicy.GetEffect(paths, targetPath);
                if (effect == DragDropEffects.None || !Directory.Exists(targetPath)) return;
                if (effect == DragDropEffects.Copy)
                {
                    var copied = await App.Services.GetRequiredService<IDragDropService>()
                        .DropFilesAsync(paths, targetPath, forceCopy: true, forceMove: false);
                    e.DragEffects = copied ? effect : DragDropEffects.None;
                    return;
                }
                paths = paths.Where(path => !FileDropPolicy.IsSameDestination(path, targetPath)).ToArray();
                var fileService = App.Services.GetRequiredService<IFileService>();
                var target = await fileService.GetEntryAsync(targetPath);
                if (target is not { IsDirectory: true, IsWritable: true }) return;
                var entries = (await Task.WhenAll(paths.Select(fileService.GetEntryAsync)))
                    .OfType<FileSystemEntry>().ToArray();
                if (entries.Length == 0) return;
                e.DragEffects = effect;
                await viewModel.MoveEntriesAsync(entries, target);
                return;
            }

            if (row != null || !IsPinnedFolderDropArea(e) || paths.Length == 0 || !paths.All(Directory.Exists)) return;
            e.DragEffects = DragDropEffects.Copy;
            foreach (var path in paths)
                if (!await viewModel.IsFolderPinnedAsync(path))
                    await viewModel.PinFolderAsync(path, new DirectoryInfo(path).Name);
        }
        catch (Exception ex)
        {
            e.DragEffects = DragDropEffects.None;
            viewModel.StatusText = $"拖放失败: {ex.Message}";
        }
    }

    private bool IsPinnedFolderDropArea(DragEventArgs e)
        => this.InputHitTest(e.GetPosition(this)) is Visual hit && IsDescendantOf(hit, PinnedFoldersControl);

    private static string[] GetDroppedPaths(IDataTransfer data) => FileListView.GetDroppedPaths(data);

    private static bool IsDescendantOf(Visual visual, Visual ancestor)
    {
        for (Visual? current = visual; current != null; current = current.GetVisualParent())
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void ClearFolderDropTarget()
    {
        _folderHoverTimer?.Dispose();
        _folderHoverTimer = null;
        _folderDropTarget?.Classes.Remove("folder-drop-target");
        _folderDropTarget = null;
        _folderDropPath = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (MacExplorer.Services.Impl.LocalizationService.Current is { } language)
            language.LanguageChanged += UpdateActiveStates;
        var coordinator = App.Services?.GetService<IServerConnectionCoordinator>();
        if (coordinator != null) coordinator.Changed += OnConnectionsChanged;
        RefreshRemoteServersList();
        UpdateActiveStates();
    }

    private void OnConnectionsChanged() => Dispatcher.UIThread.Post(RefreshRemoteServersList);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearFolderDropTarget();
        var coordinator = App.Services?.GetService<IServerConnectionCoordinator>();
        if (coordinator != null) coordinator.Changed -= OnConnectionsChanged;
        if (MacExplorer.Services.Impl.LocalizationService.Current is { } language)
            language.LanguageChanged -= UpdateActiveStates;
        base.OnDetachedFromVisualTree(e);
    }

    // ── ListBox selection handlers ──

    private async void OnVolumePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: VolumeInfo vol } || ViewModel == null) return;
        await ViewModel.NavigateToCommand.ExecuteAsync(vol.Path);
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
            if (dialog.NavigationPath != null) await ViewModel.NavigateToAsync(dialog.NavigationPath);
            else await ViewModel.ConnectToServerAsync(result);
            RefreshRemoteServersList();
        }
    }

    private async void OnRemoteServerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: RemoteServerInfo server } || ViewModel == null) return;
        await ViewModel.ConnectToServerAsync(server);
        RefreshRemoteServersList();
    }

    private async void OnDisconnectRemoteServer(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: RemoteServerInfo server }) return;
        if (ViewModel != null) await ViewModel.DisconnectServerAsync(server);
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
            server.IsConnected = App.Services?.GetService<IServerConnectionCoordinator>()?.IsConnected(server) ?? connectionService.IsConnected(server.Id);
        }

        var coordinator = App.Services?.GetService<IServerConnectionCoordinator>();
        servers = servers.Where(server => server.ServiceType != RemoteServiceType.Smb
            || coordinator?.GetMountedVolume(server) is not { } mounted
            || ViewModel?.ExternalVolumes.Any(v => v.Path == mounted.Path) != true).ToArray();
        var hasServers = servers.Count > 0;
        RemoteServersHeader.IsVisible = hasServers;
        RemoteServersList.IsVisible = hasServers;
        RemoteServersList.ItemsSource = servers;
    }
}
