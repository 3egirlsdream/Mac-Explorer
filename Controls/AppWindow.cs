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
    private bool _isResizing;
    private bool _resizeFramePending;
    private WindowEdge _resizeEdge;
    private PixelPoint _resizeStartPointer;
    private PixelPoint _pendingResizePointer;
    private PixelPoint _resizeStartPosition;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeStartScaling = 1;
    private IPointer? _resizePointer;

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
        _resizeStartPointer = GetPointerScreenPosition(e, _resizeStartScaling);
        _pendingResizePointer = _resizeStartPointer;
        _resizeStartPosition = Position;
        _resizeStartWidth = Bounds.Width;
        _resizeStartHeight = Bounds.Height;
        _resizePointer = e.Pointer;
        _resizePointer.Capture(this);
        e.Handled = true;
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizing)
            return;

        _pendingResizePointer = GetPointerScreenPosition(e, _resizeStartScaling);
        ScheduleResizeToPointer();
        e.Handled = true;
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizing)
            return;

        _pendingResizePointer = GetPointerScreenPosition(e, _resizeStartScaling);
        ApplyResizeToPointer(_pendingResizePointer);
        EndResizeDrag();
        e.Handled = true;
    }

    private void OnWindowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndResizeDrag();

    private void ScheduleResizeToPointer()
    {
        if (_resizeFramePending)
            return;

        _resizeFramePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _resizeFramePending = false;
            if (_isResizing)
                ApplyResizeToPointer(_pendingResizePointer);
        }, DispatcherPriority.Input);
    }

    private void ApplyResizeToPointer(PixelPoint pointer)
    {
        var deltaX = (pointer.X - _resizeStartPointer.X) / _resizeStartScaling;
        var deltaY = (pointer.Y - _resizeStartPointer.Y) / _resizeStartScaling;

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

        Width = width;
        Height = height;

        if (positionX != Position.X || positionY != Position.Y)
            Position = new PixelPoint(positionX, positionY);
    }

    private void EndResizeDrag()
    {
        if (!_isResizing)
            return;

        _isResizing = false;
        _resizePointer?.Capture(null);
        _resizePointer = null;
    }

    private PixelPoint GetPointerScreenPosition(PointerEventArgs e, double scaling)
    {
        var local = e.GetPosition(this);
        return new PixelPoint(
            Position.X + ToPixels(local.X, scaling),
            Position.Y + ToPixels(local.Y, scaling));
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
