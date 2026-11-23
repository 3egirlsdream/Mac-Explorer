using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Assets;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

public partial class VerticalTabBar : UserControl
{
    private const int HoverExpandDelayMs = 100;
    private const int HoverCollapseDelayMs = 220;
    private const double ExpandDurationSeconds = 0.26;
    private const double CollapseDurationSeconds = 0.23;
    internal const int MaxAnimationFps = 120;

    public static readonly StyledProperty<double> RevealOpacityProperty =
        AvaloniaProperty.Register<VerticalTabBar, double>(nameof(RevealOpacity), 1d);

    public double RevealOpacity
    {
        get => GetValue(RevealOpacityProperty);
        private set => SetValue(RevealOpacityProperty, value);
    }

    private AppWindow? _ownerWindow;
    private ILocalizationService? _localization;
    private IShortcutService? _shortcuts;
    private DispatcherTimer? _hoverEnterTimer;
    private DispatcherTimer? _hoverLeaveTimer;
    private bool _isPinned = true;
    private bool _isHoverExpanded;
    private bool _suppressHoverUntilExit;
    private double _expandedWidth = VerticalTabBarPreferences.DefaultExpandedWidth;
    private double _currentWidth = VerticalTabBarPreferences.DefaultExpandedWidth;
    private IPointer? _resizePointer;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private bool _groupClassesQueued;

    // Display-link driven expand/collapse width animation (adapts to monitor refresh rate, capped at 120fps).
    private bool _animatingWidth;
    private bool _animationFramePending;
    private long _animationStartTimestamp;
    private long _lastAppliedFrameTimestamp;
    private double _animationFromWidth;
    private double _animationTargetWidth;
    private double _animationDurationSeconds;
    private bool _animationExpanding;
    private int _targetAnimationFps = 60;
    private bool _paceAboveMaxFps;

    internal bool IsPinned => _isPinned;
    internal bool IsHoverExpanded => _isHoverExpanded;
    internal bool IsEffectivelyExpanded => _isPinned || _isHoverExpanded;
    internal bool IsAnimatingWidth => _animatingWidth;
    internal int TargetAnimationFps => _targetAnimationFps;
    internal bool IsResizing => _resizePointer != null;
    internal double ExpandedWidth => _expandedWidth;

    internal ListBox TabList => VerticalTabList;
    internal ScrollViewer ScrollViewer => TabScrollViewer;
    internal Border DropGuide => DropIndicator;
    internal AppButton GroupPresetsButton => VerticalGroupPresetsButton;
    internal AppButton PaneLayoutButton => VerticalPaneLayoutButton;
    internal AppButton TaskButton => VerticalTaskButton;
    internal AnimatedProgressRing TaskProgressRing => VerticalTaskProgressRing;
    internal AppButton CopilotStatusButton => VerticalCopilotStatusButton;

    internal Func<bool>? CanCollapseHover { get; set; }
    internal Func<bool>? CanAcceptFileDrag { get; set; }

    internal event Action? AddTabRequested;
    internal event Action<ExplorerTabViewModel>? CloseTabRequested;
    internal event Action<TabGroupViewModel>? GroupNewTabRequested;
    internal event Action<TabGroupViewModel>? GroupRenameRequested;
    internal event Action<Control>? GroupPresetsRequested;
    internal event Action<Control>? PaneLayoutRequested;
    internal event Action? TaskPanelRequested;
    internal event Action? CopilotRequested;
    internal event Action<bool>? PinStateChanged;
    internal event Action<double>? ExpandedWidthPreviewChanged;
    internal event Action<double>? ExpandedWidthCommitted;
    internal event Action<PointerPressedEventArgs>? BlankDragPressed;
    internal event Action<Control>? BlankContextMenuRequested;

    public VerticalTabBar()
    {
        InitializeComponent();

        CloseButton.Click += (_, _) =>
        {
            if (_ownerWindow is { IsModalInteractionBlocked: false } window)
                window.Close();
        };
        MinimizeButton.Click += (_, _) =>
        {
            if (_ownerWindow is { CanMinimize: true, IsModalInteractionBlocked: false } window)
                window.WindowState = WindowState.Minimized;
        };
        FullScreenButton.Click += (_, _) => _ownerWindow?.ToggleFullScreen();

        VerticalTabList.Items.CollectionChanged += OnTabItemsChanged;
        VerticalTabList.ContainerPrepared += OnTabContainerPrepared;

        PointerEntered += OnBarPointerEntered;
        PointerExited += OnBarPointerExited;

        ApplyImmediateWidth(VerticalTabBarPreferences.DefaultExpandedWidth);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ownerWindow = TopLevel.GetTopLevel(this) as AppWindow;
        if (_ownerWindow != null)
        {
            _ownerWindow.PropertyChanged += OnOwnerPropertyChanged;
            _ownerWindow.Activated += OnOwnerActivationChanged;
            _ownerWindow.Deactivated += OnOwnerActivationChanged;
            _shortcuts = _ownerWindow.Shortcuts;
            _shortcuts.Changed += UpdateShortcutHint;
        }
        _localization = App.Services?.GetService(typeof(ILocalizationService)) as ILocalizationService;
        if (_localization != null)
            _localization.LanguageChanged += UpdateLocalizedLabels;

        UpdateWindowControlState();
        UpdateLocalizedLabels();
        UpdateShortcutHint();
        QueueGroupClassRefresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelHoverTimers();
        StopWidthAnimation();
        FinishResize(commit: false);
        if (_ownerWindow != null)
        {
            _ownerWindow.PropertyChanged -= OnOwnerPropertyChanged;
            _ownerWindow.Activated -= OnOwnerActivationChanged;
            _ownerWindow.Deactivated -= OnOwnerActivationChanged;
            _ownerWindow = null;
        }
        if (_shortcuts != null)
        {
            _shortcuts.Changed -= UpdateShortcutHint;
            _shortcuts = null;
        }
        if (_localization != null)
        {
            _localization.LanguageChanged -= UpdateLocalizedLabels;
            _localization = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    internal void ApplyConfiguration(bool pinned, double expandedWidth, bool suppressHoverIfUnpinning = false, bool animate = false)
    {
        var wasPinned = _isPinned;
        _isPinned = pinned;
        _expandedWidth = VerticalTabBarPreferences.ClampWidth(expandedWidth, _ownerWindow?.Bounds.Width ?? double.PositiveInfinity);

        if (pinned)
        {
            CancelHoverTimers();
            _isHoverExpanded = false;
            _suppressHoverUntilExit = false;
            TransitionToWidth(_expandedWidth, expanding: true, animate: animate);
        }
        else
        {
            if (wasPinned && suppressHoverIfUnpinning)
            {
                CancelHoverTimers();
                _isHoverExpanded = false;
                _suppressHoverUntilExit = IsPointerOver;
            }
            var target = _isHoverExpanded ? _expandedWidth : VerticalTabBarPreferences.CollapsedWidth;
            TransitionToWidth(target, expanding: _isHoverExpanded, animate: animate);
        }
    }

    internal void ClampWidthForWindow(double windowWidth)
    {
        var clamped = VerticalTabBarPreferences.ClampWidth(_expandedWidth, windowWidth);
        if (Math.Abs(clamped - _expandedWidth) > 0.5)
        {
            _expandedWidth = clamped;
            if (IsEffectivelyExpanded)
                ApplyImmediateWidth(_expandedWidth);
        }
    }

    internal void SetHoverExpanded(bool expanded, bool animate = true)
    {
        if (_isPinned)
        {
            CancelHoverTimers();
            if (_isHoverExpanded)
            {
                _isHoverExpanded = false;
                TransitionToWidth(_expandedWidth, expanding: true, animate: false);
            }
            return;
        }

        if (!expanded && CanCollapseHover?.Invoke() == false)
            return;

        if (_isHoverExpanded == expanded && (!_animatingWidth || Math.Abs(_animationTargetWidth - (expanded ? _expandedWidth : VerticalTabBarPreferences.CollapsedWidth)) < 0.5))
            return;

        _isHoverExpanded = expanded;
        var target = expanded ? _expandedWidth : VerticalTabBarPreferences.CollapsedWidth;
        TransitionToWidth(target, expanding: expanded, animate: animate);
    }

    internal void CollapseHoverIfPointerOutside()
    {
        if (_isPinned || !_isHoverExpanded) return;
        if (!IsPointerOver && CanCollapseHover?.Invoke() != false)
            SetHoverExpanded(false, animate: true);
    }

    internal void ForceCollapseHover(bool animate = true)
    {
        CancelHoverTimers();
        _suppressHoverUntilExit = false;
        if (_isHoverExpanded || (!_isPinned && _currentWidth > VerticalTabBarPreferences.CollapsedWidth + 0.5))
        {
            _isHoverExpanded = false;
            TransitionToWidth(VerticalTabBarPreferences.CollapsedWidth, expanding: false, animate: animate);
        }
    }

    internal void FinishWidthAnimation()
    {
        if (!_animatingWidth) return;
        var target = _animationTargetWidth;
        StopWidthAnimation();
        ApplyImmediateWidth(target);
    }

    internal static double EvaluateEase(double t, bool expanding)
    {
        t = Math.Clamp(t, 0, 1);
        var power = expanding ? 4.0 : 3.5;
        return 1.0 - Math.Pow(1.0 - t, power);
    }

    internal static int ResolveAdaptiveFps(int rawDisplayFps)
        => Math.Clamp(rawDisplayFps > 0 ? rawDisplayFps : 60, 30, MaxAnimationFps);

    private void TransitionToWidth(double targetWidth, bool expanding, bool animate)
    {
        if (!animate || !IsVisible || _ownerWindow == null || MacWindowChrome.PrefersReducedMotion
            || Math.Abs(targetWidth - _currentWidth) < 0.5)
        {
            StopWidthAnimation();
            ApplyImmediateWidth(targetWidth);
            return;
        }

        var rawFps = MacWindowChrome.GetDisplayRefreshRate(_ownerWindow, maxFps: 1000);
        _targetAnimationFps = ResolveAdaptiveFps(rawFps);
        _paceAboveMaxFps = rawFps > MaxAnimationFps;

        var fullSpan = Math.Max(1, _expandedWidth - VerticalTabBarPreferences.CollapsedWidth);
        var distanceRatio = Math.Clamp(Math.Abs(targetWidth - _currentWidth) / fullSpan, 0.35, 1.0);
        var baseDuration = expanding ? ExpandDurationSeconds : CollapseDurationSeconds;

        _animationFromWidth = _currentWidth;
        _animationTargetWidth = targetWidth;
        _animationDurationSeconds = baseDuration * distanceRatio;
        _animationExpanding = expanding;
        _animationStartTimestamp = Stopwatch.GetTimestamp();
        _lastAppliedFrameTimestamp = 0;
        _animatingWidth = true;

        UpdateLayoutVisualStateForWidth(_currentWidth);
        RequestNextAnimationFrame();
    }

    private void RequestNextAnimationFrame()
    {
        if (_animationFramePending || !_animatingWidth || _ownerWindow == null)
            return;
        _animationFramePending = true;
        _ownerWindow.RequestAnimationFrame(OnWidthAnimationFrame);
    }

    private void OnWidthAnimationFrame(TimeSpan timestamp)
    {
        _animationFramePending = false;
        if (!_animatingWidth || !IsVisible || _ownerWindow == null)
        {
            StopWidthAnimation();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_animationStartTimestamp, now).TotalSeconds;
        var rawProgress = _animationDurationSeconds <= 0 ? 1.0 : Math.Clamp(elapsed / _animationDurationSeconds, 0, 1);

        // On monitors > 120 Hz (e.g. 144/240 Hz), pace layout updates to 120 fps until the final frame.
        if (rawProgress < 1.0 && _paceAboveMaxFps && _lastAppliedFrameTimestamp != 0)
        {
            var sinceLast = Stopwatch.GetElapsedTime(_lastAppliedFrameTimestamp, now).TotalSeconds;
            if (sinceLast < 0.85 / _targetAnimationFps)
            {
                RequestNextAnimationFrame();
                return;
            }
        }

        _lastAppliedFrameTimestamp = now;
        var eased = EvaluateEase(rawProgress, _animationExpanding);
        var nextWidth = _animationFromWidth + (_animationTargetWidth - _animationFromWidth) * eased;

        if (rawProgress >= 1.0)
        {
            var target = _animationTargetWidth;
            StopWidthAnimation();
            ApplyImmediateWidth(target);
            return;
        }

        ApplyImmediateWidth(nextWidth);
        RequestNextAnimationFrame();
    }

    private void StopWidthAnimation()
    {
        _animatingWidth = false;
        _animationFramePending = false;
    }

    private void ApplyImmediateWidth(double width)
    {
        _currentWidth = width;
        Width = width;
        UpdateLayoutVisualStateForWidth(width);
    }

    private void UpdateLayoutVisualStateForWidth(double currentWidth)
    {
        var collapsedWidth = VerticalTabBarPreferences.CollapsedWidth;
        var span = Math.Max(1, _expandedWidth - collapsedWidth);
        var progress = Math.Clamp((currentWidth - collapsedWidth) / span, 0, 1);

        var visuallyCollapsed = !_isPinned && !_isHoverExpanded && progress <= 0.005;
        var hoverOverlay = !_isPinned && progress > 0.005;

        RevealOpacity = visuallyCollapsed ? 0d : Math.Clamp((progress - 0.12) / 0.65, 0, 1);
        ResizeHandle.IsVisible = !visuallyCollapsed && !_animatingWidth;

        PseudoClasses.Set(":expanded", !visuallyCollapsed);
        PseudoClasses.Set(":collapsed", visuallyCollapsed);
        PseudoClasses.Set(":pinned", _isPinned);
        PseudoClasses.Set(":hover-overlay", hoverOverlay);

        UpdatePinToggleAppearance();
    }

    private void UpdatePinToggleAppearance()
    {
        PinToggleIcon.Data = _isPinned ? Icons.PanelLeftContractGeometry : Icons.PinGeometry;
        var label = LocalizationText.Get(_isPinned ? "tabs.vertical.collapse" : "tabs.vertical.pin");
        ToolTip.SetTip(PinToggleButton, label);
        AutomationProperties.SetName(PinToggleButton, label);
    }

    private void UpdateLocalizedLabels()
    {
        UpdatePinToggleAppearance();
        UpdateWindowControlState();
    }

    private void UpdateShortcutHint()
    {
        var text = _shortcuts?.GetDisplay(ShortcutIds.NewTab) ?? "";
        NewTabShortcutText.Text = text;
        NewTabShortcutText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.CanMinimizeProperty
            || e.Property == Window.CanMaximizeProperty
            || e.Property == Window.WindowStateProperty
            || e.Property == AppWindow.IsModalInteractionBlockedProperty)
        {
            UpdateWindowControlState();
        }
    }

    private void OnOwnerActivationChanged(object? sender, EventArgs e)
    {
        UpdateWindowControlState();
        if (_ownerWindow?.IsActive == false)
            ForceCollapseHover(animate: false);
    }

    private void UpdateWindowControlState()
    {
        if (_ownerWindow is not { } window) return;

        var blocked = window.IsModalInteractionBlocked;
        CloseButton.IsEnabled = !blocked;
        MinimizeButton.IsEnabled = window.CanMinimize && !blocked;
        FullScreenButton.IsEnabled = window.CanMaximize && !blocked;

        var isFullScreen = window.WindowState == WindowState.FullScreen;
        var fullScreenLabel = isFullScreen ? "退出全屏" : "进入全屏";
        ToolTip.SetTip(FullScreenButton, fullScreenLabel);
        AutomationProperties.SetName(FullScreenButton, fullScreenLabel);

        PseudoClasses.Set(":fullscreen", isFullScreen);
        PseudoClasses.Set(":inactive", !window.IsActive);
        PseudoClasses.Set(":modal-blocked", blocked);
    }

    private void OnBarPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_isPinned || !IsVisible) return;
        if (_suppressHoverUntilExit) return;

        StopTimer(ref _hoverLeaveTimer);
        if (_isHoverExpanded) return;

        // If already mid-collapse, reverse immediately so it feels responsive and elastic.
        if (_animatingWidth && _currentWidth > VerticalTabBarPreferences.CollapsedWidth + 1)
        {
            SetHoverExpanded(true, animate: true);
            return;
        }

        StopTimer(ref _hoverEnterTimer);
        _hoverEnterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverExpandDelayMs) };
        _hoverEnterTimer.Tick += (_, _) =>
        {
            StopTimer(ref _hoverEnterTimer);
            if (!_isPinned && IsVisible && IsPointerOver && !_suppressHoverUntilExit)
                SetHoverExpanded(true, animate: true);
        };
        _hoverEnterTimer.Start();
    }

    private void OnBarPointerExited(object? sender, PointerEventArgs e)
    {
        _suppressHoverUntilExit = false;
        if (_isPinned) return;

        StopTimer(ref _hoverEnterTimer);
        if (!_isHoverExpanded) return;

        StopTimer(ref _hoverLeaveTimer);
        _hoverLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverCollapseDelayMs) };
        _hoverLeaveTimer.Tick += (_, _) =>
        {
            StopTimer(ref _hoverLeaveTimer);
            if (!_isPinned && !IsPointerOver && CanCollapseHover?.Invoke() != false)
                SetHoverExpanded(false, animate: true);
        };
        _hoverLeaveTimer.Start();
    }

    private void CancelHoverTimers()
    {
        StopTimer(ref _hoverEnterTimer);
        StopTimer(ref _hoverLeaveTimer);
    }

    private static void StopTimer(ref DispatcherTimer? timer)
    {
        if (timer == null) return;
        timer.Stop();
        timer = null;
    }

    private void OnPinToggleClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var nextPinned = !_isPinned;
        PinStateChanged?.Invoke(nextPinned);
        ApplyConfiguration(nextPinned, _expandedWidth, suppressHoverIfUnpinning: !nextPinned, animate: true);
    }

    private void OnAddTabClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        AddTabRequested?.Invoke();
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: ExplorerTabViewModel tab })
            CloseTabRequested?.Invoke(tab);
    }

    private void OnGroupNewTabClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: TabGroupViewModel group })
            GroupNewTabRequested?.Invoke(group);
    }

    private void OnGroupRenameClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: TabGroupViewModel group })
            GroupRenameRequested?.Invoke(group);
    }

    private void OnGroupPresetsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        GroupPresetsRequested?.Invoke(VerticalGroupPresetsButton);
    }

    private void OnPaneLayoutClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PaneLayoutRequested?.Invoke(VerticalPaneLayoutButton);
    }

    private void OnTaskPanelClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        TaskPanelRequested?.Invoke();
    }

    private void OnCopilotClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CopilotRequested?.Invoke();
    }

    private void OnBlankAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            e.Handled = true;
            BlankContextMenuRequested?.Invoke(sender as Control ?? this);
            return;
        }
        if (properties.IsLeftButtonPressed)
            BlankDragPressed?.Invoke(e);
    }

    private void OnTabFileDragOver(object? sender, DragEventArgs e)
    {
        if (!_isPinned && !_isHoverExpanded && CanAcceptFileDrag?.Invoke() == true)
            SetHoverExpanded(true, animate: true);
        FileTabDragNavigation.DragOver(VerticalTabList, e, CanAcceptFileDrag?.Invoke() == true);
    }

    private void OnTabFileDrop(object? sender, DragEventArgs e)
        => FileTabDragNavigation.Drop(e);

    private void OnTabItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueGroupClassRefresh();

    private void OnTabContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        => QueueGroupClassRefresh();

    internal void RefreshGroupVisualClasses()
    {
        var items = VerticalTabList.Items;
        var count = items.Count;
        for (var i = 0; i < count; i++)
        {
            if (VerticalTabList.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            var item = items[i];
            if (item is ExplorerTabViewModel { Group: { } group })
            {
                var prevSame = i > 0 && items[i - 1] is ExplorerTabViewModel prev && prev.Group == group;
                var nextSame = i + 1 < count && items[i + 1] is ExplorerTabViewModel next && next.Group == group;
                container.Classes.Set("group-header", false);
                container.Classes.Set("group-member", true);
                container.Classes.Set("group-first", !prevSame);
                container.Classes.Set("group-last", !nextSame);
            }
            else
            {
                container.Classes.Set("group-header", item is TabGroupViewModel);
                container.Classes.Set("group-member", false);
                container.Classes.Set("group-first", false);
                container.Classes.Set("group-last", false);
            }
        }
    }

    private void QueueGroupClassRefresh()
    {
        if (_groupClassesQueued) return;
        _groupClassesQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _groupClassesQueued = false;
            RefreshGroupVisualClasses();
        }, DispatcherPriority.Loaded);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEffectivelyExpanded || _animatingWidth || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _resizePointer = e.Pointer;
        _resizeStartX = e.GetPosition(_ownerWindow ?? (Visual)this).X;
        _resizeStartWidth = _expandedWidth;
        e.Pointer.Capture(ResizeHandle);
        e.Handled = true;
    }

    private void OnResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_resizePointer, e.Pointer)) return;
        var currentX = e.GetPosition(_ownerWindow ?? (Visual)this).X;
        var next = VerticalTabBarPreferences.ClampWidth(
            _resizeStartWidth + (currentX - _resizeStartX),
            _ownerWindow?.Bounds.Width ?? double.PositiveInfinity);
        if (Math.Abs(next - _expandedWidth) < 0.5) return;

        _expandedWidth = next;
        ApplyImmediateWidth(_expandedWidth);
        ExpandedWidthPreviewChanged?.Invoke(_expandedWidth);
        e.Handled = true;
    }

    private void OnResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_resizePointer, e.Pointer)) return;
        FinishResize(commit: true);
        e.Handled = true;
    }

    private void OnResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => FinishResize(commit: true);

    private void FinishResize(bool commit)
    {
        var pointer = _resizePointer;
        if (pointer == null) return;
        _resizePointer = null;
        if (pointer.Captured == ResizeHandle)
            pointer.Capture(null);
        if (commit)
            ExpandedWidthCommitted?.Invoke(_expandedWidth);
        CollapseHoverIfPointerOutside();
    }
}
