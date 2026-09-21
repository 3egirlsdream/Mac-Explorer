using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Services;

namespace MacExplorer.Views;

public enum FileDragPhase { Started, Moved, Ended }
public sealed record FileDragSessionEvent(FileDragPhase Phase, Point ScreenPoint, DragDropEffects Effect);

public partial class FileListView
{
    internal bool HasOpenBrowsePopup => ColumnFilterPopup.IsOpen;
    internal bool DismissBrowsePopup()
    {
        if (!ColumnFilterPopup.IsOpen) return false;
        ColumnFilterPopup.IsOpen = false;
        return true;
    }
    public event Action<FileDragSessionEvent>? DragSessionChanged;
    public DragDropEffects AllowedDragEffects => ViewModel?.IsBrowseOnly == true
        ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move;

    private void OnNativeDragSession(FileDragSessionEvent e) => DragSessionChanged?.Invoke(e);

    private bool TryHandleBrowseShortcut(KeyEventArgs e)
    {
        if (e.Handled || IsTextInputSource(e.Source) || ViewModel == null) return false;
        var command = (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0;
        if (command)
        {
            if (e.Key == Key.Q) return false;
            switch (e.Key)
            {
                case Key.A: ViewModel.SelectAll(); break;
                case Key.R: _ = ViewModel.RefreshAsync(); break;
                case Key.Up: _ = ViewModel.NavigateUpAsync(); break;
                case Key.OemOpenBrackets: _ = ViewModel.NavigateBackAsync(); break;
                case Key.OemCloseBrackets: _ = ViewModel.NavigateForwardAsync(); break;
                case Key.O when ViewModel.SelectedEntries.Count == 1:
                    _ = ViewModel.OpenEntryAsync(ViewModel.SelectedEntries[0]); break;
            }
            // Unlisted commands cannot fall through to file operation shortcuts.
            e.Handled = true;
        }
        else if (e.Key is Key.Space or Key.Enter)
        {
            if (e.Key == Key.Enter && ViewModel.SelectedEntries.Count == 1)
                _ = ViewModel.OpenEntryAsync(ViewModel.SelectedEntries[0]);
            else _ = ViewModel.QuickLookSelectedAsync();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back or Key.F2) e.Handled = true;
        return e.Handled;
    }

    internal (FileListScrollAnchor? Anchor, double Offset) CaptureDeliveryPosition()
        => GetActiveScrollViewer() is { } scroll ? (CaptureViewAnchor(scroll), scroll.Offset.Y) : (null, 0);

    internal void RestoreDeliveryPosition(string path, FileListScrollAnchor? anchor, double offset)
    {
        var inputVersion = _snapshotInputVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel?.CurrentPath != path || inputVersion != _snapshotInputVersion) return;
            if (FastListActive)
            {
                var index = anchor == null ? -1 : FastList.IndexOfPath(anchor.ItemPath);
                if (index >= 0) FastList.ScrollToEntry(FastList.Rows[index], anchor!.ItemViewportY);
                else FastList.ScrollToOffset(offset);
            }
        }, DispatcherPriority.Loaded);
    }
}
