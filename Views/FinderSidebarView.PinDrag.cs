using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views;

public partial class FinderSidebarView
{
    private IPointer? _pinPointer;
    private PinnedFolder? _pressedPin;
    private Point _pinPressPosition;
    private Point _pinPosition;
    private bool _draggingPin;
    private PinDrop? _preview;
    private DispatcherTimer? _pinScrollTimer;

    private void OnPinnedFolderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: PinnedFolder pin } row || IsRailMode || _pinPointer != null
            || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        row.Focus();
        _pressedPin = pin;
        _pinPointer = e.Pointer;
        _pinPosition = _pinPressPosition = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pinPointer != e.Pointer) return;
        _pinPosition = e.GetPosition(this);
        if (!_draggingPin)
        {
            var threshold = Application.Current?.TryGetFeature<IPlatformSettings>()?.GetTapSize(e.Pointer.Type) ?? new Size(4, 4);
            var delta = _pinPosition - _pinPressPosition;
            if (Math.Abs(delta.X) <= threshold.Width && Math.Abs(delta.Y) <= threshold.Height) return;
            _draggingPin = true;
            // The row can be replaced during a refresh. Keep Escape routed to the drag owner.
            Focus(NavigationMethod.Pointer);
        }
        UpdatePinDropLine();
        if (PinScrollStep() != 0)
        {
            if (_pinScrollTimer == null)
            {
                _pinScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
                _pinScrollTimer.Tick += ScrollDuringPinDrag;
            }
            _pinScrollTimer.Start();
        }
        else _pinScrollTimer?.Stop();
        e.Handled = true;
    }

    private double PinScrollStep()
    {
        var bounds = SidebarScroll.Bounds;
        if (!bounds.Contains(_pinPosition)) return 0;
        if (_pinPosition.Y < bounds.Y + 24 && SidebarScroll.Offset.Y > 0) return -12;
        if (_pinPosition.Y > bounds.Bottom - 24 && SidebarScroll.Offset.Y < SidebarScroll.ScrollBarMaximum.Y) return 12;
        return 0;
    }

    private void ScrollDuringPinDrag(object? sender, EventArgs e)
    {
        var step = PinScrollStep();
        if (step == 0)
        {
            _pinScrollTimer?.Stop();
            return;
        }
        SidebarScroll.Offset += new Vector(0, step);
        SidebarScroll.UpdateLayout();
        UpdatePinDropLine();
    }

    private readonly record struct PinDrop(int GroupId, int? AnchorId, bool After, Control Guide);

    private PinDrop? FindPinDrop(Point position)
    {
        if (_pressedPin is not { } source || ViewModel == null || !SidebarScroll.Bounds.Contains(position)
            || this.InputHitTest(position) is not Visual hit) return null;

        PinDrop? drop = null;
        for (Visual? current = hit; current != null && current != this; current = current.GetVisualParent())
        {
            if (current is Border { DataContext: PinnedFolder pin } row && row.Classes.Contains("sidebar-item"))
            {
                if (pin.Id == source.Id) return null;
                var local = this.TranslatePoint(position, row);
                drop = new(pin.GroupId, pin.Id, local?.Y >= row.Bounds.Height / 2, row);
                break;
            }
            if (current is Control target && GetPinDropGroup(target) is int groupId)
            {
                drop = new(groupId, null, true, target);
                break;
            }
        }
        if (drop is { } candidate && source.GroupId != candidate.GroupId &&
            ViewModel.AllPinnedFolders.Any(pin => pin.GroupId == candidate.GroupId && pin.FolderPath == source.FolderPath))
            return null;
        return drop;
    }

    private void UpdatePinDropLine()
    {
        _preview = FindPinDrop(_pinPosition);
        PinDropLine.IsVisible = _preview.HasValue;
        if (_preview is not { } target) return;
        var guide = target.Guide;
        var edge = guide.TranslatePoint(new Point(0, target.After ? guide.Bounds.Height : 0), PinDropOverlay)!.Value;
        Canvas.SetLeft(PinDropLine, edge.X);
        Canvas.SetTop(PinDropLine, edge.Y - 1);
        PinDropLine.Width = guide.Bounds.Width;
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pinPointer != e.Pointer || _pressedPin is not { } pin ||
            e.InitialPressMouseButton != MouseButton.Left) return;
        var position = e.GetPosition(this);
        // Keep the previewed IDs if the list was refreshed under a stationary pointer.
        // The store rechecks them inside the move transaction.
        PinDrop? drop = null;
        if (_draggingPin)
            drop = position == _pinPosition ? _preview : FindPinDrop(position);
        var click = !_draggingPin && this.InputHitTest(position) is Visual hit
            && hit.GetSelfAndVisualAncestors().OfType<Border>().Any(row => row.DataContext is PinnedFolder item && item.Id == pin.Id);
        var vm = ViewModel;
        CancelPinDrag();
        e.Handled = true;
        if (vm == null) return;
        try
        {
            if (drop is { } target)
                await vm.MovePinnedFolderAsync(pin, target.GroupId, target.AnchorId, target.After);
            else if (click)
                await vm.NavigateToCommand.ExecuteAsync(pin.FolderPath);
        }
        catch (Exception ex)
        {
            vm.StatusText = LocalizationText.Get(click ? "打开文件夹失败: {0}" : "拖放失败: {0}", ex.Message);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pinPointer == e.Pointer && e.Pointer.Captured != this) CancelPinDrag();
    }

    internal bool CancelPinDrag()
    {
        if (_pinPointer is not { } pointer) return false;
        _pinPointer = null;
        _pressedPin = null;
        _preview = null;
        _draggingPin = false;
        _pinScrollTimer?.Stop();
        PinDropLine.IsVisible = false;
        if (pointer.Captured == this) pointer.Capture(null);
        return true;
    }
}
