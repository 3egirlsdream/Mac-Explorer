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
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
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
        "TitleBarBackgroundBrush",
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

    private SettingsDialog? _settingsDialog;
    private WindowNotificationManager? _notifications;
    private MainWindowViewModel? _vm;
    private FileListViewModel? _activeFileList;
    private readonly Dictionary<ExplorerTabViewModel, IServiceScope?> _tabScopes = [];
    private readonly Dictionary<ExplorerTabViewModel, ExplorerWorkspaceView> _workspaceViews = [];
    private readonly Dictionary<ExplorerWorkspaceView, Task> _workspaceDetachTasks = [];
    private readonly LinkedList<ExplorerTabViewModel> _workspaceRecency = [];
    private const int BackgroundWorkspaceLimit = 3;
    private PaneLayout? _renderedPaneLayout;
    private readonly LivePreviewCoordinator _livePreviewCoordinator;
    private readonly NavigationBridge _navigationBridge;
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
    private CancellationTokenSource? _globalSearchCts;
    private SuperPreviewView? _globalSearchPreview;

    // Task overlay panel state machine
    private enum PanelMode { None, Auto, Manual }
    private PanelMode _taskPanelMode = PanelMode.None;
    private bool _isTaskPanelAnimating;
    private CancellationTokenSource? _taskPanelAnimCts;
    private CancellationTokenSource? _autoCloseTimerCts;
    private const double TaskPanelAnimDurationMs = 220;

    private long _paneLayoutGeneration;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    private ExplorerWorkspaceView? ActiveWorkspace
        => _vm?.SelectedTab != null && _workspaceViews.TryGetValue(_vm.SelectedTab, out var workspace)
            ? workspace
            : null;

    public string? InitialNavigationPath { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        WindowDecorationProperties.SetElementRole(TitleBarDragArea,
            OperatingSystem.IsMacOS() ? WindowDecorationsElementRole.User : WindowDecorationsElementRole.TitleBar);
        TabSurface.TabStrip = TabList;
        _navigationBridge = App.Services.GetRequiredService<NavigationBridge>();
        _dragDropBridge = App.Services.GetRequiredService<IDragDropBridge>();
        _taskManager = App.Services.GetRequiredService<IBackgroundTaskManager>();
        _globalSearchScopeService = App.Services.GetRequiredService<IGlobalSearchScopeService>();
        _livePreviewCoordinator = new LivePreviewCoordinator(workspace =>
            workspace is ExplorerWorkspaceView view
            && ReferenceEquals(view, ActiveWorkspace)
            && !IsMarkdownEditorOpen
            && view.Tab != null
            && _vm?.VisiblePanes.Contains(view.Tab) == true);
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        SizeChanged += (_, _) => UpdateTaskPanelWidth();
        Activated += OnActivated;
        Closing += OnClosing;
        Closed += OnClosed;
        Application.Current?.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        _taskManager.TasksChanged += OnTasksChanged;
        _taskHistory = App.Services.GetService<IFileOperationHistoryService>();
        if (_taskHistory != null) _taskHistory.HistoryChanged += OnTasksChanged;
        App.Services.GetRequiredService<ISettingsService>().SettingChanged += OnCopilotSettingChanged;
        ApplyCopilotEnabled();
        ApplyAppearanceSettings();
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        SuperPreviewControl.RequestClose += OnSuperPreviewClosed;
        PositionChanged += (_, _) => ActiveWorkspace?.CloseTransientUi(null);
        Deactivated += (_, _) =>
        {
            ActiveWorkspace?.CloseTransientUi(null);
            PaneLayoutPopup.IsOpen = false;
        };
        GlobalSearchResults.ItemsSource = _globalSearchSuggestions;

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
        if (IsMarkdownEditorOpen && !IsModalInteractionBlocked)
            return;

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

        ActiveWorkspace?.CloseTransientUi(e.Source);
        // Keep the layout picker open during native title-bar dragging. Popup light-dismiss
        // closes on window movement, so dismiss outside clicks in the window instead.
        var isTitleBarPress = e.GetPosition(this).Y < (RootLayout.TranslatePoint(default, this)?.Y ?? 0);
        if (IsInsideVisual(e.Source as Visual, TabScrollViewer)
            || (!isTitleBarPress && !IsInsideVisual(e.Source as Visual, PaneLayoutPopup.Child as Visual)))
            PaneLayoutPopup.IsOpen = false;
        ClearTextInputFocusFromPointerSource(e.Source);
    }

    private void OnTitleBarBlankPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!OperatingSystem.IsMacOS() || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;
        // Avalonia counts consecutive downs even after a drag; AppKit resets its click count.
        if (MacWindowChrome.IsCurrentMouseDoubleClick())
        {
            if (WindowState == WindowState.Normal)
                WindowState = WindowState.Maximized;
            else if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
        }
        else
        {
            BeginMoveDrag(e);
        }
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
        if (Services.Subscriptions.SubscriptionAccess.IsLocked) return;
        if (e.Handled || Shortcuts.IsRecording) return;
        if (IsInsideTextInput(e.Source as Visual) && Services.Impl.ShortcutService.IsTextEditingGesture(e)) return;
        if (_homeFolderTransition != null)
        {
            if (e.Key == Key.Escape) { _homeFolderTransition.Close(); e.Handled = true; }
            return;
        }
        if (IsMarkdownEditorOpen && !IsModalInteractionBlocked)
        {
            _markdownEditor!.HandleWindowKeyDown(e);
            return;
        }

        if (IsModalInteractionBlocked)
        {
            if (!IsInsideVisual(e.Source as Visual, DialogHost))
                e.Handled = true;
            return;
        }

        if (CopilotPanel.IsVisible && IsInsideVisual(e.Source as Visual, CopilotInput))
        {
            CopilotInputKeyDown(sender, e);
            if (e.Handled) return;
        }

        if (e.Key == Key.Escape && PaneLayoutPopup.IsOpen)
        {
            PaneLayoutPopup.IsOpen = false;
            PaneLayoutButton.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SuperPreviewControl.IsVisible)
        {
            e.Handled = true;
            SuperPreviewControl.Close();
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
                e.Handled = true;
                _ = ExecuteGlobalSearchInputAsync();
            }
            return;
        }

        // Space opens the in-window super preview for the active pane. The
        // preview owns the rest of the keyboard interaction until it closes,
        // so normal file commands cannot leak through the overlay.
        if (SuperPreviewControl.IsVisible)
            return;

        if (e.Key == Key.Space
            && e.KeyModifiers == KeyModifiers.None
            && !IsInsideTextInput(e.Source as Visual)
            && ActiveWorkspace?.FileListView.IsVisible == true
            && _vm?.FileList.SelectedEntries.Count == 1)
        {
            e.Handled = true;
            _ = OpenSuperPreviewAsync(_vm.FileList.SelectedEntries[0]);
            return;
        }

        if (Shortcuts.Matches(ShortcutIds.PageSearch, e))
        {
            e.Handled = true;
            ActiveWorkspace?.TogglePageSearch();
            return;
        }

        if (e.Key == Key.Escape && ActiveWorkspace?.TryHandleEscape() == true)
        {
            e.Handled = true;
            return;
        }

        // Conventional shortcuts for global quick search.
        if (Shortcuts.Matches(ShortcutIds.GlobalSearch, e))
        {
            e.Handled = true;
            OpenGlobalSearch();
            return;
        }

        // Finder/browser tab shortcuts. Handle these before the file list so
        // ⌘W closes the current tab instead of the whole window when possible.
        if (Shortcuts.Matches(ShortcutIds.NewTab, e))
        {
            e.Handled = true;
            _ = AddTabAsync();
            return;
        }

        if (Shortcuts.Matches(ShortcutIds.CloseTab, e) && _vm?.SelectedTab != null)
        {
            e.Handled = true;
            if (_vm.Tabs.Count == 1)
                Close();
            else
                _ = CloseTabCoreAsync(_vm.SelectedTab);
            return;
        }

        if (_vm != null && (Shortcuts.Matches(ShortcutIds.NextTab, e) || Shortcuts.Matches(ShortcutIds.PreviousTab, e)))
        {
            e.Handled = true;
            _vm.SelectRelativeTab(Shortcuts.Matches(ShortcutIds.PreviousTab, e) ? -1 : 1);
            return;
        }

        // ⌘Z: undo last file operation
        if (!IsInsideTextInput(e.Source as Visual) && Shortcuts.Matches(ShortcutIds.Undo, e))
        {
            e.Handled = true;
            _ = UndoLastOperationAsync();
            return;
        }

        if (Shortcuts.Matches(ShortcutIds.PathInput, e))
        {
            e.Handled = true;
            ActiveWorkspace?.FocusPathInput();
            return;
        }

        ActiveWorkspace?.TryHandleFileShortcut(e);
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
            ActiveWorkspace?.CloseTransientUi(null);
            PaneLayoutPopup.IsOpen = false;
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
        if (_vm?.FileList == null) return;
        if (Directory.Exists(path))
            await _vm.FileList.NavigateToAsync(path);
        else if (File.Exists(path))
            await _vm.FileList.RevealFileAsync(new FileSystemEntry
            {
                FullPath = Path.GetFullPath(path),
                Name = Path.GetFileName(path)
            });
    }

    public void ApplyAppearanceSettings()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        var enabled = settings.Get("vibrancy_enabled", false);
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
            SchedulePaneLayoutRebuild();
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
            SchedulePaneLayoutRebuild();
        else if (e.PropertyName is nameof(MainWindowViewModel.SelectedTab)
                 or nameof(MainWindowViewModel.ActivePaneSlotIndex))
        {
            CloseHomeFolderImmediately();
            TouchSelectedWorkspace();
            _ = ActivateSelectedWorkspaceAsync();
        }
    }

    private void OnVisiblePanesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => SchedulePaneLayoutRebuild();

    private void ActivateFileList(FileListViewModel fileList)
    {
        if (ReferenceEquals(_activeFileList, fileList))
            return;

        DeactivateFileList();
        _activeFileList = fileList;
        fileList.PropertyChanged += OnFileListPropertyChanged;
        RegisterViewModel(fileList);
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
        if (e.PropertyName is nameof(FileListViewModel.IsPasteConfirmDialogVisible)
            or nameof(FileListViewModel.IsMoveConfirmDialogVisible)
            or nameof(FileListViewModel.IsDeleteConfirmDialogVisible)
            or nameof(FileListViewModel.IsTagDeleteConfirmDialogVisible)
            or nameof(FileListViewModel.IsCompressDialogVisible))
        {
            Dispatcher.UIThread.Post(() => _ = SyncDialogsAsync());
        }
    }

    private void RegisterViewModel(FileListViewModel vm)
    {
        vm.SetOwnerWindow(this);
        _navigationBridge.Register(vm);
        _dragDropBridge.Register(vm);
    }

    private void UnregisterViewModel(FileListViewModel vm)
    {
        vm.SetOwnerWindow(null);
        _navigationBridge.Unregister(vm);
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
        await dialog.ShowDialogAsync(this, _vm.FileList, _vm.FileList.TakeBatchRenameRequest());
    }

    private async Task OpenRemoteConnectionDialogAsync()
    {
        if (_vm?.FileList == null) return;

        var dialog = new RemoteConnectionDialog();
        using var modalBlock = BlockModalParentInteraction();
        var result = await dialog.ShowDialog<RemoteServerInfo?>(this);
        if (result != null && dialog.Connected)
            if (dialog.NavigationPath != null) await _vm.FileList.NavigateToAsync(dialog.NavigationPath);
            else await _vm.FileList.ConnectToServerAsync(result);
    }

    private async Task UndoLastOperationAsync()
    {
        var historyService = App.Services.GetService<IFileOperationHistoryService>();
        if (historyService == null) return;
        var undone = await historyService.UndoLastAsync();
        if (undone && _vm?.FileList != null)
        {
            _vm.FileList.StatusText = MacExplorer.Services.Impl.LocalizationText.Get("已撤销");
            await _vm.FileList.RefreshAsync();
        }
        else if (!undone && _vm?.FileList != null)
        {
            _vm.FileList.StatusText = MacExplorer.Services.Impl.LocalizationText.Get("没有可撤销的操作");
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
        {
            if (File.Exists(path))
                await NavigateToPathAsync(path);
            else
                await _vm.FileList.NavigateToAsync(path);
        }
        SchedulePaneLayoutRebuild();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        _navigationBridge.SetActive(_vm.FileList);
        _dragDropBridge.SetActive(_vm.FileList);
        _ = ActivateSelectedWorkspaceAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownCompleted)
            return;

        e.Cancel = true;
        if (_shutdownStarted)
            return;
        _shutdownStarted = true;
        // Ask before entering the existing shutdown finally block, which always closes the window.
        if (!await CloseMarkdownEditorForShutdownAsync())
        {
            _shutdownStarted = false;
            return;
        }
        try
        {
            await CloseCopilotAsync();
            await ShutdownWorkspaceStateAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed while shutting down workspace state: {ex}");
        }
        finally
        {
            _shutdownCompleted = true;
            Close();
        }
    }

    private async Task ShutdownWorkspaceStateAsync()
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
            _vm.VisiblePanes.CollectionChanged -= OnVisiblePanesChanged;
        }

        try
        {
            foreach (var tab in _workspaceViews.Keys.ToArray())
            {
                try
                {
                    await DetachWorkspaceAsync(tab, "window-closing");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to detach workspace while closing: {ex}");
                }
            }

            await Task.WhenAll(_workspaceDetachTasks.Values.ToArray());

            try
            {
                await _livePreviewCoordinator.DisposeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to dispose live preview coordinator: {ex}");
            }
        }
        finally
        {
            DeactivateFileList();
            foreach (var tab in _vm?.Tabs ?? [])
                tab.Dispose();
            foreach (var scope in _tabScopes.Values)
                scope?.Dispose();
            _tabScopes.Clear();
            _scope?.Dispose();
            _scope = null;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        App.Services.GetRequiredService<ISettingsService>().SettingChanged -= OnCopilotSettingChanged;
        _copilot?.Dispose();
        _copilot = null;
        CloseHomeFolderImmediately();
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        _taskPanelAnimCts?.Cancel();
        _autoCloseTimerCts?.Cancel();
        _taskRefreshTimer?.Dispose();
        _globalSearchCts?.Cancel();
        _globalSearchCts?.Dispose();
        _globalSearchCts = null;
        CloseGlobalSearchPreview();
        DisposeMarkdownEditor();
        SuperPreviewControl.RequestClose -= OnSuperPreviewClosed;
        SuperPreviewControl.Close();
        _taskManager.TasksChanged -= OnTasksChanged;
        if (_taskHistory != null) _taskHistory.HistoryChanged -= OnTasksChanged;
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => ApplyAppearanceSettings();

    private async Task OpenSuperPreviewAsync(FileSystemEntry entry)
    {
        if (IsMarkdownEditorOpen || SuperPreviewControl.IsVisible)
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
                var message = vm.DeleteConfirmIncludesRemoteFiles
                    ? $"确定要删除{label}吗？远程项目将直接删除，无法通过废纸篓恢复；本地项目将移到废纸篓。"
                    : $"确定要将{label}移到废纸篓吗？";
                var confirmed = await ShowConfirmationAsync("确认删除", message, "删除");
                if (confirmed) await vm.ConfirmDeleteSelectedAsync();
                else vm.CancelDeleteConfirmDialog();
            }
            else if (vm.IsTagDeleteConfirmDialogVisible)
            {
                var confirmed = await ShowConfirmationAsync("删除标签", vm.TagDeleteConfirmation, "删除");
                if (confirmed) await vm.ConfirmDeleteTagAsync();
                else vm.CancelTagDeleteConfirmDialog();
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

    private FileTaskPanelPresenter? _fileTaskPresenter;
    private IFileOperationHistoryService? _taskHistory;
    private int _taskRefreshPending;
    private IDisposable? _taskRefreshTimer;

    private void OnTasksChanged()
    {
        if (Interlocked.Exchange(ref _taskRefreshPending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_shutdownStarted) return;
            _taskRefreshTimer = DispatcherTimer.RunOnce(RefreshTaskUi, TimeSpan.FromMilliseconds(50), DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void RefreshTaskUi()
    {
        _taskRefreshTimer = null;
        Interlocked.Exchange(ref _taskRefreshPending, 0);
        if (_shutdownStarted) return;
        var tasks = _taskManager.Tasks.ToList();
        var runningCount = tasks.Count(t => t.State == BackgroundTaskState.Running);

        UpdateTaskButton();
        if (TaskOverlayPanel.IsVisible && !_isTaskPanelAnimating)
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
    }

    private void UpdateTaskButton()
    {
        var tasks = _taskManager.Tasks;
        TaskButton.IsVisible = tasks.Count > 0;
        var running = tasks.Count(t => t.State == BackgroundTaskState.Running);
        TaskButtonText.Text = Services.Impl.LocalizationText.Get("后台任务 ({0})", running > 0 ? running : tasks.Count);
    }

    // ──────────────────────────────────────────────
    //  Task Overlay Panel
    // ──────────────────────────────────────────────

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
        var animationCts = _taskPanelAnimCts;
        var token = animationCts.Token;
        _isTaskPanelAnimating = true;
        CompositionVisual? visual = null;
        try
        {
            if (show)
            {
                UpdateTaskPanelWidth();
                RebuildTaskPanelItems(_taskManager.Tasks);
                TaskOverlayPanel.IsVisible = true;
                TaskOverlayPanel.UpdateLayout();
            }
            TaskOverlayPanel.IsHitTestVisible = show;
            visual = ElementComposition.GetElementVisual(TaskOverlayPanel);
            if (visual != null)
            {
                var distance = TaskOverlayPanel.Width + 24;
                var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
                animation.Duration = TimeSpan.FromMilliseconds(TaskPanelAnimDurationMs);
                animation.InsertKeyFrame(0, new Vector3D(show ? distance : 0, 0, 0));
                animation.InsertKeyFrame(1, new Vector3D(show ? 0 : distance, 0, 0), new CubicEaseOut());
                // Translation runs on the compositor without per-frame UI/layout updates.
                visual.Translation = new Vector3D(show ? 0 : distance, 0, 0);
                visual.StartAnimation(nameof(CompositionVisual.Translation), animation);
                await visual.Compositor.RequestCommitAsync().WaitAsync(token);
                await Task.Delay(animation.Duration, token);
            }
            if (!show) TaskOverlayPanel.IsVisible = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_taskPanelAnimCts, animationCts))
            {
                visual?.StopAnimation(nameof(CompositionVisual.Translation));
                _isTaskPanelAnimating = false;
                if (show && !token.IsCancellationRequested && !_shutdownStarted)
                    RebuildTaskPanelItems(_taskManager.Tasks);
            }
        }
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
                    if (_taskPanelMode == PanelMode.Auto && !_taskManager.Tasks.Any(t => t.IsActive
                        || t.State == BackgroundTaskState.Failed))
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
        TaskPanelTitle.Text = Services.Impl.LocalizationText.Get("tasks.transfers");
        var running = tasks.Count(t => t.State is BackgroundTaskState.Running or BackgroundTaskState.Pausing);
        TaskRunningCountText.Text = running > 0 ? Services.Impl.LocalizationText.Get("{0} 项进行中", running) : "";
        CancelAllTasksMenuItem.IsEnabled = tasks.Any(t => t.IsActive && t.CanCancel);
        ClearFinishedTasksMenuItem.IsEnabled = tasks.Any(t => t.IsTerminal);
        if (_fileTaskPresenter == null)
        {
            _fileTaskPresenter = new FileTaskPanelPresenter(_taskManager, _taskHistory);
            TaskItemsPanel.Children.Clear(); TaskItemsPanel.Children.Add(_fileTaskPresenter.View);
        }
        else _fileTaskPresenter.Refresh();
    }

    private void UpdateTaskPanelWidth()
    {
        TaskOverlayPanel.Width = Math.Min(560, Math.Max(280, Bounds.Width - 32));
    }

    private void ClearAllPanelTasks(object? sender, RoutedEventArgs e)
    {
        foreach (var task in _taskManager.Tasks.Where(t => t.IsActive && t.CanCancel))
            _taskManager.CancelTask(task.Id);
    }

    private void ClearCompletedPanel(object? sender, RoutedEventArgs e)
    {
        foreach (var task in _taskManager.Tasks.Where(t => t.IsTerminal)) _taskManager.RemoveTask(task.Id);
    }

    private void OnTabFileDragOver(object? sender, DragEventArgs e)
        => FileTabDragNavigation.DragOver(TabList, e,
            !IsModalInteractionBlocked && !_shutdownStarted && !GlobalSearchOverlay.IsVisible);

    private void OnTabFileDrop(object? sender, DragEventArgs e)
        => FileTabDragNavigation.Drop(e);

    private async void AddTab(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await ExecuteWindowCapabilityAsync("ui.new-tab", "{}");
    }

    private async Task AddTabAsync(bool select = true, string? directoryPath = null)
    {
        if (_vm == null)
            return;

        var sourcePath = directoryPath ?? _vm.FileList.CurrentPath;
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
            if (directoryPath != null || (!string.IsNullOrWhiteSpace(sourcePath) && Directory.Exists(sourcePath)))
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
                _vm.FileList.StatusText = MacExplorer.Services.Impl.LocalizationText.Get("无法新建标签页：{0}", ex.Message);
        }
    }

    public async Task OpenNewTabAsync(string? directoryPath = null)
    {
        var before = _vm?.Tabs.Count ?? 0;
        await AddTabAsync(directoryPath: directoryPath);
        if (_vm == null || _vm.Tabs.Count <= before)
            throw new InvalidOperationException(_vm?.FileList.StatusText ?? "无法新建标签页。");
    }

    public async Task SetPaneLayoutAsync(PaneLayout layout)
    {
        await ApplyPaneLayoutAsync(layout);
        if (_vm?.PaneLayout != layout)
            throw new InvalidOperationException(_vm?.FileList.StatusText ?? "无法切换窗格布局。");
    }

    private async void CloseTab(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: ExplorerTabViewModel tab })
            await CloseTabCoreAsync(tab);
    }

    private async Task CloseTabCoreAsync(ExplorerTabViewModel tab)
    {
        if (_vm == null || _vm.Tabs.Count <= 1 || !_vm.Tabs.Contains(tab))
            return;

        var header = TabList.ContainerFromItem(tab);
        var releaseHeader = Platforms.MacOS.MacAutomationLifetime.Capture(header, tab);
        var release = DetachWorkspaceAsync(tab, "tab-close");
        if (_vm.RemoveTab(tab) != true)
            return;
        if (header is ContentControl content) content.Content = null;
        if (header != null) header.DataContext = null;
        releaseHeader?.Invoke();
        await release;
        await Task.WhenAll(_workspaceDetachTasks.Where(pair => ReferenceEquals(pair.Key.Tab, tab))
            .Select(pair => pair.Value).ToArray());

        tab.Dispose();
        tab.FileList.Dispose();
        if (_tabScopes.Remove(tab, out var scope))
            scope?.Dispose();
        await Dispatcher.UIThread.InvokeAsync(Platforms.MacOS.MacAutomationLifetime.ReleaseDetached,
            DispatcherPriority.Background);
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
            if (selected)
                button.Focus(NavigationMethod.Pointer);
        }
    }

    private async void ChoosePaneLayout(object? sender, RoutedEventArgs e)
    {
        PaneLayoutPopup.IsOpen = false;
        if (sender is not Button { Tag: string value }
            || !Enum.TryParse<PaneLayout>(value, out var layout))
            return;

        await ExecuteWindowCapabilityAsync("ui.pane-layout",
            System.Text.Json.JsonSerializer.Serialize(new { layout = layout.ToString() }));
        e.Handled = true;
    }

    private async Task ExecuteWindowCapabilityAsync(string id, string argumentsJson)
    {
        if (_activeFileList == null) return;
        try
        {
            await App.Services.GetRequiredService<MacExplorer.Copilot.IAppCapabilityRegistry>()
                .ExecuteUiAsync(id, argumentsJson, _activeFileList);
        }
        catch (Exception ex)
        {
            _activeFileList.StatusText = ex.Message;
        }
    }

    private async Task ApplyPaneLayoutAsync(PaneLayout layout)
    {
        if (_vm == null)
            return;

        var required = MainWindowViewModel.GetPaneCount(layout);
        while (_vm.Tabs.Count < required)
        {
            var before = _vm.Tabs.Count;
            await AddTabAsync(select: false);
            if (_vm.Tabs.Count <= before) return;
        }
        _vm.SetPaneLayout(layout);
    }

    private async void OnWorkspaceActivated(ExplorerTabViewModel tab)
    {
        if (_vm == null || !_vm.VisiblePanes.Contains(tab))
            return;

        _vm.ActivatePane(tab);
        await ActivateSelectedWorkspaceAsync();
    }

    private void SchedulePaneLayoutRebuild()
    {
        if (_shutdownStarted) return;
        Interlocked.Increment(ref _paneLayoutGeneration);
        RebuildPaneLayout();
    }

    private void RebuildPaneLayout()
    {
        if (_vm == null || _shutdownStarted) return;
        var visible = _vm.VisiblePanes.Take(_vm.PaneCount).ToArray();
        var visibleSet = visible.ToHashSet();
        foreach (var (tab, workspace) in _workspaceViews)
        {
            if (!visibleSet.Contains(tab))
            {
                if (workspace.IsVisible)
                {
                    workspace.FileListView.SaveTabViewState(tab);
                    workspace.DeactivateTransientUi();
                    workspace.IsVisible = false;
                }
                // Hidden cached children still belong to Grid. Clear old placements before
                // shrinking its definitions so its cell cache cannot reference removed slots.
                Grid.SetRow(workspace, 0);
                Grid.SetColumn(workspace, 0);
                Grid.SetRowSpan(workspace, 1);
                Grid.SetColumnSpan(workspace, 1);
            }
        }

        var definition = MainWindowViewModel.GetPaneLayoutDefinition(_vm.PaneLayout);
        if (_renderedPaneLayout != _vm.PaneLayout)
        {
            _renderedPaneLayout = _vm.PaneLayout;
            PaneLayoutRoot.RowDefinitions.Clear();
            PaneLayoutRoot.ColumnDefinitions.Clear();
            for (var row = 0; row < definition.Rows; row++)
                PaneLayoutRoot.RowDefinitions.Add(new RowDefinition(new GridLength(
                    definition.RowWeights?[row] ?? 1, GridUnitType.Star)));
            for (var column = 0; column < definition.Columns; column++)
                PaneLayoutRoot.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(
                    definition.ColumnWeights?[column] ?? 1, GridUnitType.Star)));
        }

        for (var index = 0; index < Math.Min(visible.Length, definition.Slots.Count); index++)
        {
            var tab = visible[index];
            if (!_workspaceViews.TryGetValue(tab, out var workspace))
            {
                workspace = new ExplorerWorkspaceView { DataContext = tab, IsVisible = false };
                workspace.WorkspaceActivated += OnWorkspaceActivated;
                _workspaceViews.Add(tab, workspace);
                _workspaceRecency.AddLast(tab);
                PaneLayoutRoot.Children.Add(workspace);
            }
            var wasVisible = workspace.IsVisible;
            var slot = definition.Slots[index];
            workspace.ForceCompact = _vm.IsMultiPane;
            workspace.Margin = _vm.IsMultiPane ? new Thickness(2) : default;
            Grid.SetRow(workspace, slot.Row);
            Grid.SetColumn(workspace, slot.Column);
            Grid.SetRowSpan(workspace, slot.RowSpan);
            Grid.SetColumnSpan(workspace, slot.ColumnSpan);
            workspace.IsVisible = true;
            if (!wasVisible) workspace.FileListView.RestoreTabViewState(tab);
        }

        TouchSelectedWorkspace();
        foreach (var tab in _workspaceRecency.Where(tab => !visibleSet.Contains(tab))
                     .Skip(BackgroundWorkspaceLimit).ToArray())
            _ = DetachWorkspaceAsync(tab, "cache-eviction");

        _ = ActivateSelectedWorkspaceAsync();
    }

    private void TouchSelectedWorkspace()
    {
        if (_vm?.SelectedTab is not { } tab || !_workspaceViews.ContainsKey(tab)) return;
        _workspaceRecency.Remove(tab);
        _workspaceRecency.AddFirst(tab);
    }

    private async Task ActivateSelectedWorkspaceAsync()
    {
        // Let layout/render show the cached list before touching native preview resources.
        var generation = Volatile.Read(ref _paneLayoutGeneration);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (_shutdownStarted || generation != Volatile.Read(ref _paneLayoutGeneration)
            || _vm?.SelectedTab == null
            || !_workspaceViews.TryGetValue(_vm.SelectedTab, out var workspace)
            || !workspace.IsVisible || workspace.IsDetaching)
            return;

        TouchSelectedWorkspace();
        foreach (var inactive in _workspaceViews.Values.Where(candidate => !ReferenceEquals(candidate, workspace)))
            inactive.DeactivateTransientUi();

        _navigationBridge.SetActive(_vm.FileList);
        _dragDropBridge.SetActive(_vm.FileList);
        try
        {
            await _livePreviewCoordinator.ActivateAsync(workspace);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to activate workspace preview: {ex}");
        }
    }

    private Task DetachWorkspaceAsync(ExplorerTabViewModel tab, string reason)
    {
        if (!_workspaceViews.Remove(tab, out var workspace))
            return Task.CompletedTask;

        var releaseNative = Platforms.MacOS.MacAutomationLifetime.Capture(workspace);
        _workspaceRecency.Remove(tab);
        if (workspace.IsVisible) workspace.FileListView.SaveTabViewState(tab);
        workspace.MarkDetaching();
        workspace.IsVisible = false;
        PaneLayoutRoot.Children.Remove(workspace);
        releaseNative?.Invoke();
        workspace.WorkspaceActivated -= OnWorkspaceActivated;
        var task = DetachWorkspaceCoreAsync(workspace, reason);
        _workspaceDetachTasks.Add(workspace, task);
        _ = RemoveDetachTaskWhenCompleteAsync(workspace, task);
        return task;
    }

    private async Task DetachWorkspaceCoreAsync(ExplorerWorkspaceView workspace, string reason)
    {
        try
        {
            await _livePreviewCoordinator.DeactivateAndReleaseAsync(workspace);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to release workspace preview during {reason}: {ex}");
        }
        finally
        {
            workspace.DataContext = null;
            workspace.Dispose();
        }
    }

    private async Task RemoveDetachTaskWhenCompleteAsync(ExplorerWorkspaceView workspace, Task task)
    {
        try { await task; }
        finally { _workspaceDetachTasks.Remove(workspace); }
    }

    private void OpenGlobalSearch()
    {
        if (_vm?.FileList == null || IsModalInteractionBlocked)
            return;

        ActiveWorkspace?.CloseTransientUi(null);
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
        GlobalSearchBox.Text = string.Empty;
        _globalSearchSuggestions.Clear();
        GlobalSearchResults.IsVisible = false;
        GlobalSearchEmptyHint.IsVisible = true;
        CloseGlobalSearchPreview();
        GlobalSearchResultCount.Text = MacExplorer.Services.Impl.LocalizationText.Get("范围：{0}", GetGlobalSearchScopeDisplay());
        Dispatcher.UIThread.Post(() => GlobalSearchBox.Focus());
    }

    private void CloseGlobalSearch()
    {
        _globalSearchCts?.Cancel();
        _globalSearchCts?.Dispose();
        _globalSearchCts = null;
        CloseGlobalSearchPreview();
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
        CloseGlobalSearchPreview();
        _globalSearchSuggestions.Clear();
        GlobalSearchResults.SelectedIndex = -1;

        if (query.Length == 0)
        {
            GlobalSearchResults.IsVisible = false;
            GlobalSearchEmptyHint.Text = MacExplorer.Services.Impl.LocalizationText.Get("输入内容即可搜索已索引的本地文件、常用位置和最近位置");
            GlobalSearchEmptyHint.IsVisible = true;
            GlobalSearchResultCount.Text = MacExplorer.Services.Impl.LocalizationText.Get("范围：{0}", GetGlobalSearchScopeDisplay());
            return;
        }

        GlobalSearchEmptyHint.Text = MacExplorer.Services.Impl.LocalizationText.Get("正在搜索…");
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
                GlobalSearchEmptyHint.Text = MacExplorer.Services.Impl.LocalizationText.Get("没有找到匹配项");
            GlobalSearchResultCount.Text = _globalSearchSuggestions.Count == 0
                ? MacExplorer.Services.Impl.LocalizationText.Get("可换用更短的文件名或路径片段")
                : MacExplorer.Services.Impl.LocalizationText.Get("找到 {0} 项（范围：{1}）", _globalSearchSuggestions.Count, GetGlobalSearchScopeDisplay());
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
                GlobalSearchEmptyHint.Text = MacExplorer.Services.Impl.LocalizationText.Get("搜索暂时不可用");
                GlobalSearchEmptyHint.IsVisible = true;
                GlobalSearchResultCount.Text = MacExplorer.Services.Impl.LocalizationText.Get("请稍后重试");
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
            e.Handled = true;
            await ExecuteGlobalSearchInputAsync();
        }
    }

    private async Task ExecuteGlobalSearchInputAsync()
    {
        if (_vm?.FileList == null)
            return;

        var suggestion = GlobalSearchResults.SelectedItem as OmniboxSuggestion
                         ?? _globalSearchSuggestions.FirstOrDefault();
        if (suggestion != null)
        {
            await OpenGlobalSearchSuggestionAsync(suggestion);
            return;
        }

        var input = GlobalSearchBox.Text?.Trim() ?? string.Empty;
        if (!OmniboxService.IsNavigablePath(OmniboxService.NormalizePath(input)))
            return;

        CloseGlobalSearch();
        try
        {
            await OmniboxService.ExecuteInputAsync(_vm.FileList, input);
        }
        catch (Exception ex)
        {
            _vm.FileList.StatusText = MacExplorer.Services.Impl.LocalizationText.Get("打开位置失败: {0}", ex.Message);
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

        var folders = await storage.OpenAuthorizedFolderPickerAsync(new FolderPickerOpenOptions
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

    private string GetGlobalSearchScopeDisplay() => _globalSearchScopeService.Scope switch
    {
        GlobalSearchScope.CurrentFolder => "当前文件夹",
        GlobalSearchScope.UserFolder => "用户文件夹",
        GlobalSearchScope.CustomFolders => _globalSearchScopeService.CustomFolders.Count == 1
            ? $"自定义：{Path.GetFileName(_globalSearchScopeService.CustomFolders[0].TrimEnd(Path.DirectorySeparatorChar))}"
            : $"自定义位置（{_globalSearchScopeService.CustomFolders.Count} 个）",
        _ => "这台 Mac"
    };

    private void OnGlobalSearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, GlobalSearchResults))
            return;

        CloseGlobalSearchPreview();
        if (!GlobalSearchOverlay.IsVisible
            || GlobalSearchResults.SelectedItem is not OmniboxSuggestion suggestion
            || GetGlobalSearchPreviewEntry(suggestion) is not { } entry)
            return;

        _globalSearchPreview ??= new SuperPreviewView(isCompactPreview: true);
        GlobalSearchPreviewHost.Content = _globalSearchPreview;
        _ = _globalSearchPreview.OpenAsync(entry);
    }

    private static FileSystemEntry? GetGlobalSearchPreviewEntry(OmniboxSuggestion suggestion)
    {
        if (suggestion.Entry is { IsVirtual: false } entry)
            return entry;
        if (suggestion.Kind is not (OmniboxSuggestionKind.Path or OmniboxSuggestionKind.RecentDirectory))
            return null;

        var path = OmniboxService.NormalizePath(suggestion.Value);
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            return null;
        return new FileSystemEntry
        {
            FullPath = path,
            Name = suggestion.Title,
            IsDirectory = isDirectory,
            Extension = Path.GetExtension(path)
        };
    }

    private void CloseGlobalSearchPreview()
    {
        _globalSearchPreview?.Close();
    }

    private async void OnLocateGlobalSearchResult(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not OmniboxSuggestion { CanLocate: true } suggestion
            || _vm?.FileList == null)
            return;

        CloseGlobalSearch();
        try
        {
            if (suggestion.Entry is { IsVirtual: false } entry)
                await _vm.FileList.RevealFileAsync(entry);
            else
                await OmniboxService.ExecuteInputAsync(_vm.FileList, suggestion.Value);
        }
        catch (Exception ex)
        {
            _vm.FileList.StatusText = MacExplorer.Services.Impl.LocalizationText.Get("定位文件失败: {0}", ex.Message);
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
            _vm.FileList.StatusText = MacExplorer.Services.Impl.LocalizationText.Get("打开文件失败: {0}", ex.Message);
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

    internal void ShowUpdateAvailableToast(VersionInfo version)
    {
        _notifications = MacExplorer.Controls.AppToast.For(this);
        _notifications.Show(new Notification(
            $"发现新版本 {version.Version}", "点击查看更新内容并前往更新。",
            NotificationType.Information, TimeSpan.FromSeconds(15),
            onClick: () => _ = OpenSettingsAsync(version)));
    }

    internal void ShowAbout() => _ = OpenSettingsAsync(showAbout: true);

    private async Task OpenSettingsAsync(VersionInfo? availableVersion = null, bool showAbout = false)
    {
        if (_settingsDialog?.IsVisible == true)
        {
            if (showAbout) _settingsDialog.ShowAbout();
            if (availableVersion != null)
                _settingsDialog.ShowAvailableUpdate(availableVersion);
            _settingsDialog.Activate();
            return;
        }

        var dialog = new SettingsDialog
        {
            DataContext = _vm?.FileList
        };
        if (showAbout) dialog.ShowAbout();
        if (availableVersion != null)
            dialog.ShowAvailableUpdate(availableVersion);

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
