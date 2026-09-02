using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Converters;
using MacExplorer.Platforms.MacOS;
using MacExplorer.ViewModels;
using MacExplorer.Views.Dialogs;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class MainWindow : AppWindow
{
    // The native visual effect is the continuous window-level material. Primary content
    // surfaces stay transparent; the window frame receives its own translucent tint below.
    private static readonly string[] VibrancyTransparentResourceKeys =
    [
        "WindowBackgroundBrush",
        "SurfaceBackgroundBrush",
        "ColorBgPrimary",
        "ColorBgSidebar",
        "ColorBgToolbar",
        "ColorBgContent"
    ];

    // Only cards receive the themed translucent tint; the window frame remains clear
    // so the native material can show through.
    private static readonly (string SurfaceKey, string TintKey)[] VibrancyTintResourceKeys =
    [
        ("SurfaceBrush", "GlassSurfaceTint"),
        ("SurfaceElevatedBrush", "GlassElevatedTint")
    ];

    private bool _isRestoringSearch;
    private SettingsDialog? _settingsDialog;
    private MainWindowViewModel? _vm;
    private FileListViewModel? _activeFileList;
    private readonly Dictionary<ExplorerTabViewModel, IServiceScope?> _tabScopes = [];
    private readonly Dictionary<ExplorerTabViewModel, ExplorerPaneView> _paneViews = [];
    private readonly NavigationBridge _navigationBridge;
    private readonly IDirectoryChangeNotifier _directoryChangeNotifier;
    private readonly IDragDropBridge _dragDropBridge;
    private readonly IBackgroundTaskManager _taskManager;
    private readonly IGlobalSearchScopeService _globalSearchScopeService;
    private bool _changingGlobalSearchScope;
    private bool _dialogSyncRunning;
    private bool _initialized;
    private int _modalBlockDepth;
    private int _previousRunningTaskCount;
    private IServiceScope? _scope;

    // Global quick-search state. The backing providers query the app-wide file index
    // and never start a new recursive scan while the user is typing.
    private readonly ObservableCollection<OmniboxSuggestion> _globalSearchSuggestions = [];
    private readonly ObservableCollection<FileSystemEntry> _globalSearchFolderEntries = [];
    private static readonly FileEntryToIconConverter GlobalSearchFileIconConverter = new();
    private CancellationTokenSource? _globalSearchCts;
    private CancellationTokenSource? _globalSearchPreviewCts;
    private global::Avalonia.Media.Imaging.Bitmap? _globalSearchPreviewBitmap;
    private readonly Dictionary<string, global::Avalonia.Media.Imaging.Bitmap> _globalSearchFolderThumbnailBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastUnmodifiedDKeyDownUtc;

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
    private bool _isCompactLayout;
    private bool _isSidebarCollapsed;

    private ExplorerPaneView? ActivePaneView
        => _vm?.SelectedTab != null && _paneViews.TryGetValue(_vm.SelectedTab, out var pane)
            ? pane
            : null;

    public string? InitialNavigationPath { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        _navigationBridge = App.Services.GetRequiredService<NavigationBridge>();
        _directoryChangeNotifier = App.Services.GetRequiredService<IDirectoryChangeNotifier>();
        _dragDropBridge = App.Services.GetRequiredService<IDragDropBridge>();
        _taskManager = App.Services.GetRequiredService<IBackgroundTaskManager>();
        _globalSearchScopeService = App.Services.GetRequiredService<IGlobalSearchScopeService>();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Activated += OnActivated;
        Closed += OnClosed;
        Application.Current?.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        _taskManager.TasksChanged += OnTasksChanged;
        ApplyAppearanceSettings();
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Wire up settings button in sidebar footer
        SettingsButton.Click += (_, _) => OpenSettings();
        ToolbarControl.OpenSettingsCallback = OpenSettings;
        InfoPanelControl.PreviewExpandedChanged += OnPreviewExpandedChanged;
        SuperPreviewControl.RequestClose += OnSuperPreviewClosed;
        SizeChanged += OnWindowSizeChanged;
        PositionChanged += (_, _) => ToolbarControl.CloseDropdowns();
        Deactivated += (_, _) => ToolbarControl.CloseDropdowns();
        UpdateResponsiveLayout(Width);
        GlobalSearchResults.ItemsSource = _globalSearchSuggestions;
        GlobalSearchFolderContents.ItemsSource = _globalSearchFolderEntries;

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
        if (IsModalInteractionBlocked)
        {
            if (!IsInsideVisual(e.Source as Visual, DialogHost))
                e.Handled = true;
            return;
        }

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

        ActivePaneView?.FileListView.DismissContextMenu();
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

    private static bool IsInsideVisual(Visual? visual, Visual? target)
    {
        if (target == null) return false;
        for (; visual != null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, target))
                return true;
        return false;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsModalInteractionBlocked)
        {
            if (!IsInsideVisual(e.Source as Visual, DialogHost))
                e.Handled = true;
            return;
        }

        if (GlobalSearchOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseGlobalSearch();
            }
            else if (e.Key is Key.Down or Key.Up)
            {
                e.Handled = true;
                MoveGlobalSearchSelection(e.Key == Key.Down ? 1 : -1);
            }
            else if (e.Key == Key.Enter)
            {
                var suggestion = GlobalSearchResults.SelectedItem as OmniboxSuggestion
                                 ?? _globalSearchSuggestions.FirstOrDefault();
                if (suggestion != null)
                {
                    e.Handled = true;
                    _ = OpenGlobalSearchSuggestionAsync(suggestion);
                }
            }
            return;
        }

        // Space opens the in-window super preview for the active pane. The
        // preview owns the rest of the keyboard interaction until it closes,
        // so normal file commands cannot leak through the overlay.
        if (SuperPreviewControl.IsVisible)
            return;

        if (e.Key == Key.Space
            && !IsInsideTextInput(e.Source as Visual)
            && ActivePaneView?.FileListView.IsVisible == true
            && _vm?.FileList.SelectedEntries.Count == 1)
        {
            e.Handled = true;
            _ = OpenSuperPreviewAsync(_vm.FileList.SelectedEntries[0]);
            return;
        }

        // The global quick search has conventional shortcuts plus the double-D
        // gesture from the reference interaction. Do not intercept normal typing.
        if ((e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.K)
            || (e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.F))
        {
            e.Handled = true;
            OpenGlobalSearch();
            return;
        }

        if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.None
            && !IsInsideTextInput(e.Source as Visual))
        {
            var now = DateTime.UtcNow;
            if (now - _lastUnmodifiedDKeyDownUtc <= TimeSpan.FromMilliseconds(450))
            {
                _lastUnmodifiedDKeyDownUtc = DateTime.MinValue;
                e.Handled = true;
                OpenGlobalSearch();
                return;
            }

            _lastUnmodifiedDKeyDownUtc = now;
        }

        // Finder/browser tab shortcuts. Handle these before the file list so
        // ⌘W closes the current tab instead of the whole window when possible.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.T)
        {
            e.Handled = true;
            _ = AddTabAsync();
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.W && _vm?.SelectedTab != null)
        {
            e.Handled = true;
            if (_vm.Tabs.Count == 1)
                Close();
            else
                CloseTabCore(_vm.SelectedTab);
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Tab && _vm != null)
        {
            e.Handled = true;
            _vm.SelectRelativeTab(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            return;
        }

        // ⌘Z: undo last file operation
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.Z
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = UndoLastOperationAsync();
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.L)
        {
            e.Handled = true;
            BreadcrumbControl.FocusPathInput();
            return;
        }

        if (ActivePaneView?.FileListView.IsVisible == true)
            ActivePaneView.FileListView.TryHandleFileShortcut(e);
    }

    public IDisposable BlockModalParentInteraction()
    {
        _modalBlockDepth++;
        UpdateModalInteractionBlock();
        return new ModalInteractionScope(this);
    }

    private void ReleaseModalParentInteraction()
    {
        if (_modalBlockDepth > 0)
            _modalBlockDepth--;
        UpdateModalInteractionBlock();
    }

    private void UpdateModalInteractionBlock()
    {
        var blocked = _modalBlockDepth > 0;
        IsModalInteractionBlocked = blocked;
        ModalInteractionOverlay.IsVisible = blocked;
        ModalInteractionOverlay.IsHitTestVisible = blocked;

        if (blocked)
        {
            ToolbarControl.CloseDropdowns();
            ActivePaneView?.FileListView.DismissContextMenu();
        }
    }

    private sealed class ModalInteractionScope : IDisposable
    {
        private MainWindow? _window;

        public ModalInteractionScope(MainWindow window)
        {
            _window = window;
        }

        public void Dispose()
        {
            var window = _window;
            if (window == null) return;

            _window = null;
            window.ReleaseModalParentInteraction();
        }
    }

    public void AttachScope(IServiceScope scope)
    {
        _scope = scope;
        if (_vm?.SelectedTab != null)
            _tabScopes.TryAdd(_vm.SelectedTab, null);
    }

    public async Task NavigateToPathAsync(string path)
    {
        if (_vm?.FileList != null && Directory.Exists(path))
            await _vm.FileList.NavigateToAsync(path);
    }

    public void ApplyAppearanceSettings()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        var enabled = settings.Get("vibrancy_enabled", true);
        var opacity = Math.Clamp(settings.Get("vibrancy_alpha", 0.30), 0, 1);

        ApplyVibrancySurfaceResources(enabled);
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        TransparencyBackgroundFallback = Brushes.Transparent;
        Background = Brushes.Transparent;
        MacWindowChrome.SetVibrancy(this, enabled, opacity);
    }

    private void ApplyVibrancySurfaceResources(bool enabled)
    {
        foreach (var resourceKey in VibrancyTransparentResourceKeys)
        {
            if (!enabled)
            {
                Resources.Remove(resourceKey);
                continue;
            }

            Resources[resourceKey] = Brushes.Transparent;
        }

        foreach (var (surfaceKey, tintKey) in VibrancyTintResourceKeys)
        {
            if (!enabled)
            {
                Resources.Remove(surfaceKey);
                continue;
            }

            Resources[surfaceKey] = GetThemeBrush(tintKey);
        }
    }

    private static SolidColorBrush GetThemeBrush(string resourceKey)
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("Application resources are unavailable while applying vibrancy.");
        var theme = application.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;

        if (application.TryGetResource(resourceKey, theme, out var value) && value is ISolidColorBrush brush)
            return new SolidColorBrush(brush.Color);

        throw new InvalidOperationException($"Missing vibrancy tint resource '{resourceKey}'.");
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
            _vm.VisiblePanes.CollectionChanged -= OnVisiblePanesChanged;
            DeactivateFileList();
        }

        if (DataContext is MainWindowViewModel vm)
        {
            _vm = vm;
            vm.PropertyChanged += OnMainWindowViewModelPropertyChanged;
            vm.VisiblePanes.CollectionChanged += OnVisiblePanesChanged;
            ActivateFileList(vm.FileList);
            RebuildPaneLayout();
        }
        else
        {
            _vm = null;
        }
    }

    private void OnMainWindowViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null)
            return;

        if (e.PropertyName == nameof(MainWindowViewModel.FileList))
            ActivateFileList(_vm.FileList);
        else if (e.PropertyName is nameof(MainWindowViewModel.PaneLayout)
                 or nameof(MainWindowViewModel.PaneCount)
                 or nameof(MainWindowViewModel.IsMultiPane))
            RebuildPaneLayout();
    }

    private void OnVisiblePanesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildPaneLayout();

    private void ActivateFileList(FileListViewModel fileList)
    {
        if (ReferenceEquals(_activeFileList, fileList))
            return;

        DeactivateFileList();
        _activeFileList = fileList;
        fileList.PropertyChanged += OnFileListPropertyChanged;
        RegisterViewModel(fileList);
        UpdateContentVisibility(_vm!);
        UpdateInfoPanelVisibility(_vm!);
        UpdateTaskButton();
        WireCommandPaletteEvents(fileList);
        _navigationBridge.SetActive(fileList);
        _dragDropBridge.SetActive(fileList);
    }

    private void DeactivateFileList()
    {
        if (_activeFileList == null)
            return;

        _activeFileList.PropertyChanged -= OnFileListPropertyChanged;
        UnregisterViewModel(_activeFileList);
        _activeFileList = null;
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
        vm.RequestBatchRename -= OnRequestBatchRename;
        vm.RequestRemoteConnection -= OnRequestRemoteConnection;
        vm.RequestShowTaskPanel -= OnRequestShowTaskPanel;
    }

    private void WireCommandPaletteEvents(FileListViewModel vm)
    {
        vm.RequestBatchRename += OnRequestBatchRename;
        vm.RequestRemoteConnection += OnRequestRemoteConnection;
        vm.RequestShowTaskPanel += OnRequestShowTaskPanel;
    }

    private void OnRequestBatchRename()
    {
        _ = OpenBatchRenameDialogAsync();
    }

    private void OnRequestRemoteConnection()
    {
        _ = OpenRemoteConnectionDialogAsync();
    }

    private void OnRequestShowTaskPanel()
    {
        if (!TaskOverlayPanel.IsVisible)
            ToggleTaskPanel();
    }

    private async Task OpenBatchRenameDialogAsync()
    {
        if (_vm?.FileList == null) return;
        var dialog = new Views.Dialogs.BatchRenameDialog();
        using var modalBlock = BlockModalParentInteraction();
        await dialog.ShowDialogAsync(this, _vm.FileList);
    }

    private async Task OpenRemoteConnectionDialogAsync()
    {
        if (_vm?.FileList == null) return;

        var dialog = new RemoteConnectionDialog();
        using var modalBlock = BlockModalParentInteraction();
        var result = await dialog.ShowDialog<RemoteServerInfo?>(this);
        if (result != null && dialog.Connected)
            await _vm.FileList.ConnectToServerAsync(result);
    }

    private async Task UndoLastOperationAsync()
    {
        var historyService = App.Services.GetService<IFileOperationHistoryService>();
        if (historyService == null) return;
        var undone = await historyService.UndoLastAsync();
        if (undone && _vm?.FileList != null)
        {
            _vm.FileList.StatusText = "已撤销";
            await _vm.FileList.RefreshAsync();
        }
        else if (!undone && _vm?.FileList != null)
        {
            _vm.FileList.StatusText = "没有可撤销的操作";
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        ApplyAppearanceSettings();
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
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        _previewAnimationCts?.Cancel();
        _taskPanelAnimCts?.Cancel();
        _autoCloseTimerCts?.Cancel();
        _globalSearchCts?.Cancel();
        _globalSearchCts?.Dispose();
        _globalSearchCts = null;
        _globalSearchPreviewCts?.Cancel();
        _globalSearchPreviewCts?.Dispose();
        _globalSearchPreviewCts = null;
        _globalSearchPreviewBitmap?.Dispose();
        _globalSearchPreviewBitmap = null;
        SuperPreviewControl.RequestClose -= OnSuperPreviewClosed;
        SuperPreviewControl.Close();
        _taskManager.TasksChanged -= OnTasksChanged;
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
            _vm.VisiblePanes.CollectionChanged -= OnVisiblePanesChanged;
        }
        DeactivateFileList();
        ClearPaneViews();
        foreach (var tab in _vm?.Tabs ?? [])
            tab.Dispose();
        foreach (var scope in _tabScopes.Values)
            scope?.Dispose();
        _tabScopes.Clear();
        _scope?.Dispose();
        _scope = null;
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => ApplyAppearanceSettings();

    private async Task OpenSuperPreviewAsync(FileSystemEntry entry)
    {
        if (SuperPreviewControl.IsVisible)
            return;

        SuperPreviewControl.PasswordPrompt = _vm == null
            ? null
            : () => _vm.FileList.RequestArchivePasswordAsync();
        await SuperPreviewControl.OpenAsync(entry);
    }

    private void OnSuperPreviewClosed(object? sender, EventArgs e)
    {
        if (SuperPreviewControl.IsVisible)
            return;
        SuperPreviewControl.PasswordPrompt = null;
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
                using var modalBlock = BlockModalParentInteraction();
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
    {
        using var modalBlock = BlockModalParentInteraction();
        return await DialogHost.ShowConfirmationAsync(title, message, confirmText);
    }

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

    private static bool IsUndoableTaskKind(BackgroundTaskKind kind)
        => kind is BackgroundTaskKind.Copy
            or BackgroundTaskKind.Move
            or BackgroundTaskKind.Delete
            or BackgroundTaskKind.BatchRename;

    private Control CreateCompactTaskRow(BackgroundTaskInfo task)
    {
        var row = new Border();
        row.Classes.Add("task-overlay-row");

        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(14, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (task.State == BackgroundTaskState.Running
            || (task.State == BackgroundTaskState.Failed && !string.IsNullOrWhiteSpace(task.ErrorMessage)))
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // State dot
        var dot = new Border();
        dot.Classes.Add("task-state-dot");
        dot.Classes.Add(task.State switch
        {
            BackgroundTaskState.Running => "running",
            BackgroundTaskState.Completed => "completed",
            BackgroundTaskState.Failed => "failed",
            BackgroundTaskState.Cancelled => "failed",
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

        // Status (running: %, completed/failed/cancelled: text)
        var statusText = new TextBlock
        {
            Text = task.State switch
            {
                BackgroundTaskState.Running => $"{task.Progress:F0}%",
                BackgroundTaskState.Completed => "已完成",
                BackgroundTaskState.Failed => "失败",
                BackgroundTaskState.Cancelled => "已取消",
                _ => ""
            },
            TextAlignment = TextAlignment.Right
        };
        statusText.Classes.Add(task.State == BackgroundTaskState.Running ? "task-percent" : "task-status");
        Grid.SetColumn(statusText, 2);
        Grid.SetRow(statusText, 0);
        grid.Children.Add(statusText);

        // Retry button for failed tasks with retry action
        if (task.State == BackgroundTaskState.Failed && task.CanRetry)
        {
            var retryBtn = new Button
            {
                Content = new PathIcon
                {
                    Data = Geometry.Parse(Assets.Icons.Refresh),
                    Width = 10,
                    Height = 10
                }
            };
            retryBtn.Classes.Add("ghost");
            retryBtn.Classes.Add("task-row-close");
            ToolTip.SetTip(retryBtn, "重试");
            retryBtn.Click += (_, _) => _taskManager.RetryTask(task.Id);
            Grid.SetColumn(retryBtn, 3);
            Grid.SetRow(retryBtn, 0);
            grid.Children.Add(retryBtn);
        }
        else if (task.State == BackgroundTaskState.Completed
                 && IsUndoableTaskKind(task.Kind))
        {
            var historyService = App.Services.GetService<IFileOperationHistoryService>();
            if (historyService != null && historyService.CanUndo)
            {
                var undoBtn = new Button
                {
                    Content = AppTypography.BindFontSize(new TextBlock { Text = "撤销" }, AppTypography.Meta)
                };
                undoBtn.Classes.Add("ghost");
                undoBtn.Classes.Add("task-row-close");
                ToolTip.SetTip(undoBtn, "撤销此操作");
                undoBtn.Click += async (_, _) =>
                {
                    await historyService.UndoLastAsync();
                    if (_vm?.FileList != null)
                        _vm.FileList.StatusText = "已撤销";
                };
                Grid.SetColumn(undoBtn, 3);
                Grid.SetRow(undoBtn, 0);
                grid.Children.Add(undoBtn);
            }
            else
            {
                var spacer = new Border { Width = 0 };
                Grid.SetColumn(spacer, 3);
                Grid.SetRow(spacer, 0);
                grid.Children.Add(spacer);
            }
        }
        else
        {
            // Spacer for consistent layout
            var spacer = new Border { Width = 0 };
            Grid.SetColumn(spacer, 3);
            Grid.SetRow(spacer, 0);
            grid.Children.Add(spacer);
        }

        // Close/Cancel button
        var closeBtn = CreateCompactActionButton(task);
        Grid.SetColumn(closeBtn, 4);
        Grid.SetRow(closeBtn, 0);
        grid.Children.Add(closeBtn);

        // Progress bar for running tasks
        if (task.State == BackgroundTaskState.Running)
        {
            var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = task.Progress };
            progress.Classes.Add("task-mini");
            Grid.SetColumn(progress, 1);
            Grid.SetColumnSpan(progress, 4);
            Grid.SetRow(progress, 1);
            grid.Children.Add(progress);
        }
        else if (task.State == BackgroundTaskState.Failed
                 && !string.IsNullOrWhiteSpace(task.ErrorMessage))
        {
            var errorText = AppTypography.BindFontSize(new TextBlock
            {
                Text = task.ErrorMessage,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush.Parse("#E5484D")
            }, AppTypography.Meta);
            Grid.SetColumn(errorText, 1);
            Grid.SetColumnSpan(errorText, 4);
            Grid.SetRow(errorText, 1);
            grid.Children.Add(errorText);
        }

        row.Child = grid;
        return row;
    }

    private Button CreateCompactActionButton(BackgroundTaskInfo task)
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

        if (task.State == BackgroundTaskState.Running && task.CanCancel)
        {
            ToolTip.SetTip(button, "取消任务");
            button.Click += (_, _) =>
            {
                task.Cts.Cancel();
                _taskManager.CancelTask(task.Id);
            };
        }
        else
        {
            ToolTip.SetTip(button, "移除任务");
            button.Click += (_, _) =>
            {
                if (task.State == BackgroundTaskState.Running)
                    task.Cts.Cancel();
                _taskManager.RemoveTask(task.Id);
            };
        }

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
        => ActivePaneView?.RefreshState();

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
            ? (_isCompactLayout ? GetInfoPanelMaxWidth() : Math.Max(380, InfoDrawer.Bounds.Width))
            : Math.Clamp(_normalPreviewWidth, 280, GetInfoPanelMaxWidth()));
        if (!expanded && _isPreviewExpanded == expanded)
            InfoPanelPane.ColumnDefinitions[0].Width = new GridLength(6);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
        if (_isPreviewExpanded && InfoDrawer.Bounds.Width > 0)
            InfoDrawer.OpenPaneLength = _isCompactLayout
                ? GetInfoPanelMaxWidth()
                : InfoDrawer.Bounds.Width;
    }

    private void UpdateResponsiveLayout(double width)
    {
        var layout = ResponsiveWindowLayout.Resolve(width);
        var compact = layout.IsCompact;
        if (_isCompactLayout != compact)
        {
            _isCompactLayout = compact;
            PseudoClasses.Set(":compact", compact);
            SidebarToggleButton.IsVisible = compact;
            InfoDrawer.DisplayMode = layout.InfoPanelDisplayMode;
        }

        SidebarHost.Width = layout.SidebarWidth;
        SidebarHost.IsVisible = !compact || !_isSidebarCollapsed;
        InfoDrawer.OpenPaneLength = Math.Clamp(InfoDrawer.OpenPaneLength, 280, GetInfoPanelMaxWidth());
    }

    private double GetInfoPanelMaxWidth()
    {
        var availableWidth = InfoDrawer.Bounds.Width > 0 ? InfoDrawer.Bounds.Width : Bounds.Width;
        return _isCompactLayout
            ? Math.Max(280, availableWidth - 64)
            : Math.Max(280, Bounds.Width * 0.5);
    }

    private void ToggleSidebar(object? sender, RoutedEventArgs e)
    {
        if (!_isCompactLayout)
            return;

        if (!_isSidebarCollapsed
            && FocusManager?.GetFocusedElement() is Visual focused
            && IsInsideVisual(focused, SidebarHost))
            SidebarToggleButton.Focus();

        _isSidebarCollapsed = !_isSidebarCollapsed;
        SidebarHost.IsVisible = !_isSidebarCollapsed;
        ToolTip.SetTip(SidebarToggleButton, _isSidebarCollapsed ? "显示侧栏" : "隐藏侧栏");
    }

    private async Task AnimatePreviewWidthAsync(bool expanded, double targetWidth)
    {
        _previewAnimationCts?.Cancel();
        _previewAnimationCts?.Dispose();
        _previewAnimationCts = new CancellationTokenSource();
        var token = _previewAnimationCts.Token;
        var startWidth = InfoDrawer.OpenPaneLength;
        InfoDrawer.DisplayMode = _isCompactLayout
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;
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
            GetInfoPanelMaxWidth());
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

    private async void AddTab(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await AddTabAsync();
    }

    private async Task AddTabAsync(bool select = true)
    {
        if (_vm == null)
            return;

        var sourcePath = _vm.FileList.CurrentPath;
        var scope = App.Services.CreateScope();
        ExplorerTabViewModel? tab = null;
        try
        {
            var fileList = scope.ServiceProvider.GetRequiredService<FileListViewModel>();
            tab = _vm.AddTab(fileList, select);
            _tabScopes[tab] = scope;

            // Finder opens a new tab at the current ordinary folder. Special
            // views start at Home because their virtual state cannot be safely
            // reconstructed from a filesystem path alone.
            if (!string.IsNullOrWhiteSpace(sourcePath) && Directory.Exists(sourcePath))
                await fileList.NavigateToAsync(sourcePath);
        }
        catch (Exception ex)
        {
            if (tab != null)
            {
                _vm.RemoveTab(tab);
                tab.Dispose();
                _tabScopes.Remove(tab);
            }
            scope.Dispose();
            if (_vm?.FileList != null)
                _vm.FileList.StatusText = $"无法新建标签页：{ex.Message}";
        }
    }

    private void CloseTab(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: ExplorerTabViewModel tab })
            CloseTabCore(tab);
    }

    private void CloseTabCore(ExplorerTabViewModel tab)
    {
        if (_vm?.RemoveTab(tab) != true)
            return;

        if (_vm.Tabs.Count < _vm.PaneCount)
        {
            var fallback = _vm.Tabs.Count switch
            {
                1 => PaneLayout.Single,
                2 => PaneLayout.TwoColumns,
                3 => PaneLayout.MainLeftTwoRowsRight,
                _ => _vm.PaneLayout
            };
            _vm.SetPaneLayout(fallback);
        }

        tab.Dispose();
        if (_tabScopes.Remove(tab, out var scope))
            scope?.Dispose();
    }

    private void TogglePaneLayoutPopup(object? sender, RoutedEventArgs e)
    {
        PaneLayoutPopup.IsOpen = !PaneLayoutPopup.IsOpen;
        if (PaneLayoutPopup.IsOpen)
            Dispatcher.UIThread.Post(UpdatePaneLayoutPickerSelection);
        e.Handled = true;
    }

    private void UpdatePaneLayoutPickerSelection()
    {
        if (_vm == null || PaneLayoutPopup.Child is not Control popupContent)
            return;

        foreach (var button in popupContent.GetVisualDescendants().OfType<Button>()
                     .Where(button => button.Classes.Contains("layout-choice")))
        {
            var selected = button.Tag is string value
                           && Enum.TryParse<PaneLayout>(value, out var layout)
                           && layout == _vm.PaneLayout;
            button.Classes.Set("selected", selected);
        }
    }

    private async void ChoosePaneLayout(object? sender, RoutedEventArgs e)
    {
        PaneLayoutPopup.IsOpen = false;
        if (sender is not Button { Tag: string value }
            || !Enum.TryParse<PaneLayout>(value, out var layout))
            return;

        await ApplyPaneLayoutAsync(layout);
        e.Handled = true;
    }

    private async Task ApplyPaneLayoutAsync(PaneLayout layout)
    {
        if (_vm == null)
            return;

        var required = MainWindowViewModel.GetPaneCount(layout);
        while (_vm.Tabs.Count < required)
            await AddTabAsync(select: false);
        _vm.SetPaneLayout(layout);
    }

    private void OnPaneActivated(ExplorerTabViewModel tab)
    {
        if (_vm != null && !ReferenceEquals(_vm.SelectedTab, tab))
            _vm.SelectedTab = tab;
    }

    private void RebuildPaneLayout()
    {
        if (_vm == null || PaneLayoutRoot == null)
            return;

        PaneLayoutRoot.Children.Clear();
        PaneLayoutRoot.RowDefinitions.Clear();
        PaneLayoutRoot.ColumnDefinitions.Clear();

        var visible = _vm.VisiblePanes.Take(_vm.PaneCount).ToArray();
        var visibleSet = visible.ToHashSet();
        foreach (var removed in _paneViews.Keys.Where(tab => !visibleSet.Contains(tab)).ToArray())
        {
            var pane = _paneViews[removed];
            pane.PaneActivated -= OnPaneActivated;
            pane.DataContext = null;
            _paneViews.Remove(removed);
        }

        ExplorerPaneView GetPane(ExplorerTabViewModel tab)
        {
            if (_paneViews.TryGetValue(tab, out var existing))
                return existing;

            var pane = new ExplorerPaneView { DataContext = tab, Margin = new Thickness(2) };
            pane.PaneActivated += OnPaneActivated;
            _paneViews[tab] = pane;
            return pane;
        }

        void Configure(int rows, int columns, double[]? rowWeights = null, double[]? columnWeights = null)
        {
            for (var row = 0; row < rows; row++)
                PaneLayoutRoot.RowDefinitions.Add(new RowDefinition(
                    new GridLength(rowWeights?[row] ?? 1, GridUnitType.Star)));
            for (var column = 0; column < columns; column++)
                PaneLayoutRoot.ColumnDefinitions.Add(new ColumnDefinition(
                    new GridLength(columnWeights?[column] ?? 1, GridUnitType.Star)));
        }

        void Add(int index, int row, int column, int rowSpan = 1, int columnSpan = 1)
        {
            if (index >= visible.Length)
                return;
            var pane = GetPane(visible[index]);
            pane.SetHeaderVisible(_vm.IsMultiPane);
            Grid.SetRow(pane, row);
            Grid.SetColumn(pane, column);
            Grid.SetRowSpan(pane, rowSpan);
            Grid.SetColumnSpan(pane, columnSpan);
            PaneLayoutRoot.Children.Add(pane);
        }

        switch (_vm.PaneLayout)
        {
            case PaneLayout.Single:
                Configure(1, 1); Add(0, 0, 0); break;
            case PaneLayout.TwoColumns:
                Configure(1, 2); Add(0, 0, 0); Add(1, 0, 1); break;
            case PaneLayout.TwoRows:
                Configure(2, 1); Add(0, 0, 0); Add(1, 1, 0); break;
            case PaneLayout.ThreeColumns:
                Configure(1, 3); Add(0, 0, 0); Add(1, 0, 1); Add(2, 0, 2); break;
            case PaneLayout.ThreeRows:
                Configure(3, 1); Add(0, 0, 0); Add(1, 1, 0); Add(2, 2, 0); break;
            case PaneLayout.MainLeftTwoRowsRight:
                Configure(2, 2, columnWeights: [2, 1]);
                Add(0, 0, 0, 2); Add(1, 0, 1); Add(2, 1, 1); break;
            case PaneLayout.MainRightTwoRowsLeft:
                Configure(2, 2, columnWeights: [1, 2]);
                Add(0, 0, 1, 2); Add(1, 0, 0); Add(2, 1, 0); break;
            case PaneLayout.FourGrid:
                Configure(2, 2);
                Add(0, 0, 0); Add(1, 0, 1); Add(2, 1, 0); Add(3, 1, 1); break;
            case PaneLayout.FourColumns:
                Configure(1, 4);
                Add(0, 0, 0); Add(1, 0, 1); Add(2, 0, 2); Add(3, 0, 3); break;
            case PaneLayout.FourRows:
                Configure(4, 1);
                Add(0, 0, 0); Add(1, 1, 0); Add(2, 2, 0); Add(3, 3, 0); break;
            case PaneLayout.MainLeftThreeRowsRight:
                Configure(3, 2, columnWeights: [2, 1]);
                Add(0, 0, 0, 3); Add(1, 0, 1); Add(2, 1, 1); Add(3, 2, 1); break;
            case PaneLayout.MainRightThreeRowsLeft:
                Configure(3, 2, columnWeights: [1, 2]);
                Add(0, 0, 1, 3); Add(1, 0, 0); Add(2, 1, 0); Add(3, 2, 0); break;
        }
    }

    private void ClearPaneViews()
    {
        foreach (var pane in _paneViews.Values)
        {
            pane.PaneActivated -= OnPaneActivated;
            pane.DataContext = null;
        }
        _paneViews.Clear();
        PaneLayoutRoot?.Children.Clear();
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

    private void OpenGlobalSearch()
    {
        if (_vm?.FileList == null || IsModalInteractionBlocked)
            return;

        ToolbarControl.CloseDropdowns();
        ActivePaneView?.FileListView.DismissContextMenu();
        GlobalSearchOverlay.IsVisible = true;
        _changingGlobalSearchScope = true;
        try
        {
            GlobalSearchScopeCombo.SelectedIndex = GetGlobalSearchScopeIndex(_globalSearchScopeService.Scope);
        }
        finally
        {
            _changingGlobalSearchScope = false;
        }
        UpdateGlobalSearchCustomFolderButton();
        GlobalSearchPreviewSizeCombo.SelectedIndex = GetGlobalSearchPreviewSizeIndex();
        ApplyGlobalSearchPreviewSize();
        GlobalSearchBox.Text = string.Empty;
        _globalSearchSuggestions.Clear();
        GlobalSearchResults.IsVisible = false;
        GlobalSearchEmptyHint.IsVisible = true;
        ResetGlobalSearchPreview("选择文件以预览");
        GlobalSearchResultCount.Text = $"范围：{GetGlobalSearchScopeDisplay()}";
        Dispatcher.UIThread.Post(() => GlobalSearchBox.Focus());
    }

    private void CloseGlobalSearch()
    {
        _globalSearchCts?.Cancel();
        _globalSearchCts?.Dispose();
        _globalSearchCts = null;
        _globalSearchPreviewCts?.Cancel();
        _globalSearchPreviewCts?.Dispose();
        _globalSearchPreviewCts = null;
        _globalSearchPreviewBitmap?.Dispose();
        _globalSearchPreviewBitmap = null;
        GlobalSearchPreviewImage.Source = null;
        ClearGlobalSearchFolderThumbnails();
        _globalSearchFolderEntries.Clear();
        GlobalSearchOverlay.IsVisible = false;
        _globalSearchSuggestions.Clear();
        GlobalSearchResults.SelectedIndex = -1;
        Focus(NavigationMethod.Unspecified, KeyModifiers.None);
    }

    private void OnGlobalSearchTextChanged(object? sender, TextChangedEventArgs e)
        => _ = RefreshGlobalSearchAsync();

    private async Task RefreshGlobalSearchAsync()
    {
        if (!GlobalSearchOverlay.IsVisible || _vm?.FileList == null)
            return;

        _globalSearchCts?.Cancel();
        _globalSearchCts?.Dispose();
        _globalSearchCts = new CancellationTokenSource();
        var cancellationToken = _globalSearchCts.Token;
        var query = GlobalSearchBox.Text?.Trim() ?? string.Empty;

        if (query.Length == 0)
        {
            _globalSearchSuggestions.Clear();
            GlobalSearchResults.IsVisible = false;
            GlobalSearchEmptyHint.Text = "输入内容即可搜索已索引的本地文件、收藏夹和最近位置";
            GlobalSearchEmptyHint.IsVisible = true;
            GlobalSearchResultCount.Text = $"范围：{GetGlobalSearchScopeDisplay()}";
            return;
        }

        GlobalSearchEmptyHint.Text = "正在搜索…";
        GlobalSearchEmptyHint.IsVisible = true;
        GlobalSearchResults.IsVisible = false;

        try
        {
            // A short debounce makes results feel instant without issuing an index
            // query for every intermediate composition character.
            await Task.Delay(TimeSpan.FromMilliseconds(90), cancellationToken);
            var suggestions = await OmniboxService.GetSuggestionsAsync(
                _vm.FileList, query, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !GlobalSearchOverlay.IsVisible)
                return;

            _globalSearchSuggestions.Clear();
            foreach (var suggestion in suggestions.Take(40))
                _globalSearchSuggestions.Add(suggestion);

            GlobalSearchResults.SelectedIndex = _globalSearchSuggestions.Count > 0 ? 0 : -1;
            GlobalSearchResults.IsVisible = _globalSearchSuggestions.Count > 0;
            GlobalSearchEmptyHint.IsVisible = _globalSearchSuggestions.Count == 0;
            if (_globalSearchSuggestions.Count == 0)
                GlobalSearchEmptyHint.Text = "没有找到匹配项";
            GlobalSearchResultCount.Text = _globalSearchSuggestions.Count == 0
                ? "可换用更短的文件名或路径片段"
                : $"找到 {_globalSearchSuggestions.Count} 项（范围：{GetGlobalSearchScopeDisplay()}）";
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke replaced this request.
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _globalSearchSuggestions.Clear();
                GlobalSearchResults.IsVisible = false;
                GlobalSearchEmptyHint.Text = "搜索暂时不可用";
                GlobalSearchEmptyHint.IsVisible = true;
                GlobalSearchResultCount.Text = "请稍后重试";
            }
        }
    }

    private async void OnGlobalSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseGlobalSearch();
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            MoveGlobalSearchSelection(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            var suggestion = GlobalSearchResults.SelectedItem as OmniboxSuggestion
                             ?? _globalSearchSuggestions.FirstOrDefault();
            if (suggestion != null)
            {
                e.Handled = true;
                await OpenGlobalSearchSuggestionAsync(suggestion);
            }
        }
    }

    private void MoveGlobalSearchSelection(int delta)
    {
        var count = _globalSearchSuggestions.Count;
        if (count == 0)
            return;

        var next = GlobalSearchResults.SelectedIndex < 0
            ? (delta > 0 ? 0 : count - 1)
            : (GlobalSearchResults.SelectedIndex + delta + count) % count;
        GlobalSearchResults.SelectedIndex = next;
        GlobalSearchResults.ScrollIntoView(_globalSearchSuggestions[next]);
    }

    private async void OnGlobalSearchScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changingGlobalSearchScope)
            return;

        if (GlobalSearchScopeCombo.SelectedItem is not ComboBoxItem { Tag: string value }
            || !Enum.TryParse<GlobalSearchScope>(value, out var scope))
            return;

        if (scope == GlobalSearchScope.CustomFolders)
        {
            // The list is maintained in Settings → Locations. Only open the
            // picker for an older profile that has no locations yet; selecting
            // this scope should otherwise be immediate and predictable.
            if (_globalSearchScopeService.CustomFolders.Count == 0)
            {
                var selectedPaths = await PickGlobalSearchFoldersAsync("选择搜索文件夹");
                if (selectedPaths.Count == 0)
                {
                    RestoreGlobalSearchScopeSelection();
                    return;
                }

                _globalSearchScopeService.SetCustomFolders(selectedPaths);
                if (_globalSearchScopeService.CustomFolders.Count == 0)
                {
                    RestoreGlobalSearchScopeSelection();
                    return;
                }
            }
        }

        _globalSearchScopeService.Scope = scope;
        UpdateGlobalSearchCustomFolderButton();
        _ = RefreshGlobalSearchAsync();
    }

    private async void OnAddGlobalSearchFolder(object? sender, RoutedEventArgs e)
    {
        if (_globalSearchScopeService.Scope != GlobalSearchScope.CustomFolders)
            return;

        var selectedPaths = await PickGlobalSearchFoldersAsync("添加搜索文件夹");
        if (selectedPaths.Count == 0)
            return;

        _globalSearchScopeService.SetCustomFolders(
            _globalSearchScopeService.CustomFolders.Concat(selectedPaths));
        UpdateGlobalSearchCustomFolderButton();
        _ = RefreshGlobalSearchAsync();
    }

    private async Task<IReadOnlyList<string>> PickGlobalSearchFoldersAsync(string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null)
            return [];

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = true
        });

        return folders
            .Select(folder => folder.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void UpdateGlobalSearchCustomFolderButton()
    {
        GlobalSearchAddFolderButton.IsVisible =
            _globalSearchScopeService.Scope == GlobalSearchScope.CustomFolders;
    }

    private void RestoreGlobalSearchScopeSelection()
    {
        _changingGlobalSearchScope = true;
        try
        {
            GlobalSearchScopeCombo.SelectedIndex = GetGlobalSearchScopeIndex(_globalSearchScopeService.Scope);
        }
        finally
        {
            _changingGlobalSearchScope = false;
        }
    }

    private static int GetGlobalSearchScopeIndex(GlobalSearchScope scope) => scope switch
    {
        GlobalSearchScope.CurrentFolder => 0,
        GlobalSearchScope.CustomFolders => 2,
        _ => 1
    };

    private void OnGlobalSearchPreviewSizeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GlobalSearchPreviewSizeCombo.SelectedItem is not ComboBoxItem { Tag: string value })
            return;

        App.Services.GetRequiredService<ISettingsService>().Set("global_search_preview_size", value);
        ApplyGlobalSearchPreviewSize();
        if (GlobalSearchResults.SelectedItem is OmniboxSuggestion suggestion)
            _ = LoadGlobalSearchPreviewAsync(suggestion);
    }

    private void OnGlobalSearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GlobalSearchResults.SelectedItem is OmniboxSuggestion suggestion)
            _ = LoadGlobalSearchPreviewAsync(suggestion);
        else
            ResetGlobalSearchPreview("选择文件以预览");
    }

    private int GetGlobalSearchPreviewSizeIndex()
    {
        var value = App.Services.GetRequiredService<ISettingsService>()
            .Get("global_search_preview_size", "Medium");
        return value.Equals("Small", StringComparison.OrdinalIgnoreCase) ? 0
            : value.Equals("Large", StringComparison.OrdinalIgnoreCase) ? 2
            : 1;
    }

    private void ApplyGlobalSearchPreviewSize()
    {
        var index = GlobalSearchPreviewSizeCombo.SelectedIndex;
        var (panelWidth, imageSize, dialogWidth) = index switch
        {
            0 => (170d, 120d, 800d),
            2 => (300d, 250d, 960d),
            _ => (220d, 180d, 860d)
        };

        GlobalSearchPreviewPanel.Width = panelWidth;
        GlobalSearchPreviewImage.Width = imageSize;
        GlobalSearchPreviewImage.Height = imageSize;
        GlobalSearchPanel.Width = dialogWidth;
    }

    private string GetGlobalSearchScopeDisplay() => _globalSearchScopeService.Scope switch
    {
        GlobalSearchScope.CurrentFolder => "当前文件夹",
        GlobalSearchScope.UserFolder => "用户文件夹",
        GlobalSearchScope.CustomFolders => _globalSearchScopeService.CustomFolders.Count == 1
            ? $"自定义：{Path.GetFileName(_globalSearchScopeService.CustomFolders[0].TrimEnd(Path.DirectorySeparatorChar))}"
            : $"自定义位置（{_globalSearchScopeService.CustomFolders.Count} 个）",
        _ => "这台 Mac"
    };

    private async Task LoadGlobalSearchPreviewAsync(OmniboxSuggestion suggestion)
    {
        _globalSearchPreviewCts?.Cancel();
        _globalSearchPreviewCts?.Dispose();
        _globalSearchPreviewCts = new CancellationTokenSource();
        var cancellationToken = _globalSearchPreviewCts.Token;

        GlobalSearchPreviewTitle.Text = suggestion.Title;
        GlobalSearchPreviewPath.Text = suggestion.Subtitle;
        if (suggestion.Entry is not { IsVirtual: false } entry)
        {
            ResetGlobalSearchPreview(suggestion.Kind == OmniboxSuggestionKind.Path
                ? "文件夹没有缩略图预览"
                : "此项目没有可用预览", preserveLabels: true);
            return;
        }

        ResetGlobalSearchPreview("正在生成预览…", preserveLabels: true);

        // A directory result is useful even without a thumbnail: show its
        // immediate children in the preview pane so users can inspect the
        // matched folder without leaving global search.
        if (entry.IsDirectory)
        {
            await LoadGlobalSearchFolderContentsAsync(entry, cancellationToken);
            return;
        }

        try
        {
            var thumbnailService = App.Services.GetService<IThumbnailService>();
            var pixelSize = (int)Math.Max(GlobalSearchPreviewImage.Width, GlobalSearchPreviewImage.Height) * 2;
            var thumbnail = thumbnailService == null
                ? null
                : await thumbnailService.GetThumbnailResultAsync(entry.FullPath, pixelSize, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            if (thumbnail is not { Bytes.Length: > 0 })
            {
                ResetGlobalSearchPreview("此文件暂无可用预览", preserveLabels: true);
                return;
            }

            using var stream = new MemoryStream(thumbnail.Bytes);
            var bitmap = new global::Avalonia.Media.Imaging.Bitmap(stream);
            _globalSearchPreviewBitmap?.Dispose();
            _globalSearchPreviewBitmap = bitmap;
            GlobalSearchPreviewImage.Source = bitmap;
            GlobalSearchPreviewImage.IsVisible = true;
            GlobalSearchPreviewPlaceholder.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this preview.
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
                ResetGlobalSearchPreview("无法生成预览", preserveLabels: true);
        }
    }

    private void ResetGlobalSearchPreview(string placeholder, bool preserveLabels = false)
    {
        _globalSearchPreviewBitmap?.Dispose();
        _globalSearchPreviewBitmap = null;
        GlobalSearchPreviewImage.Source = null;
        ClearGlobalSearchFolderThumbnails();
        GlobalSearchPreviewImage.IsVisible = false;
        _globalSearchFolderEntries.Clear();
        GlobalSearchFolderContents.IsVisible = false;
        GlobalSearchPreviewPlaceholder.Text = placeholder;
        GlobalSearchPreviewPlaceholder.IsVisible = true;
        if (!preserveLabels)
        {
            GlobalSearchPreviewTitle.Text = string.Empty;
            GlobalSearchPreviewPath.Text = string.Empty;
        }
    }

    private async Task LoadGlobalSearchFolderContentsAsync(FileSystemEntry folder, CancellationToken cancellationToken)
    {
        try
        {
            var fileService = App.Services.GetService<IFileService>();
            if (fileService == null)
            {
                ResetGlobalSearchPreview("文件夹内容暂时不可用", preserveLabels: true);
                return;
            }

            var entries = await fileService.GetDirectoryContentsAsync(folder.FullPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var visibleEntries = entries
                .Where(ShouldShowGlobalSearchFolderEntry)
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToArray();

            _globalSearchFolderEntries.Clear();
            foreach (var entry in visibleEntries)
                _globalSearchFolderEntries.Add(entry);

            GlobalSearchPreviewTitle.Text = visibleEntries.Length == 0
                ? $"{folder.Name} · 空文件夹"
                : $"{folder.Name} · {visibleEntries.Length} 项";
            GlobalSearchPreviewImage.IsVisible = false;
            GlobalSearchFolderContents.IsVisible = visibleEntries.Length > 0;
            GlobalSearchPreviewPlaceholder.Text = visibleEntries.Length == 0
                ? "此文件夹为空"
                : string.Empty;
            GlobalSearchPreviewPlaceholder.IsVisible = visibleEntries.Length == 0;

            if (visibleEntries.Length > 0)
            {
                // The list is initially hidden while its items are added, so its
                // Image.Loaded events are not reliable enough to start thumbnail
                // work.  Run a small pre-load after the preview becomes visible.
                Dispatcher.UIThread.Post(
                    () => _ = PrimeGlobalSearchFolderThumbnailsAsync(visibleEntries, cancellationToken),
                    DispatcherPriority.Render);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
                ResetGlobalSearchPreview("无法读取文件夹内容", preserveLabels: true);
        }
    }

    private bool ShouldShowGlobalSearchFolderEntry(FileSystemEntry entry)
    {
        var settings = App.Services.GetService<ISettingsService>();
        if (settings?.Get("HideSystemFiles", true) == true
            && (entry.Name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (settings?.Get("HideDotFiles", true) == true
            && !entry.IsDirectory && entry.Name.StartsWith('.'))
            return false;
        if (settings?.Get("HideDotFolders", true) == true
            && entry.IsDirectory && entry.Name.StartsWith('.'))
            return false;
        return true;
    }

    private async void OnGlobalSearchFolderImageLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Image image)
            await LoadGlobalSearchFolderImageAsync(image);
    }

    private void OnGlobalSearchFolderImageDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Image image && image.DataContext is FileSystemEntry entry)
        {
            SetGlobalSearchFolderImageFallback(image, entry);
            _ = LoadGlobalSearchFolderImageAsync(image);
        }
    }

    private async Task LoadGlobalSearchFolderImageAsync(Image image)
    {
        if (image.DataContext is not FileSystemEntry entry
            || entry.IsVirtual)
            return;

        SetGlobalSearchFolderImageFallback(image, entry);
        if (entry.IsDirectory || !_globalSearchFolderEntries.Contains(entry))
            return;

        var cancellationToken = _globalSearchPreviewCts?.Token ?? CancellationToken.None;
        try
        {
            var bitmap = await GetGlobalSearchFolderThumbnailAsync(entry, cancellationToken);
            if (bitmap == null || cancellationToken.IsCancellationRequested
                || image.DataContext is not FileSystemEntry current
                || !string.Equals(current.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase)
                || !_globalSearchFolderEntries.Contains(entry))
                return;

            image.Source = bitmap;
            ApplyGlobalSearchFolderThumbnail(entry, bitmap);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Keep the converter-provided file icon as a fallback.
        }
    }

    private async Task PrimeGlobalSearchFolderThumbnailsAsync(
        IReadOnlyList<FileSystemEntry> entries,
        CancellationToken cancellationToken)
    {
        // Preview the first visible page now; further rows continue to be loaded
        // through their Image.Loaded handler when the user scrolls.
        foreach (var entry in entries.Where(entry => !entry.IsDirectory && !entry.IsVirtual).Take(24))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = await GetGlobalSearchFolderThumbnailAsync(entry, cancellationToken);
            if (bitmap != null && !cancellationToken.IsCancellationRequested)
                ApplyGlobalSearchFolderThumbnail(entry, bitmap);
        }
    }

    private async Task<global::Avalonia.Media.Imaging.Bitmap?> GetGlobalSearchFolderThumbnailAsync(
        FileSystemEntry entry,
        CancellationToken cancellationToken)
    {
        if (_globalSearchFolderThumbnailBitmaps.TryGetValue(entry.FullPath, out var cached))
            return cached;

        var thumbnailService = App.Services.GetService<IThumbnailService>();
        if (thumbnailService == null)
            return null;

        var thumbnail = await thumbnailService.GetThumbnailResultAsync(entry.FullPath, 192, cancellationToken);
        if (thumbnail is not { Bytes.Length: > 0 } || cancellationToken.IsCancellationRequested)
            return null;

        using var stream = new MemoryStream(thumbnail.Bytes, writable: false);
        var bitmap = new global::Avalonia.Media.Imaging.Bitmap(stream);
        if (_globalSearchFolderThumbnailBitmaps.TryGetValue(entry.FullPath, out var previous))
        {
            bitmap.Dispose();
            return previous;
        }

        _globalSearchFolderThumbnailBitmaps[entry.FullPath] = bitmap;
        return bitmap;
    }

    private void ApplyGlobalSearchFolderThumbnail(
        FileSystemEntry entry,
        global::Avalonia.Media.Imaging.Bitmap bitmap)
    {
        foreach (var image in GlobalSearchFolderContents.GetVisualDescendants().OfType<Image>())
        {
            if (image.DataContext is FileSystemEntry current
                && string.Equals(current.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase))
                image.Source = bitmap;
        }
    }

    private static void SetGlobalSearchFolderImageFallback(Image image, FileSystemEntry entry)
    {
        try
        {
            image.Source = GlobalSearchFileIconConverter.Convert(
                entry,
                typeof(global::Avalonia.Media.IImage),
                32,
                System.Globalization.CultureInfo.InvariantCulture) as global::Avalonia.Media.IImage;
        }
        catch
        {
            image.Source = null;
        }
    }

    private void ClearGlobalSearchFolderThumbnails()
    {
        foreach (var bitmap in _globalSearchFolderThumbnailBitmaps.Values)
            bitmap.Dispose();
        _globalSearchFolderThumbnailBitmaps.Clear();
    }

    private async void OnGlobalSearchFolderEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FileSystemEntry entry || _vm?.FileList == null)
            return;

        e.Handled = true;
        CloseGlobalSearch();
        try
        {
            await _vm.FileList.OpenEntryAsync(entry);
        }
        catch (Exception ex)
        {
            _vm.FileList.StatusText = $"打开文件失败: {ex.Message}";
        }
    }

    private async void OnGlobalSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is OmniboxSuggestion suggestion)
        {
            e.Handled = true;
            await OpenGlobalSearchSuggestionAsync(suggestion);
        }
    }

    private async Task OpenGlobalSearchSuggestionAsync(OmniboxSuggestion suggestion)
    {
        if (_vm?.FileList == null)
            return;

        CloseGlobalSearch();
        // Search results represent concrete files/folders. Open them through
        // the same command used by the file list so PDFs and other documents
        // are handed to the platform launcher instead of merely being revealed.
        try
        {
            if (suggestion.Entry != null)
            {
                await _vm.FileList.OpenEntryAsync(suggestion.Entry);
                return;
            }

            await OmniboxService.ExecuteAsync(_vm.FileList, suggestion);
        }
        catch (Exception ex)
        {
            _vm.FileList.StatusText = $"打开文件失败: {ex.Message}";
        }
    }

    private void CloseGlobalSearchFromBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (!IsInsideVisual(e.Source as Visual, GlobalSearchPanel))
        {
            e.Handled = true;
            CloseGlobalSearch();
        }
    }

    public void OpenSettings()
    {
        _ = OpenSettingsAsync();
    }

    private async Task OpenSettingsAsync()
    {
        if (_settingsDialog?.IsVisible == true)
        {
            _settingsDialog.Activate();
            return;
        }

        var dialog = new SettingsDialog
        {
            DataContext = _vm?.FileList
        };

        _settingsDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsDialog, dialog))
                _settingsDialog = null;
        };

        using var modalBlock = BlockModalParentInteraction();
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            if (ReferenceEquals(_settingsDialog, dialog))
                _settingsDialog = null;
        }
    }
}
