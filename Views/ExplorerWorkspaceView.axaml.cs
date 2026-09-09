using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class ExplorerWorkspaceView : UserControl, ILivePreviewWorkspace, IDisposable
{
    public static readonly StyledProperty<bool> ForceCompactProperty =
        AvaloniaProperty.Register<ExplorerWorkspaceView, bool>(nameof(ForceCompact));

    public static readonly DirectProperty<ExplorerWorkspaceView, bool> IsCompactProperty =
        AvaloniaProperty.RegisterDirect<ExplorerWorkspaceView, bool>(
            nameof(IsCompact),
            view => view.IsCompact);

    public static readonly StyledProperty<bool> IsLivePreviewEnabledProperty =
        AvaloniaProperty.Register<ExplorerWorkspaceView, bool>(nameof(IsLivePreviewEnabled));

    private ExplorerTabViewModel? _tab;
    private bool _isCompact;
    private bool _isPageSearchExpanded;
    private bool _isRestoringSearch;
    private bool _resizingPreview;
    private double _normalPreviewWidth = 380;
    private double _pendingPreviewWidth;
    private bool _previewResizeFramePending;
    private bool _disposed;
    private long _activationGeneration;

    public event Action<ExplorerTabViewModel>? WorkspaceActivated;

    public ExplorerWorkspaceView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnWorkspacePointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnWorkspaceGotFocus,
            RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += OnWorkspaceSizeChanged;
        InfoPanelControl.PreviewExpandedChanged += OnPreviewExpandedChanged;
    }

    public ExplorerTabViewModel? Tab => DataContext as ExplorerTabViewModel;
    public FileListView FileListView => FileListControl;
    public bool IsDetaching { get; private set; }
    internal bool IsDisposed => _disposed;

    public bool ForceCompact
    {
        get => GetValue(ForceCompactProperty);
        set => SetValue(ForceCompactProperty, value);
    }

    public bool IsCompact
    {
        get => _isCompact;
        private set => SetAndRaise(IsCompactProperty, ref _isCompact, value);
    }

    public bool IsLivePreviewEnabled
    {
        get => GetValue(IsLivePreviewEnabledProperty);
        private set => SetValue(IsLivePreviewEnabledProperty, value);
    }

    bool ILivePreviewWorkspace.CanActivateLivePreview
        => !_disposed && !IsDetaching && VisualRoot != null && Tab != null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ForceCompactProperty)
        {
            PseudoClasses.Set(":split", ForceCompact);
            ApplyResponsiveLayout(Bounds.Width);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_tab != null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab.FileList.PropertyChanged -= OnFileListPropertyChanged;
        }

        base.OnDataContextChanged(e);
        _tab = Tab;
        if (_tab != null)
        {
            _tab.PropertyChanged += OnTabPropertyChanged;
            _tab.FileList.PropertyChanged += OnFileListPropertyChanged;
            _normalPreviewWidth = _tab.InfoPanelWidth;
            InfoPanelControl.RestorePreviewExpanded(_tab.IsPreviewExpanded);
        }

        RefreshState();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerTabViewModel.IsActive))
            UpdateActiveState();
    }

    private void OnFileListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileListViewModel.IsHomePage)
            or nameof(FileListViewModel.IsAiView)
            or nameof(FileListViewModel.AiViewMode))
        {
            UpdateContentVisibility();
            UpdateInfoPanelVisibility();
        }
        else if (e.PropertyName is nameof(FileListViewModel.IsPreviewPaneVisible)
                 or nameof(FileListViewModel.IsMetadataPanelVisible)
                 or nameof(FileListViewModel.IsInfoPanelVisible))
        {
            UpdateInfoPanelVisibility();
        }
    }

    public void RefreshState()
    {
        UpdateActiveState();
        UpdateContentVisibility();
        UpdateInfoPanelVisibility();
    }

    private void UpdateActiveState()
        => WorkspaceSurface.Classes.Set("active", _tab?.IsActive == true);

    private void UpdateContentVisibility()
    {
        var fileList = _tab?.FileList;
        var showAiSearch = fileList?.IsAiView == true && fileList.AiViewMode == AiViewMode.TextSearch;
        FileListControl.IsVisible = fileList != null && !fileList.IsHomePage && !showAiSearch;
        HomeViewControl.IsVisible = fileList?.IsHomePage == true;
        AiViewControl.IsVisible = showAiSearch;
        UpdatePageSearchPresentation();
    }

    private void UpdateInfoPanelVisibility()
    {
        var fileList = _tab?.FileList;
        var canShow = fileList != null && !fileList.IsHomePage && !fileList.IsAiView;
        InfoDrawer.IsPaneOpen = canShow && fileList!.IsInfoPanelVisible;
    }

    private void OnWorkspacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ActivateWorkspace();
        if (IsCompact && SidebarSplitView.IsPaneOpen
            && !IsInsideVisual(e.Source as Visual, SidebarSurface)
            && !IsInsideVisual(e.Source as Visual, SidebarToggleButton))
        {
            SetCompactSidebarOpen(false);
        }
    }

    private void OnWorkspaceGotFocus(object? sender, RoutedEventArgs e)
        => ActivateWorkspace();

    private void OnInactivePreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        ActivateWorkspace();
        e.Handled = true;
    }

    private void ActivateWorkspace()
    {
        if (!IsDetaching && _tab != null && !_tab.IsActive)
            WorkspaceActivated?.Invoke(_tab);
    }

    public void FocusPathInput()
    {
        if (_tab?.FileList.IsHomePage == true)
            HomeViewControl.FocusOmnibox();
        else
            BreadcrumbControl.FocusPathInput();
    }

    public void ToggleSidebar()
    {
        if (!IsCompact)
        {
            SidebarSplitView.IsPaneOpen = true;
            SidebarControl.SetRailMode(false);
            return;
        }

        SetCompactSidebarOpen(!SidebarSplitView.IsPaneOpen);
    }

    private void OnToggleSidebar(object? sender, RoutedEventArgs e)
    {
        ToggleSidebar();
        e.Handled = true;
    }

    private void SetCompactSidebarOpen(bool open)
    {
        SidebarSplitView.IsPaneOpen = open;
        SidebarControl.SetRailMode(!open);
        ToolTip.SetTip(SidebarToggleButton, open ? "收起侧栏" : "展开侧栏");
    }

    public void TogglePageSearch()
    {
        if (_tab?.FileList.IsHomePage == true)
        {
            HomeViewControl.FocusOmnibox();
            return;
        }
        if (!IsCompact)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }

        _isPageSearchExpanded = !_isPageSearchExpanded;
        UpdatePageSearchPresentation();
        if (_isPageSearchExpanded)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    private void OnTogglePageSearch(object? sender, RoutedEventArgs e)
    {
        TogglePageSearch();
        e.Handled = true;
    }

    private void UpdatePageSearchPresentation()
    {
        var isHome = _tab?.FileList.IsHomePage == true;
        var expanded = !isHome && IsCompact && _isPageSearchExpanded;
        Grid.SetColumn(SearchControls, expanded ? 1 : 2);
        Grid.SetColumnSpan(SearchControls, expanded ? 2 : 1);
        SearchHost.Classes.Set("expanded", expanded);
        SearchToggleButton.IsVisible = !isHome && IsCompact && !_isPageSearchExpanded;
        SearchHost.IsVisible = !isHome && (!IsCompact || _isPageSearchExpanded);
        BreadcrumbControl.IsVisible = isHome || !IsCompact || !_isPageSearchExpanded;
    }

    public void ToggleToolbarMenu(ToolbarMenuKind kind) => ToolbarControl.ToggleMenu(kind);

    public void ToggleInfoPanel()
    {
        if (_tab?.FileList == null)
            return;
        _tab.FileList.IsInfoPanelVisible = !_tab.FileList.IsInfoPanelVisible;
    }

    public void CloseTransientUi(object? source)
    {
        FileListControl.DismissContextMenu();
        ToolbarControl.CloseDropdownsFromPointerSource(source);
        BreadcrumbControl.CloseTransientUiFromPointerSource(source);
    }

    public void DeactivateTransientUi()
    {
        CloseTransientUi(null);
        if (IsCompact)
            SetCompactSidebarOpen(false);
    }

    public bool TryHandleEscape()
    {
        if (FileListControl.TryDismissContextMenu())
            return true;
        if (BreadcrumbControl.TryCloseTransientUi())
        {
            FileListControl.Focus();
            return true;
        }
        if (ToolbarControl.TryCloseDropdown())
            return true;
        if (_isPageSearchExpanded)
        {
            _isPageSearchExpanded = false;
            UpdatePageSearchPresentation();
            FileListControl.Focus();
            return true;
        }
        if (IsCompact && SidebarSplitView.IsPaneOpen)
        {
            SetCompactSidebarOpen(false);
            SidebarToggleButton.Focus();
            return true;
        }
        if (IsCompact && InfoDrawer.IsPaneOpen && _tab?.FileList != null)
        {
            _tab.FileList.IsInfoPanelVisible = false;
            FileListControl.Focus();
            return true;
        }
        return false;
    }

    public bool TryHandleFileShortcut(KeyEventArgs e)
        => FileListControl.IsVisible && FileListControl.TryHandleFileShortcut(e);

    public void MarkDetaching()
    {
        IsDetaching = true;
        CloseTransientUi(null);
        if (IsCompact)
            SetCompactSidebarOpen(false);
    }

    async Task ILivePreviewWorkspace.SetLivePreviewStateAsync(bool enabled, long activationGeneration)
        => await SetLivePreviewStateAsync(enabled, activationGeneration);

    internal async Task SetLivePreviewStateAsync(bool enabled, long activationGeneration)
    {
        _activationGeneration = activationGeneration;
        IsLivePreviewEnabled = enabled;
        InactivePreviewPlaceholder.IsVisible = !enabled;
        await InfoPanelControl.SetLivePreviewStateAsync(enabled, activationGeneration);
    }

    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
        if (_tab?.IsPreviewExpanded == true && InfoDrawer.Bounds.Width > 0)
            InfoDrawer.OpenPaneLength = GetInfoPanelMaxWidth();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Choose the initial mode before measuring the sidebar/template. Bounds
        // is still zero here for a new tab and is not a compact window size.
        if (double.IsFinite(availableSize.Width))
            ApplyResponsiveLayout(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0 && !ForceCompact)
            return;

        var layout = ResponsiveWorkspaceLayout.Resolve(width, ForceCompact);
        if (IsCompact != layout.IsCompact)
        {
            if (layout.IsCompact)
            {
                SidebarSplitView.IsPaneOpen = false;
                SidebarSplitView.DisplayMode = layout.SidebarDisplayMode;
            }
            else
            {
                SidebarSplitView.IsPaneOpen = false;
                SidebarSplitView.DisplayMode = layout.SidebarDisplayMode;
                SidebarSplitView.IsPaneOpen = true;
            }

            IsCompact = layout.IsCompact;
            PseudoClasses.Set(":compact", IsCompact);
            ToolbarControl.IsCompact = IsCompact;
            SidebarToggleButton.IsVisible = IsCompact;
            SidebarControl.SetRailMode(IsCompact && !SidebarSplitView.IsPaneOpen);
            _isPageSearchExpanded = false;
            UpdatePageSearchPresentation();
        }

        SidebarSplitView.DisplayMode = layout.SidebarDisplayMode;
        SidebarSplitView.OpenPaneLength = layout.SidebarOpenPaneLength;
        SidebarSplitView.CompactPaneLength = layout.SidebarCompactPaneLength;
        InfoDrawer.DisplayMode = layout.InfoPanelDisplayMode;
        if (Bounds.Width > 0)
            InfoDrawer.OpenPaneLength = ClampInfoPanelWidth(
                _tab?.InfoPanelWidth ?? InfoDrawer.OpenPaneLength);
    }

    private double GetInfoPanelMaxWidth()
    {
        var availableWidth = InfoDrawer.Bounds.Width > 0 ? InfoDrawer.Bounds.Width : Bounds.Width;
        return IsCompact
            ? Math.Max(0, availableWidth - 48)
            : Math.Max(0, availableWidth * 0.5);
    }

    private double ClampInfoPanelWidth(double requestedWidth)
    {
        var maximum = GetInfoPanelMaxWidth();
        var minimum = Math.Min(280, maximum);
        return Math.Clamp(requestedWidth, minimum, maximum);
    }

    private void OnInfoPanelResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_tab?.IsPreviewExpanded == true)
            return;
        if (sender is not Control handle || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;
        _resizingPreview = true;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnInfoPanelResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingPreview || sender is not Control handle || e.Pointer.Captured != handle)
            return;
        _pendingPreviewWidth = ClampInfoPanelWidth(
            InfoDrawer.Bounds.Width - e.GetPosition(InfoDrawer).X);
        if (!_previewResizeFramePending)
        {
            _previewResizeFramePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _previewResizeFramePending = false;
                if (!_resizingPreview)
                    return;
                InfoDrawer.OpenPaneLength = _pendingPreviewWidth;
                if (_tab != null)
                    _tab.InfoPanelWidth = _pendingPreviewWidth;
            }, DispatcherPriority.Render);
        }
        e.Handled = true;
    }

    private void OnInfoPanelResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizingPreview)
            return;
        _resizingPreview = false;
        e.Pointer.Capture(null);
        if (_tab != null)
            _tab.InfoPanelWidth = InfoDrawer.OpenPaneLength;
        e.Handled = true;
    }

    private void OnPreviewExpandedChanged(object? sender, bool expanded)
    {
        if (_tab == null)
            return;
        if (expanded)
            _normalPreviewWidth = _tab.InfoPanelWidth;
        _tab.IsPreviewExpanded = expanded;
        InfoPanelResizeHandle.IsVisible = !expanded;
        InfoPanelPane.ColumnDefinitions[0].Width = new GridLength(expanded ? 0 : 6);
        InfoPanelControl.SetExpandedChrome(expanded);
        InfoDrawer.OpenPaneLength = expanded
            ? GetInfoPanelMaxWidth()
            : ClampInfoPanelWidth(_normalPreviewWidth);
        if (!expanded)
            _tab.InfoPanelWidth = InfoDrawer.OpenPaneLength;
        InfoPanelControl.CompletePreviewTransition(expanded);
    }

    private async void NavigateBack(object? sender, RoutedEventArgs e)
    {
        if (_tab?.FileList.CanGoBack == true)
            await _tab.FileList.NavigateBackAsync();
    }

    private async void NavigateForward(object? sender, RoutedEventArgs e)
    {
        if (_tab?.FileList.CanGoForward == true)
            await _tab.FileList.NavigateForwardAsync();
    }

    private async void NavigateUp(object? sender, RoutedEventArgs e)
    {
        if (_tab?.FileList != null)
            await _tab.FileList.NavigateUpAsync();
    }

    private async void RefreshView(object? sender, RoutedEventArgs e)
    {
        if (_tab?.FileList != null)
            await _tab.FileList.RefreshCommand.ExecuteAsync(null);
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tab?.FileList == null)
            return;
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            await _tab.FileList.SearchCommand.ExecuteAsync(SearchBox.Text);
            SearchClearButton.IsVisible = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = string.Empty;
            await RestoreSearchOriginAsync();
            if (IsCompact)
            {
                _isPageSearchExpanded = false;
                UpdatePageSearchPresentation();
                FileListControl.Focus();
            }
            e.Handled = true;
        }
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        SearchClearButton.IsVisible = hasQuery;
        if (!hasQuery && _tab?.FileList.IsSearchMode == true)
            await RestoreSearchOriginAsync();
    }

    private async void ClearSearch(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchClearButton.IsVisible = false;
        await RestoreSearchOriginAsync();
    }

    private async Task RestoreSearchOriginAsync()
    {
        if (_isRestoringSearch || _tab?.FileList.IsSearchMode != true)
            return;
        _isRestoringSearch = true;
        try
        {
            await _tab.FileList.ExitSearchAsync();
        }
        finally
        {
            _isRestoringSearch = false;
        }
    }

    private static bool IsInsideVisual(Visual? visual, Visual? target)
    {
        if (target == null)
            return false;
        for (; visual != null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, target))
                return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SizeChanged -= OnWorkspaceSizeChanged;
        InfoPanelControl.PreviewExpandedChanged -= OnPreviewExpandedChanged;
        if (_tab != null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab.FileList.PropertyChanged -= OnFileListPropertyChanged;
        }
        ToolbarControl.CloseDropdowns();
        BreadcrumbControl.CloseTransientUi();
        FileListControl.DismissContextMenu();
        _tab = null;
    }
}
