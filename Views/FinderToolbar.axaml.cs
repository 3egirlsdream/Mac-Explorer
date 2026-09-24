using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Views.Dialogs;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public enum ToolbarMenuKind
{
    New,
    Sort,
    More
}

public partial class FinderToolbar : UserControl
{
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<FinderToolbar, bool>(nameof(IsCompact));

    private FileListViewModel? _subscribedViewModel;

    // Callback to open settings dialog via MainWindow
    public Action? OpenSettingsCallback { get; set; }

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public FinderToolbar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SubscribeToViewModel(ViewModel);
        SyncViewModeToggles();
        PointerEntered += (_, _) => UpdateActionAvailability();
        GotFocus += (_, _) => UpdateActionAvailability();
        AttachedToVisualTree += (_, _) =>
        {
            SubscribeToViewModel(ViewModel);
            _ = ViewModel?.LoadNewItemActionsAsync();
            SyncViewModeToggles();
        };
        DetachedFromVisualTree += (_, _) => SubscribeToViewModel(null);
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCompactProperty)
            PseudoClasses.Set(":compact", change.GetNewValue<bool>());
    }

    private void ToggleNewDropdown(object? sender, RoutedEventArgs e)
    {
        var shouldOpen = !NewDropdown.IsOpen;
        CloseDropdowns();
        NewDropdown.IsOpen = shouldOpen;
    }

    private async void CreateNewItem(object? sender, RoutedEventArgs e)
    {
        NewDropdown.IsOpen = false;
        if (sender is Button { DataContext: ContextMenuAction { Execute: { } execute } })
            await execute();
    }

    private void CutSelected(object? sender, RoutedEventArgs e) => ViewModel?.CutSelected();
    private void CopySelected(object? sender, RoutedEventArgs e) => ViewModel?.CopySelected();
    private async void PasteItems(object? sender, RoutedEventArgs e) { if (ViewModel != null) await ViewModel.PasteAsync(); }
    private void DeleteSelected(object? sender, RoutedEventArgs e) => ViewModel?.ShowDeleteConfirmDialog();

    private async void ToggleGridView(object? sender, RoutedEventArgs e)
    {
        await InvokeUiCapabilityAsync("ui.view-mode", new { mode = nameof(ViewMode.Grid) });
        SyncViewModeToggles();
    }

    private async void ToggleListView(object? sender, RoutedEventArgs e)
    {
        await InvokeUiCapabilityAsync("ui.view-mode", new { mode = nameof(ViewMode.List) });
        SyncViewModeToggles();
    }

    private async void ToggleTreeView(object? sender, RoutedEventArgs e)
    {
        await InvokeUiCapabilityAsync("ui.view-mode", new { mode = nameof(ViewMode.Tree) });
        SyncViewModeToggles();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SubscribeToViewModel(ViewModel);
        _ = ViewModel?.LoadNewItemActionsAsync();
        SyncViewModeToggles();
    }

    private void SubscribeToViewModel(FileListViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel)) return;

        if (_subscribedViewModel != null)
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _subscribedViewModel = viewModel;

        if (_subscribedViewModel != null)
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(FileListViewModel.ViewMode))
            Dispatcher.UIThread.Post(SyncViewModeToggles);
        if (e.PropertyName is nameof(FileListViewModel.StatusSummaryText) or nameof(FileListViewModel.CutPaths)
            or nameof(FileListViewModel.IsHomePage) or nameof(FileListViewModel.CurrentPath))
            Dispatcher.UIThread.Post(UpdateActionAvailability);
    }

    private void SyncViewModeToggles()
    {
        var viewMode = ViewModel?.ViewMode;
        GridViewToggle.IsChecked = viewMode == ViewMode.Grid;
        ListViewToggle.IsChecked = viewMode == ViewMode.List;
        TreeViewToggle.IsChecked = viewMode == ViewMode.Tree;
        UpdateActionAvailability();
    }

    private void UpdateActionAvailability()
    {
        var hasSelection = ViewModel?.SelectedEntries.Count > 0;
        CutButton.IsEnabled = CutOverflowButton.IsEnabled = hasSelection;
        CopyButton.IsEnabled = CopyOverflowButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = DeleteOverflowButton.IsEnabled = hasSelection;
        PasteButton.IsEnabled = PasteOverflowButton.IsEnabled = ViewModel != null
            && (App.Services?.GetService<IClipboardService>()?.HasPasteableContent ?? false);
    }

    private async void ToggleSortDirection(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await InvokeUiCapabilityAsync("ui.sort", new
            {
                field = ViewModel.SortField.ToString(), ascending = !ViewModel.SortAscending
            });
            SortDirectionButton.Content = ViewModel.SortAscending ? "升序 ↑" : "降序 ↓";
        }
        SortDropdown.IsOpen = false;
    }

    private void ToggleSortDropdown(object? sender, RoutedEventArgs e)
    {
        var shouldOpen = !SortDropdown.IsOpen;
        CloseDropdowns();
        SortDropdown.IsOpen = shouldOpen;
        if (ViewModel != null)
            SortDirectionButton.Content = ViewModel.SortAscending ? "升序 ↑" : "降序 ↓";
    }

    private async void SelectSortField(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<SortField>(value, out var field))
            await InvokeUiCapabilityAsync("ui.sort", new { field = field.ToString() });
        SortDropdown.IsOpen = false;
    }

    private async void SelectGroupField(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<GroupField>(value, out var field) && ViewModel != null)
            await InvokeUiCapabilityAsync("ui.group", new { field = field.ToString() });
        SortDropdown.IsOpen = false;
    }

    private async void GoHome(object? sender, RoutedEventArgs e)
        => await InvokeUiCapabilityAsync("ui.home");

    private async void TogglePreviewPane(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            await InvokeUiCapabilityAsync("ui.preview-pane", new { visible = !ViewModel.IsPreviewPaneVisible });
    }

    private async void ToggleMetadataPanel(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            await InvokeUiCapabilityAsync("ui.metadata-panel", new { visible = !ViewModel.IsMetadataPanelVisible });
    }

    private async void ToggleInfoPanel(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            await InvokeUiCapabilityAsync("ui.info-panel", new { visible = !ViewModel.IsInfoPanelVisible });
    }

    private async Task InvokeUiCapabilityAsync(string id, object? arguments = null)
    {
        var viewModel = ViewModel;
        if (viewModel == null) return;
        try
        {
            var json = arguments == null ? "{}" : System.Text.Json.JsonSerializer.Serialize(arguments);
            var result = await App.Services.GetRequiredService<MacExplorer.Copilot.IAppCapabilityRegistry>()
                .ExecuteUiAsync(id, json, viewModel);
            if (!result.Success) viewModel.StatusText = result.Message;
        }
        catch (Exception ex) { viewModel.StatusText = ex.Message; }
    }

    private void ToggleMoreDropdown(object? sender, RoutedEventArgs e)
    {
        var shouldOpen = !MoreDropdown.IsOpen;
        CloseDropdowns();
        MoreDropdown.IsOpen = shouldOpen;
    }

    public void CloseDropdowns()
    {
        NewDropdown.IsOpen = false;
        SortDropdown.IsOpen = false;
        MoreDropdown.IsOpen = false;
    }

    public void ToggleMenu(ToolbarMenuKind kind)
    {
        var popup = kind switch
        {
            ToolbarMenuKind.New => NewDropdown,
            ToolbarMenuKind.Sort => SortDropdown,
            ToolbarMenuKind.More => MoreDropdown,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var shouldOpen = !popup.IsOpen;
        CloseDropdowns();
        popup.IsOpen = shouldOpen;
    }

    public bool TryCloseDropdown()
    {
        if (MoreDropdown.IsOpen)
        {
            MoreDropdown.IsOpen = false;
            MoreBtn.Focus();
            return true;
        }

        if (SortDropdown.IsOpen)
        {
            SortDropdown.IsOpen = false;
            SortButton.Focus();
            return true;
        }

        if (NewDropdown.IsOpen)
        {
            NewDropdown.IsOpen = false;
            NewBtn.Focus();
            return true;
        }

        return false;
    }

    public void CloseDropdownsFromPointerSource(object? source)
    {
        var visual = source as Visual;
        if (IsInsideVisual(visual, NewBtn)
            || IsInsideVisual(visual, SortButton)
            || IsInsideVisual(visual, MoreBtn)
            || IsInsideVisual(visual, NewDropdown.Child as Visual)
            || IsInsideVisual(visual, SortDropdown.Child as Visual)
            || IsInsideVisual(visual, MoreDropdown.Child as Visual))
            return;

        CloseDropdowns();
    }

    private static bool IsInsideVisual(Visual? visual, Visual? target)
    {
        if (target == null) return false;
        for (; visual != null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, target))
                return true;
        return false;
    }

    private void OnDropdownStateChanged(object? sender, EventArgs e)
    {
        NewBtn.Classes.Set("dropdown-open", NewDropdown.IsOpen);
        SortButton.Classes.Set("dropdown-open", SortDropdown.IsOpen);
        MoreBtn.Classes.Set("dropdown-open", MoreDropdown.IsOpen);
    }

    private async void OnConnectRemoteServer(object? sender, RoutedEventArgs e)
    {
        MoreDropdown.IsOpen = false;

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
        }
    }

    private async void OnBatchRename(object? sender, RoutedEventArgs e)
    {
        MoreDropdown.IsOpen = false;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window window || ViewModel == null) return;

        var dialog = new BatchRenameDialog();
        using var modalBlock = window is MainWindow mainWindow
            ? mainWindow.BlockModalParentInteraction()
            : null;
        await dialog.ShowDialogAsync(window, ViewModel);
    }
}
