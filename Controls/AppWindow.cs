using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Controls;

public class AppWindow : Window
{
    private const double ResizeHitTestThickness = 10;
    private static readonly TimeSpan ResizeFrameInterval = TimeSpan.FromMilliseconds(16);
    private bool _isResizing;
    private WindowEdge _resizeEdge;
    private Point _resizeStartPointer;
    private Point _pendingResizePointer;
    private PixelPoint _resizeStartPosition;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeStartScaling = 1;
    private IPointer? _resizePointer;
    private IDisposable? _resizeTimer;

    public AppWindow()
    {
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 0;
        Focusable = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Opened += (_, _) => ApplyNativeWindowChrome();
        LayoutUpdated += OnAppWindowLayoutUpdated;
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnWindowPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override Type StyleKeyOverride => typeof(AppWindow);

    public void ApplyNativeWindowChrome()
    {
        MacWindowChrome.MakeTransparent(this);
        Dispatcher.UIThread.Post(() => MacWindowChrome.MakeTransparent(this), DispatcherPriority.Background);
    }

    private void OnAppWindowLayoutUpdated(object? sender, EventArgs e)
    {
        LayoutUpdated -= OnAppWindowLayoutUpdated;
        ApplyNativeWindowChrome();
    }

    public void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            PseudoClasses.Set(":maximized", WindowState is WindowState.Maximized or WindowState.FullScreen);
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanResize || WindowState != WindowState.Normal)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var edge = GetResizeEdge(e.GetPosition(this));
        if (edge is null)
            return;

        StartResizeDrag(edge.Value, e);
    }

    public void StartResizeDrag(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (!CanResize || WindowState != WindowState.Normal)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _isResizing = true;
        _resizeEdge = edge;
        _resizeStartScaling = RenderScaling <= 0 ? 1 : RenderScaling;
        _resizeStartPointer = GetPointerScreenPosition(e);
        _pendingResizePointer = _resizeStartPointer;
        _resizeStartPosition = Position;
        _resizeStartWidth = Bounds.Width;
        _resizeStartHeight = Bounds.Height;
        _resizePointer = e.Pointer;
        Cursor = GetResizeCursor(edge);
        _resizePointer.Capture(this);
        e.Handled = true;
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizing)
            return;

        var pointer = GetPointerScreenPosition(e);
        if (pointer == _pendingResizePointer)
        {
            e.Handled = true;
            return;
        }

        _pendingResizePointer = pointer;
        ScheduleResizeToPointer();
        e.Handled = true;
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizing)
            return;

        _pendingResizePointer = GetPointerScreenPosition(e);
        ApplyResizeToPointer(_pendingResizePointer);
        EndResizeDrag();
        e.Handled = true;
    }

    private void OnWindowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndResizeDrag();

    private void ScheduleResizeToPointer()
    {
        if (_resizeTimer != null)
            return;

        _resizeTimer = DispatcherTimer.RunOnce(() =>
        {
            _resizeTimer = null;
            if (_isResizing)
                ApplyResizeToPointer(_pendingResizePointer);
        }, ResizeFrameInterval, DispatcherPriority.Input);
    }

    private void ApplyResizeToPointer(Point pointer)
    {
        var deltaX = pointer.X - _resizeStartPointer.X;
        var deltaY = pointer.Y - _resizeStartPointer.Y;

        var width = _resizeStartWidth;
        var height = _resizeStartHeight;
        var positionX = _resizeStartPosition.X;
        var positionY = _resizeStartPosition.Y;

        if (IsEast(_resizeEdge))
            width = ClampDimension(_resizeStartWidth + deltaX, MinWidth, MaxWidth);

        if (IsSouth(_resizeEdge))
            height = ClampDimension(_resizeStartHeight + deltaY, MinHeight, MaxHeight);

        if (IsWest(_resizeEdge))
        {
            width = ClampDimension(_resizeStartWidth - deltaX, MinWidth, MaxWidth);
            positionX = _resizeStartPosition.X + ToPixels(_resizeStartWidth - width, _resizeStartScaling);
        }

        if (IsNorth(_resizeEdge))
        {
            height = ClampDimension(_resizeStartHeight - deltaY, MinHeight, MaxHeight);
            positionY = _resizeStartPosition.Y + ToPixels(_resizeStartHeight - height, _resizeStartScaling);
        }

        if (MacWindowChrome.TrySetWindowFrame(
                this,
                width,
                height,
                keepRightEdge: IsWest(_resizeEdge),
                keepTopEdge: IsSouth(_resizeEdge)))
            return;

        if ((IsEast(_resizeEdge) || IsWest(_resizeEdge)) && Math.Abs(width - Bounds.Width) >= 0.5)
            Width = width;

        if ((IsSouth(_resizeEdge) || IsNorth(_resizeEdge)) && Math.Abs(height - Bounds.Height) >= 0.5)
            Height = height;

        if ((IsWest(_resizeEdge) || IsNorth(_resizeEdge))
            && (positionX != Position.X || positionY != Position.Y))
            Position = new PixelPoint(positionX, positionY);
    }

    private void EndResizeDrag()
    {
        if (!_isResizing)
            return;

        _isResizing = false;
        _resizeTimer?.Dispose();
        _resizeTimer = null;
        ClearValue(CursorProperty);
        _resizePointer?.Capture(null);
        _resizePointer = null;
    }

    private Point GetPointerScreenPosition(PointerEventArgs e)
    {
        if (MacWindowChrome.TryGetPointerScreenPosition(out var pointer))
            return pointer;

        return this.PointToScreen(e.GetPosition(this)).ToPoint(_resizeStartScaling);
    }

    private static int ToPixels(double value, double scaling)
        => (int)Math.Round(value * scaling);

    private static double ClampDimension(double value, double min, double max)
    {
        var effectiveMin = Math.Max(1, min);
        var effectiveMax = double.IsInfinity(max) || double.IsNaN(max)
            ? double.PositiveInfinity
            : Math.Max(effectiveMin, max);
        return Math.Clamp(value, effectiveMin, effectiveMax);
    }

    private static bool IsWest(WindowEdge edge)
        => edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;

    private static bool IsEast(WindowEdge edge)
        => edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast;

    private static bool IsNorth(WindowEdge edge)
        => edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;

    private static bool IsSouth(WindowEdge edge)
        => edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast;

    private static Cursor GetResizeCursor(WindowEdge edge)
        => new(edge switch
        {
            WindowEdge.West or WindowEdge.East => StandardCursorType.SizeWestEast,
            WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
            _ => StandardCursorType.SizeAll
        });

    private WindowEdge? GetResizeEdge(Point point)
    {
        var nearLeft = point.X <= ResizeHitTestThickness;
        var nearRight = point.X >= Bounds.Width - ResizeHitTestThickness;
        var nearTop = point.Y <= ResizeHitTestThickness;
        var nearBottom = point.Y >= Bounds.Height - ResizeHitTestThickness;

        if (nearTop && nearLeft) return WindowEdge.NorthWest;
        if (nearTop && nearRight) return WindowEdge.NorthEast;
        if (nearBottom && nearLeft) return WindowEdge.SouthWest;
        if (nearBottom && nearRight) return WindowEdge.SouthEast;
        if (nearLeft) return WindowEdge.West;
        if (nearRight) return WindowEdge.East;
        if (nearTop) return WindowEdge.North;
        if (nearBottom) return WindowEdge.South;

        return null;
    }
}

public class DialogWindow : AppWindow
{
    public DialogWindow()
    {
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            Close();
        };
    }

    protected override Type StyleKeyOverride => typeof(AppWindow);
}

public class ToolWindow : AppWindow
{
    protected override Type StyleKeyOverride => typeof(AppWindow);
}
