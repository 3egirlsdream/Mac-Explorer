using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MacExplorer.Controls;

public sealed class WindowResizeGrip : Border
{
    public static readonly StyledProperty<WindowEdge> EdgeProperty =
        AvaloniaProperty.Register<WindowResizeGrip, WindowEdge>(nameof(Edge));

    public WindowEdge Edge
    {
        get => GetValue(EdgeProperty);
        set => SetValue(EdgeProperty, value);
    }

    public WindowResizeGrip()
    {
        Background = Brushes.Transparent;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (TopLevel.GetTopLevel(this) is not Window { CanResize: true } window) return;
        if (window is AppWindow appWindow)
            appWindow.StartResizeDrag(Edge, e);
        else
            window.BeginResizeDrag(Edge, e);
        e.Handled = true;
    }
}
