using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

/// <summary>Local pointer sorting, independent of the OS file drag session.</summary>
internal sealed class TabStripInteraction : IDisposable
{
    private readonly ListBox _list;
    private readonly ChromiumTabStripSurface _surface;
    private readonly Func<bool> _canInteract;
    private readonly Action<ExplorerTabViewModel, int> _move;
    private readonly Action<Control, ExplorerTabViewModel> _showMenu;
    private ListBoxItem? _pressed;
    private AdaptiveTabPanel? _panel;
    private IPointer? _pointer;
    private Point _start;
    private double _grabOffset;
    private object[] _snapshot = [];
    private bool _dragging;
    private TabDragSurface? _dragSurface;
    internal bool IsPointerDown => _pressed != null;

    internal TabStripInteraction(ListBox list, ChromiumTabStripSurface surface, Func<bool> canInteract,
        Action<ExplorerTabViewModel, int> move, Action<Control, ExplorerTabViewModel> showMenu)
    {
        _list = list; _surface = surface; _canInteract = canInteract; _move = move; _showMenu = showMenu;
        list.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
        list.PointerCaptureLost += CaptureLost;
        list.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel);
        list.Items.CollectionChanged += ItemsChanged;
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_canInteract()) return;
        var source = e.Source as Visual;
        var header = source as ListBoxItem ?? source?.FindAncestorOfType<ListBoxItem>();
        if (header?.DataContext is not ExplorerTabViewModel tab) return;
        var properties = e.GetCurrentPoint(_list).Properties;
        if (properties.IsRightButtonPressed)
        {
            Cancel(); e.Handled = true; _showMenu(header, tab); return;
        }
        if (!properties.IsLeftButtonPressed || source is Button || source?.FindAncestorOfType<Button>() != null) return;
        Cancel();
        _panel = _list.GetVisualDescendants().OfType<AdaptiveTabPanel>().FirstOrDefault();
        if (_panel == null) return;
        _pressed = header; _start = e.GetPosition(_panel); _grabOffset = e.GetPosition(header).X;
        _snapshot = _list.Items.Cast<object>().ToArray();
        _pointer = e.Pointer;
        _list.Focus();
        e.Pointer.Capture(_list);
        // Defer selection until click release so sorting an inactive tab does not activate it.
        e.Handled = true;
    }

    private bool IsSnapshotCurrent() => _snapshot.SequenceEqual(_list.Items.Cast<object>());

    private void Moved(object? sender, PointerEventArgs e)
    {
        if (_pressed == null || _panel == null) return;
        if (!_canInteract() || !IsSnapshotCurrent()) { Cancel(); return; }
        var position = e.GetPosition(_panel);
        if (!_dragging && Math.Abs(position.X - _start.X) <= 5) return;
        if (!_dragging)
        {
            _dragging = true;
            _pressed.SetCurrentValue(Visual.ZIndexProperty, 10);
            _dragSurface = _pressed.GetVisualDescendants().OfType<TabDragSurface>().FirstOrDefault();
            if (_dragSurface != null) _dragSurface.IsVisible = true;
            _surface.DraggedTab = _pressed;
        }
        var x = Math.Clamp(position.X - _grabOffset, 0, Math.Max(0, _panel.Bounds.Width - _pressed.Bounds.Width));
        _panel.PreviewDrag(Array.IndexOf(_snapshot, _pressed.DataContext), x);
        e.Handled = true;
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed?.DataContext is not ExplorerTabViewModel tab || e.InitialPressMouseButton != MouseButton.Left) return;
        var valid = _canInteract() && IsSnapshotCurrent();
        _panel?.UpdateLayout();
        var destination = _dragging ? _panel?.DropIndex ?? -1 : -1;
        var wasDragging = _dragging;
        Cancel();
        if (valid)
        {
            if (wasDragging && destination >= 0) _move(tab, destination);
            else if (!wasDragging) _list.SetCurrentValue(ListBox.SelectedItemProperty, tab);
        }
        e.Handled = true;
    }

    private void KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _pressed != null) { Cancel(); e.Handled = true; }
        else if ((e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
            && _canInteract() && _list.SelectedItem is ExplorerTabViewModel tab
            && _list.ContainerFromItem(tab) is Control header)
        { Cancel(); _showMenu(header, tab); e.Handled = true; }
    }

    private void CaptureLost(object? sender, PointerCaptureLostEventArgs e) => Cancel();
    private void ItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Cancel();

    internal void Cancel()
    {
        var pointer = _pointer; _pointer = null;
        if (_dragSurface != null) _dragSurface.IsVisible = false;
        _dragSurface = null;
        _surface.DraggedTab = null;
        _pressed?.ClearValue(Visual.ZIndexProperty);
        _panel?.EndDrag();
        _pressed = null; _panel = null; _snapshot = []; _dragging = false;
        if (pointer?.Captured == _list) pointer.Capture(null);
    }

    public void Dispose()
    {
        Cancel();
        _list.RemoveHandler(InputElement.PointerPressedEvent, Pressed);
        _list.RemoveHandler(InputElement.PointerMovedEvent, Moved);
        _list.RemoveHandler(InputElement.PointerReleasedEvent, Released);
        _list.PointerCaptureLost -= CaptureLost;
        _list.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
        _list.Items.CollectionChanged -= ItemsChanged;
    }
}
