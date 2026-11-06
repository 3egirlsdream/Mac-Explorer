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
    private readonly Control _viewport;
    private readonly Func<bool> _canInteract;
    private readonly Action<object, object?, bool, bool> _move;
    private readonly TabGroupStripMotion _groupMotion;
    private readonly Action<Control, TabGroupViewModel> _showGroupMenu;
    private readonly Action<Control, ExplorerTabViewModel> _showMenu;
    private ListBoxItem? _pressed;
    private AdaptiveTabPanel? _panel;
    private IPointer? _pointer;
    private Point _start;
    private double _grabOffset;
    private object[] _snapshot = [];
    private bool _dragging;
    private int _outsideEdge;
    private bool _releaseQueued;
    private bool _scrollQueued;
    private ScrollViewer? _scroller;
    private TabDragSurface? _dragSurface;
    private ListBoxItem[] _draggedHeaders = [];
    internal bool IsPointerDown => _pressed != null;
    internal TabGroupStripMotion GroupMotion => _groupMotion;

    internal TabStripInteraction(ListBox list, Control viewport, Grid content, Button newTabButton, ChromiumTabStripSurface surface, Func<bool> canInteract,
        Action<object, object?, bool, bool> move, Action<Control, ExplorerTabViewModel> showMenu,
        Action<TabGroupViewModel> toggleGroup, Action<Control, TabGroupViewModel> showGroupMenu)
    {
        _list = list; _surface = surface; _canInteract = canInteract; _move = move; _showMenu = showMenu;
        _groupMotion = new TabGroupStripMotion(list, content, newTabButton, surface, toggleGroup);
        _viewport = viewport;
        _viewport.SizeChanged += ViewportChanged;
        _showGroupMenu = showGroupMenu;
        list.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
        list.PointerCaptureLost += CaptureLost;
        list.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel);
        list.Items.CollectionChanged += ItemsChanged;
        list.SelectionChanged += SelectionChanged;
        list.AttachedToVisualTree += Attached;
        list.DetachedFromVisualTree += Detached;
    }

    private void Attached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _scroller = _list.FindAncestorOfType<ScrollViewer>();
        if (_scroller != null) _scroller.ScrollChanged += Scrolled;
        QueueSelectionScroll();
    }

    private void Detached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_scroller != null) _scroller.ScrollChanged -= Scrolled;
        _scroller = null;
    }

    private void ViewportChanged(object? sender, SizeChangedEventArgs e)
    {
        _groupMotion.OnStripChanged();
        QueueSelectionScroll();
    }

    private void Scrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.X != 0) _groupMotion.OnStripChanged();
    }

    private void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _groupMotion.OnStripChanged();
        QueueSelectionScroll();
    }

    private void QueueSelectionScroll()
    {
        if (_scrollQueued || _groupMotion.IsRunning) return;
        _scrollQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_scroller == null || IsPointerDown || _groupMotion.IsRunning || _list.SelectedItem is not { } selected
                || _list.ContainerFromItem(selected) is not { } header) return;
            _list.UpdateLayout();
            if (header.TranslatePoint(default, _scroller) is not { } point) return;
            var adjustment = point.X < 0 ? point.X
                : Math.Max(0, point.X + header.Bounds.Width - _scroller.Viewport.Width);
            if (Math.Abs(adjustment) > 0.5)
                _scroller.Offset = new Vector(Math.Max(0, _scroller.Offset.X + adjustment), 0);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_canInteract()) return;
        var source = e.Source as Visual;
        var header = source as ListBoxItem ?? source?.FindAncestorOfType<ListBoxItem>();
        if (header?.DataContext is not (ExplorerTabViewModel or TabGroupViewModel)) return;
        if (_groupMotion.IsRunning && header.DataContext is not TabGroupViewModel)
        {
            e.Handled = true;
            return;
        }
        var properties = e.GetCurrentPoint(_list).Properties;
        if (properties.IsRightButtonPressed)
        {
            Cancel(); e.Handled = true; ShowMenu(header); return;
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
        if (!_dragging && Math.Abs(position.X - _start.X) <= 5 && Math.Abs(position.Y - _start.Y) <= 5) return;
        if (!_dragging)
        {
            if (_groupMotion.IsRunning)
            {
                _groupMotion.Finish();
                if (_pressed == null || _panel == null) return;
                _list.UpdateLayout();
                position = e.GetPosition(_panel);
            }
            _dragging = true;
            var group = _pressed.DataContext as TabGroupViewModel;
            _draggedHeaders = _snapshot.Where(item => item == _pressed.DataContext || group != null
                && item is ExplorerTabViewModel tab && tab.Group == group)
                .Select(item => _list.ContainerFromItem(item)).OfType<ListBoxItem>().ToArray();
            foreach (var header in _draggedHeaders) header.SetCurrentValue(Visual.ZIndexProperty, 10);
            _dragSurface = _pressed.GetVisualDescendants().OfType<TabDragSurface>().FirstOrDefault();
            if (_dragSurface != null) _dragSurface.IsVisible = true;
            _surface.DraggedTab = _pressed;
        }
        _outsideEdge = position.X < 0 ? -1 : position.X > _panel.Bounds.Width ? 1 : 0;
        var x = Math.Clamp(position.X - _grabOffset, 0,
            Math.Max(0, _panel.Bounds.Width - _draggedHeaders.Sum(header => header.Bounds.Width)));
        if (_list.FindAncestorOfType<ScrollViewer>() is { } scroller)
        {
            var relative = e.GetPosition(scroller).X;
            if (relative < 24) scroller.Offset = new Vector(Math.Max(0, scroller.Offset.X - 24), 0);
            else if (relative > scroller.Bounds.Width - 24) scroller.Offset = new Vector(scroller.Offset.X + 24, 0);
        }
        _panel.PreviewDrag(Array.IndexOf(_snapshot, _pressed.DataContext), x, position.X,
            _outsideEdge != 0 || position.Y < -8 || position.Y > _panel.Bounds.Height + 12);
        e.Handled = true;
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed?.DataContext is not { } item || e.InitialPressMouseButton != MouseButton.Left) return;
        var valid = _canInteract() && IsSnapshotCurrent();
        _panel?.UpdateLayout();
        var destination = _dragging ? _panel?.DropIndex ?? -1 : -1;
        var anchor = _outsideEdge == 0 ? _panel?.DropAnchor : null;
        var after = _outsideEdge == 0 ? _panel?.DropAfter ?? false : _outsideEdge > 0;
        var ungroup = _outsideEdge != 0 || (_panel?.DropUngroup ?? false);
        var wasDragging = _dragging;
        Cancel();
        if (valid)
        {
            if (wasDragging && destination >= 0) _move(item, anchor, after, ungroup);
            else if (!wasDragging && item is TabGroupViewModel group) _groupMotion.Toggle(group);
            else if (!wasDragging) _list.SetCurrentValue(ListBox.SelectedItemProperty, item);
        }
        e.Handled = true;
    }

    private void KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _pressed != null) { Cancel(); e.Handled = true; }
        else if ((e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
            && _canInteract() && _list.SelectedItem is { } item
            && _list.ContainerFromItem(item) is ListBoxItem header)
        { Cancel(); ShowMenu(header); e.Handled = true; }
        else if ((e.Key == Key.Enter || e.Key == Key.Space) && _canInteract()
            && _list.SelectedItem is TabGroupViewModel group)
        { _groupMotion.Toggle(group); e.Handled = true; }
    }

    private void ShowMenu(ListBoxItem header)
    {
        if (header.DataContext is ExplorerTabViewModel tab) _showMenu(header, tab);
        else if (header.DataContext is TabGroupViewModel group) _showGroupMenu(header, group);
    }

    private void CaptureLost(object? sender, PointerCaptureLostEventArgs e) => Cancel();
    private void ItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _groupMotion.OnStripChanged();
        Cancel();
        if (_releaseQueued) return;
        _releaseQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _releaseQueued = false;
            Platforms.MacOS.MacAutomationLifetime.ReleaseDetached();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    internal void FinishGroupMotion() => _groupMotion.Finish();

    internal void Cancel()
    {
        var pointer = _pointer; _pointer = null;
        if (_dragSurface != null) _dragSurface.IsVisible = false;
        _dragSurface = null;
        _surface.DraggedTab = null;
        foreach (var header in _draggedHeaders) header.ClearValue(Visual.ZIndexProperty);
        _draggedHeaders = [];
        _panel?.EndDrag();
        _pressed = null; _panel = null; _snapshot = []; _dragging = false; _outsideEdge = 0;
        if (pointer?.Captured == _list) pointer.Capture(null);
    }

    public void Dispose()
    {
        _groupMotion.Dispose();
        _viewport.SizeChanged -= ViewportChanged;
        Cancel();
        _list.RemoveHandler(InputElement.PointerPressedEvent, Pressed);
        _list.RemoveHandler(InputElement.PointerMovedEvent, Moved);
        _list.RemoveHandler(InputElement.PointerReleasedEvent, Released);
        _list.PointerCaptureLost -= CaptureLost;
        _list.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
        _list.Items.CollectionChanged -= ItemsChanged;
        _list.SelectionChanged -= SelectionChanged;
        _list.AttachedToVisualTree -= Attached;
        _list.DetachedFromVisualTree -= Detached;
        if (_scroller != null) _scroller.ScrollChanged -= Scrolled;
        _scroller = null;
    }
}
