using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.ViewModels;
using MacExplorer.Views.Dialogs;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class MainWindow : AppWindow
{
    private bool _isRestoringSearch;
    private SettingsDialog? _settingsDialog;
    private TaskPanel? _taskPanel;
    private MainWindowViewModel? _vm;
    private readonly NavigationBridge _navigationBridge;
    private readonly IDirectoryChangeNotifier _directoryChangeNotifier;
    private readonly IDragDropBridge _dragDropBridge;
    private readonly IBackgroundTaskManager _taskManager;
    private bool _dialogSyncRunning;
    private bool _initialized;
    private int _previousRunningTaskCount;
    private IServiceScope? _scope;
    private bool _resizingPreview;
    private double _pendingPreviewWidth;
    private bool _previewResizeFramePending;
    private double _normalPreviewWidth = 380;
    private bool _isPreviewExpanded;
    private CancellationTokenSource? _previewAnimationCts;

    public string? InitialNavigationPath { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        _navigationBridge = App.Services.GetRequiredService<NavigationBridge>();
        _directoryChangeNotifier = App.Services.GetRequiredService<IDirectoryChangeNotifier>();
        _dragDropBridge = App.Services.GetRequiredService<IDragDropBridge>();
        _taskManager = App.Services.GetRequiredService<IBackgroundTaskManager>();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Activated += OnActivated;
        Closed += OnClosed;
        _taskManager.TasksChanged += OnTasksChanged;
        ApplyAppearanceSettings();
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Wire up settings button in sidebar footer
        SettingsButton.Click += (_, _) => OpenSettings();
        ToolbarControl.OpenSettingsCallback = OpenSettings;
        InfoPanelControl.PreviewExpandedChanged += OnPreviewExpandedChanged;
        SizeChanged += OnWindowSizeChanged;
        PositionChanged += (_, _) => ToolbarControl.CloseDropdowns();
        Deactivated += (_, _) => ToolbarControl.CloseDropdowns();

        // Ctrl+Shift+G: open Liquid Glass demo
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.G && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                               && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                new LiquidGlassDemoWindow().Show();
            }
        };
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed && _vm?.FileList.CanGoBack == true)
        {
            e.Handled = true;
            _ = _vm.FileList.NavigateBackAsync();
            return;
        }

        if (properties.IsXButton2Pressed && _vm?.FileList.CanGoForward == true)
        {
            e.Handled = true;
            _ = _vm.FileList.NavigateForwardAsync();
            return;
        }

        FileListControl.DismissContextMenu();
        ToolbarControl.CloseDropdownsFromPointerSource(e.Source);
        ClearTextInputFocusFromPointerSource(e.Source);
    }

    private void ClearTextInputFocusFromPointerSource(object? source)
    {
        if (IsInsideTextInput(source as Visual))
            return;

        if (FocusManager?.GetFocusedElement() is TextBox textBox)
        {
            textBox.ClearSelection();
            Focus(NavigationMethod.Pointer, KeyModifiers.None);
        }
    }

    private static bool IsInsideTextInput(Visual? visual)
    {
        for (; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is TextBox or ComboBox or NumericUpDown)
                return true;
        }

        return false;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.L)
        {
            e.Handled = true;
            BreadcrumbControl.FocusPathInput();
            return;
        }

        if (FileListControl.IsVisible)
            FileListControl.TryHandleFileShortcut(e);
    }

    public void AttachScope(IServiceScope scope) => _scope = scope;

    public async Task NavigateToPathAsync(string path)
    {
        if (_vm?.FileList != null && Directory.Exists(path))
            await _vm.FileList.NavigateToAsync(path);
    }

    public void ApplyAppearanceSettings()
    {
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        ApplyNativeWindowChrome();
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.FileList.PropertyChanged -= OnFileListPropertyChanged;
            UnregisterViewModel(_vm.FileList);
        }

        if (DataContext is MainWindowViewModel vm)
        {
            _vm = vm;
            vm.FileList.PropertyChanged += OnFileListPropertyChanged;
            RegisterViewModel(vm.FileList);
            UpdateContentVisibility(vm);
            UpdateInfoPanelVisibility(vm);
            UpdateTaskButton();
        }
        else
        {
            _vm = null;
        }
    }

    private void OnFileListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(_vm.FileList.IsHomePage) ||
            e.PropertyName == nameof(_vm.FileList.IsAiView) ||
            e.PropertyName == nameof(_vm.FileList.AiViewMode))
        {
            UpdateContentVisibility(_vm);
            UpdateInfoPanelVisibility(_vm);
        }
        else if (e.PropertyName is nameof(FileListViewModel.IsPreviewPaneVisible)
                 or nameof(FileListViewModel.IsMetadataPanelVisible)
                 or nameof(FileListViewModel.IsInfoPanelVisible))
        {
            UpdateInfoPanelVisibility(_vm);
        }

        if (e.PropertyName is nameof(FileListViewModel.IsPasteConfirmDialogVisible)
            or nameof(FileListViewModel.IsMoveConfirmDialogVisible)
            or nameof(FileListViewModel.IsDeleteConfirmDialogVisible)
            or nameof(FileListViewModel.IsCollectionDeleteConfirmDialogVisible)
            or nameof(FileListViewModel.IsCompressDialogVisible))
        {
            Dispatcher.UIThread.Post(() => _ = SyncDialogsAsync());
        }
    }

    private void RegisterViewModel(FileListViewModel vm)
    {
        _navigationBridge.Register(vm);
        _directoryChangeNotifier.Subscribe(vm);
        _dragDropBridge.Register(vm);
    }

    private void UnregisterViewModel(FileListViewModel vm)
    {
        _navigationBridge.Unregister(vm);
        _directoryChangeNotifier.Unsubscribe(vm);
        _dragDropBridge.Unregister(vm);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized || _vm == null) return;
        _initialized = true;

        var pendingPath = InitialNavigationPath ?? _navigationBridge.PendingNavigationPath;
        _navigationBridge.PendingNavigationPath = null;
        var restorePath = _navigationBridge.PendingQuickAccessFocus
            ? null
            : _vm.FileList.GetRestorableDirectoryPath();
        _navigationBridge.PendingQuickAccessFocus = false;

        var path = !string.IsNullOrEmpty(pendingPath) ? pendingPath : restorePath;
        if (!string.IsNullOrEmpty(path))
            await _vm.FileList.NavigateToAsync(path);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        _navigationBridge.SetActive(_vm.FileList);
        _dragDropBridge.SetActive(_vm.FileList);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _previewAnimationCts?.Cancel();
        _taskManager.TasksChanged -= OnTasksChanged;
        if (_vm != null)
        {
            _vm.FileList.PropertyChanged -= OnFileListPropertyChanged;
            UnregisterViewModel(_vm.FileList);
        }
        _scope?.Dispose();
        _scope = null;
    }

    private async Task SyncDialogsAsync()
    {
        if (_dialogSyncRunning || _vm == null) return;
        _dialogSyncRunning = true;
        try
        {
            var vm = _vm.FileList;
            if (vm.IsDeleteConfirmDialogVisible)
            {
                var label = vm.DeleteConfirmItemCount == 1
                    ? $"“{vm.DeleteConfirmFirstItemName}”"
                    : $"选中的 {vm.DeleteConfirmItemCount} 个项目";
                var confirmed = await ShowConfirmationAsync("确认删除", $"确定要将{label}移到废纸篓吗？", "删除");
                if (confirmed) await vm.ConfirmDeleteSelectedAsync();
                else vm.CancelDeleteConfirmDialog();
            }
            else if (vm.IsCollectionDeleteConfirmDialogVisible)
            {
                var confirmed = await ShowConfirmationAsync("删除收藏", $"确定要删除收藏“{vm.PendingDeleteCollectionName}”吗？收藏中的原始文件不会被删除。", "删除");
                if (confirmed) await vm.ConfirmDeleteCollectionAsync();
                else vm.CancelCollectionDeleteConfirmDialog();
            }
            else if (vm.IsPasteConfirmDialogVisible)
            {
                var confirmed = await ShowConfirmationAsync("替换已有项目", BuildConflictMessage(vm.PasteConflictNames), "替换");
                if (confirmed) await vm.ConfirmPasteAsync();
                else vm.CancelPasteConfirmDialog();
            }
            else if (vm.IsMoveConfirmDialogVisible)
            {
                var confirmed = await ShowConfirmationAsync("替换已有项目", BuildConflictMessage(vm.MoveConflictNames), "替换");
                if (confirmed) await vm.ConfirmMoveAsync();
                else vm.CancelMoveConfirmDialog();
            }
            else if (vm.IsCompressDialogVisible && vm.PendingCompressOptions != null)
            {
                var dialog = new CompressDialog();
                var result = await dialog.ShowDialogAsync(this, vm.PendingCompressOptions);
                if (result != null) vm.ConfirmCompress(result);
                else vm.CancelCompressDialog();
            }
        }
        finally
        {
            _dialogSyncRunning = false;
        }
    }

    private async Task<bool> ShowConfirmationAsync(string title, string message, string confirmText)
        => await DialogHost.ShowConfirmationAsync(title, message, confirmText);

    private static string BuildConflictMessage(IReadOnlyList<string> names)
    {
        var preview = string.Join("、", names.Take(3).Select(name => $"“{name}”"));
        if (names.Count > 3) preview += $" 等 {names.Count} 个项目";
        return $"目标位置已经包含 {preview}。是否替换？";
    }

    private void OnTasksChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var runningCount = _taskManager.Tasks.Count(t => t.State == BackgroundTaskState.Running);
            UpdateTaskButton();
            if (runningCount > 0 && _previousRunningTaskCount == 0)
                OpenTaskPanel();
            _previousRunningTaskCount = runningCount;
        });
    }

    private void UpdateTaskButton()
    {
        var tasks = _taskManager.Tasks;
        TaskButton.IsVisible = tasks.Count > 0;
        var running = tasks.Count(t => t.State == BackgroundTaskState.Running);
        TaskButtonText.Text = running > 0 ? $"后台任务 ({running})" : $"后台任务 ({tasks.Count})";
    }

    private void OpenTaskPanel(object? sender, RoutedEventArgs e) => OpenTaskPanel();

    private void OpenTaskPanel()
    {
        if (_taskPanel == null)
        {
            _taskPanel = new TaskPanel();
            _taskPanel.SetTaskManager(_taskManager);
            _taskPanel.Closed += (_, _) => _taskPanel = null;
        }

        if (!_taskPanel.IsVisible) _taskPanel.Show(this);
        else _taskPanel.Activate();
    }

    private void UpdateContentVisibility(MainWindowViewModel vm)
    {
        var showTextSearch = vm.FileList.IsAiView && vm.FileList.AiViewMode == AiViewMode.TextSearch;
        FileListControl.IsVisible = !vm.FileList.IsHomePage && !showTextSearch;
        HomeViewControl.IsVisible = vm.FileList.IsHomePage;
        AiViewControl.IsVisible = showTextSearch;
    }

    private void UpdateInfoPanelVisibility(MainWindowViewModel vm)
    {
        var canShowPanel = !vm.FileList.IsHomePage && !vm.FileList.IsAiView;
        InfoDrawer.IsPaneOpen = canShowPanel && vm.FileList.IsInfoPanelVisible;
    }

    private void OnInfoPanelResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isPreviewExpanded) return;
        if (sender is not Control handle || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        _resizingPreview = true;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private async void OnPreviewExpandedChanged(object? sender, bool expanded)
    {
        if (expanded)
            _normalPreviewWidth = InfoDrawer.OpenPaneLength;
        _isPreviewExpanded = expanded;
        InfoPanelResizeHandle.IsVisible = !expanded;
        if (expanded)
            InfoPanelPane.ColumnDefinitions[0].Width = new GridLength(0);
        InfoPanelControl.SetExpandedChrome(expanded);
        await AnimatePreviewWidthAsync(expanded, expanded
            ? Math.Max(380, InfoDrawer.Bounds.Width)
            : Math.Clamp(_normalPreviewWidth, 280, Math.Max(280, Bounds.Width * 0.5)));
        if (!expanded && _isPreviewExpanded == expanded)
            InfoPanelPane.ColumnDefinitions[0].Width = new GridLength(6);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isPreviewExpanded && InfoDrawer.Bounds.Width > 0)
            InfoDrawer.OpenPaneLength = InfoDrawer.Bounds.Width;
    }

    private async Task AnimatePreviewWidthAsync(bool expanded, double targetWidth)
    {
        _previewAnimationCts?.Cancel();
        _previewAnimationCts?.Dispose();
        _previewAnimationCts = new CancellationTokenSource();
        var token = _previewAnimationCts.Token;
        var startWidth = InfoDrawer.OpenPaneLength;
        InfoDrawer.DisplayMode = SplitViewDisplayMode.Inline;
        var stopwatch = Stopwatch.StartNew();
        const double durationMilliseconds = 180;

        try
        {
            while (true)
            {
                var progress = Math.Min(1, stopwatch.Elapsed.TotalMilliseconds / durationMilliseconds);
                var eased = 1 - Math.Pow(1 - progress, 3);
                InfoDrawer.OpenPaneLength = startWidth + (targetWidth - startWidth) * eased;
                if (progress >= 1) break;
                await Task.Delay(16, token);
            }

            InfoDrawer.OpenPaneLength = targetWidth;
            InfoPanelControl.CompletePreviewTransition(expanded);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnInfoPanelResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingPreview || sender is not Control handle || e.Pointer.Captured != handle) return;
        _pendingPreviewWidth = Math.Clamp(
            InfoDrawer.Bounds.Width - e.GetPosition(InfoDrawer).X,
            280,
            Math.Max(280, Bounds.Width * 0.5));
        if (!_previewResizeFramePending)
        {
            _previewResizeFramePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _previewResizeFramePending = false;
                if (_resizingPreview)
                    InfoDrawer.OpenPaneLength = _pendingPreviewWidth;
            }, DispatcherPriority.Render);
        }
        e.Handled = true;
    }

    private void OnInfoPanelResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizingPreview) return;
        _resizingPreview = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private async void NavigateBack(object? sender, RoutedEventArgs e)
    {
        if (_vm?.FileList.CanGoBack == true)
            await _vm.FileList.NavigateBackAsync();
    }

    private async void NavigateForward(object? sender, RoutedEventArgs e)
    {
        if (_vm?.FileList.CanGoForward == true)
            await _vm.FileList.NavigateForwardAsync();
    }

    private async void NavigateUp(object? sender, RoutedEventArgs e)
    {
        if (_vm?.FileList == null) return;
        var current = _vm.FileList.CurrentPath;
        if (!string.IsNullOrEmpty(current) && current != "/")
        {
            var parent = System.IO.Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(parent))
                await _vm.FileList.NavigateToAsync(parent);
            else
                await _vm.FileList.NavigateToAsync("/");
        }
    }

    private async void RefreshView(object? sender, RoutedEventArgs e)
    {
        if (_vm?.FileList == null) return;
        await _vm.FileList.RefreshCommand.ExecuteAsync(null);
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm?.FileList == null) return;
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            await _vm.FileList.SearchCommand.ExecuteAsync(SearchBox.Text);
            SearchClearBtn.IsVisible = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            await RestoreSearchOriginAsync();
            e.Handled = true;
        }
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        SearchClearBtn.IsVisible = hasQuery;
        if (!hasQuery && _vm?.FileList.IsSearchMode == true)
            await RestoreSearchOriginAsync();
    }

    private async void ClearSearch(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchClearBtn.IsVisible = false;
        await RestoreSearchOriginAsync();
    }

    private async Task RestoreSearchOriginAsync()
    {
        if (_isRestoringSearch || _vm?.FileList.IsSearchMode != true)
            return;

        _isRestoringSearch = true;
        try
        {
            await _vm.FileList.ExitSearchAsync();
        }
        finally
        {
            _isRestoringSearch = false;
        }
    }

    public void OpenSettings()
    {
        if (_settingsDialog == null)
        {
            _settingsDialog = new SettingsDialog();
            _settingsDialog.DataContext = _vm?.FileList;
            _settingsDialog.Closed += (_, _) => _settingsDialog = null;
        }

        if (!_settingsDialog.IsVisible)
        {
            _settingsDialog.Show(this);
        }
        else
        {
            _settingsDialog.Activate();
        }
    }
}
