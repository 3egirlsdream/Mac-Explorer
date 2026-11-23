using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

/// <summary>Pointer sorting, middle-click close, group toggle, and context menus for the vertical tab strip.</summary>
internal sealed class VerticalTabStripInteraction : IDisposable
{
    private readonly VerticalTabBar _bar;
    private readonly ListBox _list;
    private readonly Func<bool> _canInteract;
    private readonly Action<object, object?, bool, bool> _move;
    private readonly Action<Control, ExplorerTabViewModel> _showMenu;
    private readonly Action<TabGroupViewModel> _toggleGroup;
    private readonly Action<Control, TabGroupViewModel> _showGroupMenu;
    private readonly Action<ExplorerTabViewModel> _closeTab;

    private ListBoxItem? _pressed;
    private IPointer? _pointer;
    private Point _start;
    private object[] _snapshot = [];
    private bool _dragging;
    private object? _dropAnchor;
    private bool _dropAfter;
    private bool _dropUngroup;
    private bool _hasDropTarget;
    private bool _scrollQueued;

    internal bool IsPointerDown => _pressed != null;

    internal VerticalTabStripInteraction(
        VerticalTabBar bar,
        Func<bool> canInteract,
        Action<object, object?, bool, bool> move,
        Action<Control, ExplorerTabViewModel> showMenu,
        Action<TabGroupViewModel> toggleGroup,
        Action<Control, TabGroupViewModel> showGroupMenu,
        Action<ExplorerTabViewModel> closeTab)
    {
        _bar = bar;
        _list = bar.TabList;
        _canInteract = canInteract;
        _move = move;
        _showMenu = showMenu;
        _toggleGroup = toggleGroup;
        _showGroupMenu = showGroupMenu;
        _closeTab = closeTab;

        _list.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
        _list.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
        _list.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
        _list.PointerCaptureLost += CaptureLost;
        _list.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel);
        _list.Items.CollectionChanged += ItemsChanged;
        _list.SelectionChanged += SelectionChanged;
    }

    private void SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => QueueSelectionScroll();

    private void QueueSelectionScroll()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (!_bar.IsVisible || IsPointerDown || _list.SelectedItem is not { } selected
                || _list.ContainerFromItem(selected) is not { } header)
                return;
            _list.UpdateLayout();
            var scroller = _bar.ScrollViewer;
            if (header.TranslatePoint(default, scroller) is not { } point)
                return;
            var adjustment = point.Y < 0
                ? point.Y
                : Math.Max(0, point.Y + header.Bounds.Height - scroller.Viewport.Height);
            if (Math.Abs(adjustment) > 0.5)
                scroller.Offset = new Vector(0, Math.Max(0, scroller.Offset.Y + adjustment));
        }, DispatcherPriority.Background);
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_canInteract()) return;
        var source = e.Source as Visual;
        var header = source as ListBoxItem ?? source?.FindAncestorOfType<ListBoxItem>();
        if (header?.DataContext is not (ExplorerTabViewModel or TabGroupViewModel)) return;

        var properties = e.GetCurrentPoint(_list).Properties;
        if (properties.IsRightButtonPressed)
        {
            Cancel();
            e.Handled = true;
            ShowMenu(header);
            return;
        }
        if (properties.IsMiddleButtonPressed)
        {
            Cancel();
            if (header.DataContext is ExplorerTabViewModel { CanClose: true } closingTab)
            {
                e.Handled = true;
                _closeTab(closingTab);
            }
            return;
        }
        if (!properties.IsLeftButtonPressed || source is Button || source?.FindAncestorOfType<Button>() != null)
            return;

        Cancel();
        _pressed = header;
        _start = e.GetPosition(_bar.ScrollViewer);
        _snapshot = _list.Items.Cast<object>().ToArray();
        _pointer = e.Pointer;
        _list.Focus();
        e.Pointer.Capture(_list);
        // Defer activation until click release so dragging an inactive tab does not switch panes.
        e.Handled = true;
    }

    private bool IsSnapshotCurrent() => _snapshot.SequenceEqual(_list.Items.Cast<object>());

    private void Moved(object? sender, PointerEventArgs e)
    {
        if (_pressed == null) return;
        if (!_canInteract() || !IsSnapshotCurrent()) { Cancel(); return; }

        var scroller = _bar.ScrollViewer;
        var pos = e.GetPosition(scroller);
        if (!_dragging && Math.Abs(pos.Y - _start.Y) <= 5 && Math.Abs(pos.X - _start.X) <= 5)
            return;

        if (!_dragging)
        {
            _dragging = true;
            _pressed.Opacity = 0.55;
        }

        if (pos.Y < 24)
            scroller.Offset = new Vector(0, Math.Max(0, scroller.Offset.Y - 24));
        else if (pos.Y > scroller.Bounds.Height - 24)
            scroller.Offset = new Vector(0, scroller.Offset.Y + 24);

        UpdateDropTarget(e.GetPosition(_list), pos);
        e.Handled = true;
    }

    private void UpdateDropTarget(Point listPos, Point scrollerPos)
    {
        var moving = _pressed?.DataContext;
        if (moving == null || _snapshot.Length == 0)
        {
            HideDropGuide();
            return;
        }

        var outsideX = scrollerPos.X < -12 || scrollerPos.X > _bar.Bounds.Width + 12;
        var movingGroup = moving as TabGroupViewModel;
        ListBoxItem? targetContainer = null;
        object? targetItem = null;
        var after = false;
        double guideY = 0;

        for (var i = 0; i < _snapshot.Length; i++)
        {
            var candidate = _snapshot[i];
            if (_list.ContainerFromItem(candidate) is not ListBoxItem container) continue;
            if (container.TranslatePoint(default, _list) is not { } topLeft) continue;
            var mid = topLeft.Y + container.Bounds.Height / 2;

            targetContainer = container;
            targetItem = candidate;
            if (listPos.Y < mid)
            {
                after = false;
                guideY = topLeft.Y;
                break;
            }
            after = true;
            guideY = topLeft.Y + container.Bounds.Height;
        }

        if (targetContainer == null || targetItem == null)
        {
            HideDropGuide();
            return;
        }

        // Dropping just below an expanded group header targets the top of that group.
        if (moving is ExplorerTabViewModel && targetItem is TabGroupViewModel { IsCollapsed: false } expandedGroup && after)
        {
            var headerIndex = Array.IndexOf(_snapshot, expandedGroup);
            if (headerIndex >= 0 && headerIndex + 1 < _snapshot.Length
                && _snapshot[headerIndex + 1] is ExplorerTabViewModel firstMember
                && firstMember.Group == expandedGroup)
            {
                targetItem = firstMember;
                after = false;
            }
        }

        // Dropping a group onto its own member is a no-op.
        if (movingGroup != null && (ReferenceEquals(targetItem, movingGroup)
            || targetItem is ExplorerTabViewModel member && member.Group == movingGroup))
        {
            HideDropGuide();
            return;
        }

        var ungroup = outsideX;
        if (moving is ExplorerTabViewModel && !ungroup)
        {
            // Dropping before a group header or after the last member of a group when moving downwards
            // places the tab outside the group if dragging to the top/bottom outer boundary.
            if (targetItem is TabGroupViewModel && !after)
                ungroup = true;
            else if (listPos.Y < -8 || listPos.Y > _list.Bounds.Height + 8)
                ungroup = true;
        }

        _dropAnchor = targetItem;
        _dropAfter = after;
        _dropUngroup = ungroup;
        _hasDropTarget = !ReferenceEquals(targetItem, moving);

        if (!_hasDropTarget)
        {
            HideDropGuide();
            return;
        }

        var scrollerTop = _list.TranslatePoint(new Point(0, guideY), _bar.ScrollViewer)?.Y ?? guideY;
        var guide = _bar.DropGuide;
        var inset = _bar.IsEffectivelyExpanded ? 10d : 8d;
        guide.Width = Math.Max(16, _bar.Bounds.Width - inset * 2);
        Canvas.SetLeft(guide, inset);
        Canvas.SetTop(guide, Math.Clamp(scrollerTop - 1, 0, Math.Max(0, _bar.ScrollViewer.Bounds.Height - 2)));
        guide.IsVisible = true;
    }

    private void HideDropGuide()
    {
        _hasDropTarget = false;
        _bar.DropGuide.IsVisible = false;
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed?.DataContext is not { } item || e.InitialPressMouseButton != MouseButton.Left)
            return;

        var valid = _canInteract() && IsSnapshotCurrent();
        var wasDragging = _dragging;
        var hasTarget = _hasDropTarget;
        var anchor = _dropAnchor;
        var after = _dropAfter;
        var ungroup = _dropUngroup;
        Cancel();

        if (valid)
        {
            if (wasDragging && hasTarget)
                _move(item, anchor, after, ungroup);
            else if (!wasDragging && item is TabGroupViewModel group)
                _toggleGroup(group);
            else if (!wasDragging)
                _list.SetCurrentValue(ListBox.SelectedItemProperty, item);
        }
        e.Handled = true;
    }

    private void KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _pressed != null)
        {
            Cancel();
            e.Handled = true;
        }
        else if ((e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
            && _canInteract() && _list.SelectedItem is { } item
            && _list.ContainerFromItem(item) is ListBoxItem header)
        {
            Cancel();
            ShowMenu(header);
            e.Handled = true;
        }
        else if ((e.Key == Key.Enter || e.Key == Key.Space) && _canInteract()
            && _list.SelectedItem is TabGroupViewModel group)
        {
            _toggleGroup(group);
            e.Handled = true;
        }
    }

    private void ShowMenu(ListBoxItem header)
    {
        if (header.DataContext is ExplorerTabViewModel tab) _showMenu(header, tab);
        else if (header.DataContext is TabGroupViewModel group) _showGroupMenu(header, group);
    }

    private void CaptureLost(object? sender, PointerCaptureLostEventArgs e) => Cancel();

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Cancel();

    internal void Cancel()
    {
        var pointer = _pointer;
        _pointer = null;
        if (_pressed != null)
            _pressed.ClearValue(Visual.OpacityProperty);
        _pressed = null;
        _snapshot = [];
        _dragging = false;
        _dropAnchor = null;
        _dropAfter = false;
        _dropUngroup = false;
        HideDropGuide();
        if (pointer?.Captured == _list)
            pointer.Capture(null);
        _bar.CollapseHoverIfPointerOutside();
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
        _list.SelectionChanged -= SelectionChanged;
    }
}
