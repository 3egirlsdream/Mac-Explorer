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
    private MainWindowViewModel? _vm;
    private readonly NavigationBridge _navigationBridge;
    private readonly IDirectoryChangeNotifier _directoryChangeNotifier;
    private readonly IDragDropBridge _dragDropBridge;
    private readonly IBackgroundTaskManager _taskManager;
    private bool _dialogSyncRunning;
    private bool _initialized;
    private int _previousRunningTaskCount;
    private IServiceScope? _scope;

    // Task overlay panel state machine
    private enum PanelMode { None, Auto, Manual }
    private PanelMode _taskPanelMode = PanelMode.None;
    private bool _isTaskPanelAnimating;
    private CancellationTokenSource? _taskPanelAnimCts;
    private CancellationTokenSource? _autoCloseTimerCts;
    private const double TaskPanelAnimDurationMs = 220;

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
        vm.SetOwnerWindow(this);
        _navigationBridge.Register(vm);
        _directoryChangeNotifier.Subscribe(vm);
        _dragDropBridge.Register(vm);
    }

    private void UnregisterViewModel(FileListViewModel vm)
    {
        vm.SetOwnerWindow(null);
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
        _taskPanelAnimCts?.Cancel();
        _autoCloseTimerCts?.Cancel();
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
            var tasks = _taskManager.Tasks.ToList();
            var runningCount = tasks.Count(t => t.State == BackgroundTaskState.Running);

            UpdateTaskButton();
            RebuildTaskPanelItems(tasks);

            // Auto-open when first task starts running
            if (runningCount > 0 && _previousRunningTaskCount == 0
                && _taskPanelMode == PanelMode.None)
            {
                _taskPanelMode = PanelMode.Auto;
                _ = SlideTaskPanelAsync(show: true);
            }

            // Auto-close when all tasks complete (Auto mode only, after delay)
            if (runningCount == 0 && _previousRunningTaskCount > 0
                && _taskPanelMode == PanelMode.Auto)
            {
                StartAutoCloseTimer();
            }

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

    // ──────────────────────────────────────────────
    //  Task Overlay Panel
    // ──────────────────────────────────────────────

    private static int GetTaskSortOrder(BackgroundTaskInfo task) =>
        task.State switch
        {
            BackgroundTaskState.Running => 0,
            BackgroundTaskState.Failed => 1,
            BackgroundTaskState.Completed => 2,
            _ => 3
        };

    private void ToggleTaskPanel(object? sender, RoutedEventArgs e) => ToggleTaskPanel();

    private void ToggleTaskPanel()
    {
        if (_isTaskPanelAnimating) return;
        CancelAutoCloseTimer();

        if (TaskOverlayPanel.IsVisible)
        {
            _taskPanelMode = PanelMode.None;
            _ = SlideTaskPanelAsync(show: false);
        }
        else
        {
            _taskPanelMode = PanelMode.Manual;
            RebuildTaskPanelItems(_taskManager.Tasks.ToList());
            _ = SlideTaskPanelAsync(show: true);
        }
    }

    private void CloseTaskPanel(object? sender, RoutedEventArgs e)
    {
        if (_isTaskPanelAnimating) return;
        CancelAutoCloseTimer();
        _taskPanelMode = PanelMode.None;
        _ = SlideTaskPanelAsync(show: false);
    }

    private async Task SlideTaskPanelAsync(bool show)
    {
        _taskPanelAnimCts?.Cancel();
        _taskPanelAnimCts?.Dispose();
        _taskPanelAnimCts = new CancellationTokenSource();
        var token = _taskPanelAnimCts.Token;
        _isTaskPanelAnimating = true;

        var transform = (TranslateTransform)TaskOverlayPanel.RenderTransform!;
        var startX = transform.X;
        var targetX = show ? 0 : 380;
        var stopwatch = Stopwatch.StartNew();

        if (show)
        {
            TaskOverlayPanel.IsVisible = true;
            TaskPanelTitle.Text = _taskManager.Tasks.Count > 0
                ? $"后台任务 ({_taskManager.Tasks.Count(t => t.State == BackgroundTaskState.Running)} 个进行中)"
                : "后台任务";
        }

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var progress = Math.Min(1, stopwatch.Elapsed.TotalMilliseconds / TaskPanelAnimDurationMs);
                var eased = 1 - Math.Pow(1 - progress, 3);
                transform.X = startX + (targetX - startX) * eased;
                if (progress >= 1) break;
                await Task.Delay(16, token);
            }
            transform.X = targetX;
        }
        catch (OperationCanceledException) { }

        if (!show)
            TaskOverlayPanel.IsVisible = false;

        _isTaskPanelAnimating = false;
    }

    private void StartAutoCloseTimer()
    {
        CancelAutoCloseTimer();
        _autoCloseTimerCts = new CancellationTokenSource();
        var token = _autoCloseTimerCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, token);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (_taskPanelMode == PanelMode.Auto)
                    {
                        _taskPanelMode = PanelMode.None;
                        await SlideTaskPanelAsync(show: false);
                    }
                });
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void CancelAutoCloseTimer()
    {
        _autoCloseTimerCts?.Cancel();
        _autoCloseTimerCts?.Dispose();
        _autoCloseTimerCts = null;
    }

    private void RebuildTaskPanelItems(IReadOnlyList<BackgroundTaskInfo> tasks)
    {
        var runningCount = tasks.Count(t => t.State == BackgroundTaskState.Running);
        var completedCount = tasks.Count(t => t.State == BackgroundTaskState.Completed);
        var failedCount = tasks.Count(t => t.State == BackgroundTaskState.Failed);

        TaskPanelTitle.Text = runningCount > 0
            ? $"后台任务 ({runningCount} 个进行中)"
            : tasks.Count > 0 ? "后台任务" : "没有任务";

        ClearPanelButton.IsEnabled = tasks.Count > 0;
        ClearCompletedButton.IsEnabled = completedCount + failedCount > 0;
        PanelFooter.IsVisible = completedCount + failedCount > 0;

        TaskItemsPanel.Children.Clear();
        foreach (var task in tasks.OrderBy(GetTaskSortOrder))
            TaskItemsPanel.Children.Add(CreateCompactTaskRow(task));
    }

    private Control CreateCompactTaskRow(BackgroundTaskInfo task)
    {
        var row = new Border();
        row.Classes.Add("task-overlay-row");

        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(14, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (task.State == BackgroundTaskState.Running)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // State dot
        var dot = new Border();
        dot.Classes.Add("task-state-dot");
        dot.Classes.Add(task.State switch
        {
            BackgroundTaskState.Running => "running",
            BackgroundTaskState.Completed => "completed",
            BackgroundTaskState.Failed => "failed",
            _ => "running"
        });
        Grid.SetColumn(dot, 0);
        Grid.SetRow(dot, 0);
        grid.Children.Add(dot);

        // Label
        var label = new TextBlock
        {
            Text = task.Label,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.Classes.Add("task-label");
        Grid.SetColumn(label, 1);
        Grid.SetRow(label, 0);
        grid.Children.Add(label);

        // Status (running: %, completed/failed: text)
        var statusText = new TextBlock
        {
            Text = task.State switch
            {
                BackgroundTaskState.Running => $"{task.Progress:F0}%",
                BackgroundTaskState.Completed => "已完成",
                BackgroundTaskState.Failed => "失败",
                _ => ""
            },
            TextAlignment = TextAlignment.Right
        };
        statusText.Classes.Add(task.State == BackgroundTaskState.Running ? "task-percent" : "task-status");
        Grid.SetColumn(statusText, 2);
        Grid.SetRow(statusText, 0);
        grid.Children.Add(statusText);

        // Close button
        var closeBtn = CreateCompactCloseButton(task);
        Grid.SetColumn(closeBtn, 3);
        Grid.SetRow(closeBtn, 0);
        grid.Children.Add(closeBtn);

        // Progress bar for running tasks
        if (task.State == BackgroundTaskState.Running)
        {
            var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = task.Progress };
            progress.Classes.Add("task-mini");
            Grid.SetColumn(progress, 1);
            Grid.SetColumnSpan(progress, 3);
            Grid.SetRow(progress, 1);
            grid.Children.Add(progress);
        }
        else if (task.State == BackgroundTaskState.Failed
                 && !string.IsNullOrWhiteSpace(task.ErrorMessage))
        {
            var errorText = new TextBlock
            {
                Text = task.ErrorMessage,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 10,
                Foreground = Brush.Parse("#E5484D")
            };
            Grid.SetColumn(errorText, 1);
            Grid.SetColumnSpan(errorText, 3);
            Grid.SetRow(errorText, 1);
            grid.Children.Add(errorText);
        }

        row.Child = grid;
        return row;
    }

    private Button CreateCompactCloseButton(BackgroundTaskInfo task)
    {
        var button = new Button
        {
            Content = new PathIcon
            {
                Data = Geometry.Parse(Assets.Icons.Close),
                Width = 9,
                Height = 9
            }
        };
        button.Classes.Add("ghost");
        button.Classes.Add("task-row-close");
        ToolTip.SetTip(button, task.State == BackgroundTaskState.Running ? "取消任务" : "移除任务");
        button.Click += (_, _) =>
        {
            if (task.State == BackgroundTaskState.Running)
                task.Cts.Cancel();
            _taskManager.RemoveTask(task.Id);
        };
        return button;
    }

    private void ClearAllPanelTasks(object? sender, RoutedEventArgs e)
    {
        foreach (var task in _taskManager.Tasks.ToList())
        {
            if (task.State == BackgroundTaskState.Running)
                task.Cts.Cancel();
            _taskManager.RemoveTask(task.Id);
        }
    }

    private void ClearCompletedPanel(object? sender, RoutedEventArgs e)
    {
        foreach (var task in _taskManager.Tasks
                     .Where(t => t.State != BackgroundTaskState.Running)
                     .ToList())
        {
            _taskManager.RemoveTask(task.Id);
        }
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
