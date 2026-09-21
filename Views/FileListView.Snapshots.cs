using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Services;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class FileListView
{
    private FileListViewModel? _snapshotOwner;
    private FileListScrollAnchor? _snapshotAnchor;
    private long _snapshotAnchorVersion;
    private long _snapshotInputVersion;
    private bool _snapshotHooksAttached;

    private void InitializeSnapshotAnchoring()
    {
        AttachedToVisualTree += (_, _) => { _snapshotHooksAttached = true; BindSnapshotOwner(); };
        DetachedFromVisualTree += (_, _) => { _snapshotHooksAttached = false; BindSnapshotOwner(); };
        DataContextChanged += (_, _) => BindSnapshotOwner();
        // Do not replay a captured position after new user input has superseded it.
        AddHandler(PointerWheelChangedEvent, (_, _) => _snapshotInputVersion++, RoutingStrategies.Tunnel, true);
        AddHandler(PointerPressedEvent, (_, _) => _snapshotInputVersion++, RoutingStrategies.Tunnel, true);
        AddHandler(PointerMovedEvent, (_, _) => _snapshotInputVersion++, RoutingStrategies.Tunnel, true);
        AddHandler(KeyDownEvent, (_, _) => _snapshotInputVersion++, RoutingStrategies.Tunnel, true);
    }

    private void BindSnapshotOwner()
    {
        if (_snapshotOwner != null)
        {
            _snapshotOwner.SnapshotApplying -= CaptureSnapshotAnchor;
            _snapshotOwner.SnapshotApplied -= RestoreSnapshotAnchor;
        }
        _snapshotAnchorVersion++;
        _snapshotAnchor = null;
        _snapshotOwner = _snapshotHooksAttached ? ViewModel : null;
        if (_snapshotOwner != null)
        {
            _snapshotOwner.SnapshotApplying += CaptureSnapshotAnchor;
            _snapshotOwner.SnapshotApplied += RestoreSnapshotAnchor;
        }
    }

    private void CaptureSnapshotAnchor()
    {
        _snapshotAnchorVersion++;
        _snapshotAnchor = null;
        var vm = _snapshotOwner;
        if (vm == null || vm.ScrollBehaviorAfterLoad != FileListViewModel.ScrollMode.PreservePosition
            || vm.Entries.Count == 0 || GetActiveScrollViewer() is not { } scroll) return;

        _snapshotAnchor = CaptureViewAnchor(scroll);
    }

    private FileListScrollAnchor? CaptureViewAnchor(ScrollViewer scroll)
    {
        if (!FastListActive) return null;
        var first = FastList.VisibleRange.First;
        return first < FastList.Rows.Count
            ? new FileListScrollAnchor(FastList.Rows[first].FullPath, FastList.RowBounds(first).Y, scroll.Offset.Y)
            : null;
    }

    internal void SaveTabViewState(ExplorerTabViewModel tab)
    {
        if (!ReferenceEquals(ViewModel, tab.FileList) || GetActiveScrollViewer() is not { } scroll) return;
        tab.CachedListState = new FileListTabViewState(tab.FileList.CurrentPath,
            tab.FileList.ViewMode, tab.FileList.GroupField, CaptureViewAnchor(scroll),
            scroll.Offset.Y, _fastFocusedPath);
    }

    internal void RestoreTabViewState(ExplorerTabViewModel tab)
    {
        if (tab.CachedListState is not { } state) return;
        var inputVersion = _snapshotInputVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible || VisualRoot == null || inputVersion != _snapshotInputVersion
                || !ReferenceEquals(ViewModel, tab.FileList) || tab.FileList.IsDirectoryLoading
                || state.Path != tab.FileList.CurrentPath || state.ViewMode != tab.FileList.ViewMode
                || state.GroupField != tab.FileList.GroupField || GetActiveScrollViewer() is not { } scroll) return;
            if (FastListActive)
            {
                var index = state.Anchor == null ? -1 : FastList.IndexOfPath(state.Anchor.ItemPath);
                if (index >= 0) FastList.ScrollToEntry(FastList.Rows[index], state.Anchor!.ItemViewportY);
                else FastList.ScrollToOffset(state.OffsetY);
                _fastFocusedPath = state.FocusedPath;
                FastList.KeyboardFocusPath = state.FocusedPath;
            }
        }, DispatcherPriority.Loaded);
    }

    internal void DeactivateTabInteraction()
    {
        CancelSlowRename();
        CancelActiveRename();
        ColumnFilterPopup.IsOpen = false;
        if (_marqueePointer != null) EndMarquee(_marqueePointer);
    }

    private void RestoreSnapshotAnchor()
    {
        if (_snapshotAnchor is not { } anchor || _snapshotOwner is not { } owner) return;
        var version = _snapshotAnchorVersion;
        var inputVersion = _snapshotInputVersion;
        var path = owner.CurrentPath;
        // The existing selection/absolute-offset restoration posts at Loaded first.
        // Queue after it, using the new fixed-height layout and current snapshot membership.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_snapshotHooksAttached || version != _snapshotAnchorVersion
                || inputVersion != _snapshotInputVersion || !ReferenceEquals(owner, ViewModel)
                || !string.Equals(path, owner.CurrentPath, StringComparison.Ordinal)
                || GetActiveScrollViewer() is not { } scroll) return;
            if (FastListActive)
            {
                var index = FastList.IndexOfPath(anchor.ItemPath);
                if (index >= 0) FastList.ScrollToEntry(FastList.Rows[index], anchor.ItemViewportY);
                else FastList.ScrollToOffset(anchor.AbsoluteOffsetFallback);
            }
        }, DispatcherPriority.Loaded);
    }
}
