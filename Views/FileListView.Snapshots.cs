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

        if (FastListActive)
        {
            var first = FastList.VisibleRange.First;
            if (first < FastList.Rows.Count)
                _snapshotAnchor = new FileListScrollAnchor(FastList.Rows[first].FullPath,
                    FastList.RowBounds(first).Y, scroll.Offset.Y);
            return;
        }
        if (vm.ViewMode != ViewMode.List || vm.GroupField != GroupField.None) return;

        var index = Math.Clamp((int)Math.Floor(scroll.Offset.Y / FileListScrollAnchor.DetailsRowHeight), 0, vm.Entries.Count - 1);
        // At most one realized-container lookup. No traversal of all files/visual descendants.
        var container = FileItemsList.ContainerFromIndex(index);
        var viewportY = container?.TranslatePoint(default, scroll)?.Y
            ?? index * FileListScrollAnchor.DetailsRowHeight - scroll.Offset.Y;
        var origin = viewportY + scroll.Offset.Y - index * FileListScrollAnchor.DetailsRowHeight;
        _snapshotAnchor = new FileListScrollAnchor(vm.Entries[index].FullPath, viewportY, scroll.Offset.Y, origin);
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
                return;
            }
            if (owner.ViewMode != ViewMode.List || owner.GroupField != GroupField.None) return;
            scroll.Offset = new Vector(0, anchor.Resolve(owner.Entries, scroll.Viewport.Height));
        }, DispatcherPriority.Loaded);
    }

    private static int GetEntryThumbnailPixelSize(Image image)
        => FileThumbnailSizing.GetPixelSize(image.Width, TopLevel.GetTopLevel(image)?.RenderScaling ?? 1);
}
