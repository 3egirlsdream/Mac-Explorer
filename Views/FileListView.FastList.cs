using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Controls;

namespace MacExplorer.Views;

public partial class FileListView
{
    private readonly FileNameTypeAhead _typeAhead = new();
    private bool _typeAheadShortcut;
    private string? _fastFocusedPath;
    private IReadOnlyList<FileSystemEntry>? _fastSourceEntries;
    private IReadOnlyList<FileGroup>? _fastSourceGroups;
    private IReadOnlyList<FileTreeRow>? _fastSourceTreeRows;
    private ViewMode? _fastSourceMode;
    private bool _fastSourceUsesTree;
    private string? _fastRowsPath;
    private bool FastListActive => FastList.IsVisible;

    private void InitializeFastFileList()
    {
        BindTypeAhead(FastList);
        FastList.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(FastList).Properties.IsLeftButtonPressed
                && ViewModel?.ViewMode == ViewMode.Tree
                && FastList.TreeDisclosureIndexAt(e.GetPosition(FastList)) is var treeIndex and >= 0)
            {
                var state = FastList.TreeRowAt(treeIndex)!.Value;
                if (state.HasError) _ = ViewModel.RetryTreeDirectoryAsync(state.Entry);
                else _ = ViewModel.ToggleTreeDirectoryAsync(state.Entry);
                e.Handled = true;
                return;
            }
            if (e.Handled || FastList.EntryAt(e.GetPosition(FastList), contentOnly: FastList.IsGrid) is not { } entry) return;
            _fastFocusedPath = entry.FullPath;
            if (e.ClickCount == 2 && e.GetCurrentPoint(FastList).Properties.IsLeftButtonPressed)
            {
                OpenEntryFromGesture(entry);
                e.Handled = true;
                return;
            }
            HandleEntryPointerPressed(FastList, entry, e);
        };
        FastList.PointerMoved += OnItemPointerMoved;
        FastList.PointerReleased += OnItemPointerReleased;
        FastList.ViewportChanged += PositionFastRenameEditor;
    }

    private void BindTypeAhead(Control list)
    {
        list.AddHandler(KeyDownEvent, (_, e) =>
        {
            _typeAheadShortcut = (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt)) != 0;
            if (_typeAheadShortcut || e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End
                or Key.PageUp or Key.PageDown or Key.Escape or Key.Enter or Key.Space)
                _typeAhead.Reset();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(KeyUpEvent, (_, _) => _typeAheadShortcut = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerPressedEvent, (_, _) => _typeAhead.Reset(), RoutingStrategies.Tunnel, handledEventsToo: true);
        list.LostFocus += (_, _) =>
        {
            _typeAhead.Reset();
            _typeAheadShortcut = false;
        };
        list.TextInput += (_, e) =>
        {
            if (!list.IsKeyboardFocusWithin || !list.IsEffectivelyVisible || _typeAheadShortcut || Shortcuts.IsRecording
                || _renameEditor != null || ColumnFilterPopup.IsOpen || _openMenu?.IsOpen == true
                || ViewModel is not { IsDirectoryLoading: false } vm || _fastRowsPath != vm.CurrentPath
                || FastList.IsLoading || FastList.Rows.Count == 0
                || string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl)) return;

            var current = IsGalleryVisible ? GalleryStrip.SelectedIndex : CurrentFastRowIndex();
            var index = _typeAhead.Find(FastList.Rows, current, e.Text, Environment.TickCount64);
            if (index >= 0)
            {
                if (IsGalleryVisible) SelectGalleryItem(index);
                else FocusFastRow(index);
            }
            e.Handled = true;
        };
    }

    private int CurrentFastRowIndex()
    {
        var current = _fastFocusedPath == null ? -1 : FastList.IndexOfPath(_fastFocusedPath);
        return current < 0 && ViewModel?.SelectedEntries.LastOrDefault() is { } selected
            ? FastList.IndexOf(selected) : current;
    }

    private void SyncFastListRows(bool force = false)
    {
        if (!FastListActive || ViewModel is not { } vm)
        {
            _fastSourceEntries = null;
            _fastSourceGroups = null;
            _fastSourceTreeRows = null;
            _fastSourceMode = null;
            _fastSourceUsesTree = false;
            _fastRowsPath = null;
            if (FastList.Rows.Count > 0) FastList.SetRows([]);
            UpdateFastListLoading();
            return;
        }
        var groups = vm.GroupField == GroupField.None ? null : vm.Groups;
        var useTree = vm.ViewMode == ViewMode.Tree && vm.IsTreeExpansionEnabled;
        var entriesChanged = force || !ReferenceEquals(_fastSourceEntries, vm.Entries);
        var treeChanged = useTree && !ReferenceEquals(_fastSourceTreeRows, vm.TreeRows);
        var modeChanged = _fastSourceMode != vm.ViewMode;
        var rowsStructureChanged = entriesChanged || treeChanged || _fastSourceUsesTree != useTree
            || !ReferenceEquals(_fastSourceGroups, groups);
        if (rowsStructureChanged)
        {
            _fastSourceEntries = vm.Entries;
            _fastSourceGroups = groups;
            _fastSourceTreeRows = useTree ? vm.TreeRows : null;
            _fastSourceMode = vm.ViewMode;
            _fastSourceUsesTree = useTree;
            if (entriesChanged) _fastRowsPath = vm.CurrentPath;
            if (useTree)
                FastList.SetTreeRows(vm.TreeRows,
                    vm.TreeGroups.Select(group => new FastFileListGroup(group.Name, group.VisibleCount, group.DirectCount)).ToArray());
            else if (groups == null) FastList.SetRows(vm.Entries);
            else FastList.SetRows(groups.SelectMany(group => group.Entries).ToArray(),
                groups.Select(group => new FastFileListGroup(group.Name, group.Entries.Count)).ToArray());
            QueueGalleryRefresh();
        }
        else if (modeChanged)
        {
            _fastSourceMode = vm.ViewMode;
            QueueGalleryRefresh();
        }
        UpdateFastListLoading();
        PositionFastRenameEditor();
    }

    private void UpdateFastListLoading()
    {
        var vm = ViewModel;
        var loading = vm != null && (vm.IsDirectoryLoading
            || vm.IsLoading && (vm.Entries.Count == 0 || _fastRowsPath != vm.CurrentPath));
        if (FastListActive && loading && !FastList.IsLoading)
        {
            CancelActiveRename();
            if (_fastRowsPath != vm?.CurrentPath) vm?.ClearSelection();
        }
        FastList.IsLoading = loading;
        FileScroll.IsHitTestVisible = !(vm?.IsDirectoryLoading == true || FastListActive && loading);
    }

    private FileSystemEntry? EntryAtPointer(PointerEventArgs e)
        => FastListActive && (IsWithinVisual(e.Source as Visual, FastList)
            || e.Source?.GetType().FullName == "Avalonia.Controls.Primitives.LightDismissOverlayLayer")
            ? FastList.EntryAt(e.GetPosition(FastList))
            : null;

    private void HandleFastListNavigation(KeyEventArgs e)
    {
        if (!FastListActive || IsGalleryVisible || FastList.IsLoading || ViewModel == null || e.Handled || IsTextInputSource(e.Source)
            || (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt)) != 0
            || FastList.Rows.Count == 0) return;
        var current = CurrentFastRowIndex();
        if (ViewModel.ViewMode == ViewMode.Tree && current >= 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var row = FastList.TreeRowAt(current);
            if (row is { } treeRow && e.Key == Key.Right)
            {
                if (treeRow.CanExpand && !treeRow.IsExpanded) _ = ViewModel.ExpandTreeDirectoryAsync(treeRow.Entry);
                else if (treeRow.IsExpanded && current + 1 < FastList.Rows.Count
                         && FastList.TreeRowAt(current + 1)?.Depth > treeRow.Depth)
                    FocusFastRow(current + 1);
                e.Handled = true;
                return;
            }
            if (row is { } leftRow && e.Key == Key.Left)
            {
                if (leftRow.IsExpanded) ViewModel.CollapseTreeDirectory(leftRow.Entry);
                else
                    for (var i = current - 1; i >= 0; i--)
                        if (FastList.TreeRowAt(i)?.Depth == leftRow.Depth - 1)
                        {
                            FocusFastRow(i);
                            break;
                        }
                e.Handled = true;
                return;
            }
        }
        var page = Math.Max(1, (int)(FastList.Viewport.Height / FastList.ItemHeight) - 1);
        var next = e.Key switch
        {
            Key.Up => current < 0 ? 0 : FastList.MoveVertical(current, -1),
            Key.Down => current < 0 ? 0 : FastList.MoveVertical(current, 1),
            Key.Left when FastList.IsGrid => current < 0 ? 0 : current - 1,
            Key.Right when FastList.IsGrid => current < 0 ? 0 : current + 1,
            Key.Home => 0,
            Key.End => FastList.Rows.Count - 1,
            Key.PageUp => current < 0 ? 0 : FastList.MoveVertical(current, -page),
            Key.PageDown => current < 0 ? 0 : FastList.MoveVertical(current, page),
            _ => -1
        };
        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            && !(FastList.IsGrid && e.Key is Key.Left or Key.Right)) return;
        FocusFastRow(Math.Clamp(next, 0, FastList.Rows.Count - 1), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    private void FocusFastRow(int index, bool extendSelection = false)
    {
        if (ViewModel == null || index < 0 || index >= FastList.Rows.Count) return;
        CancelSlowRename();
        var entry = FastList.Rows[index];
        _fastFocusedPath = entry.FullPath;
        FastList.KeyboardFocusPath = entry.FullPath;
        FastList.Focus(NavigationMethod.Directional);
        ViewModel.SelectEntry(entry, shiftKey: extendSelection);
        FastList.ScrollToEntry(entry);
        FastList.InvalidateVisual();
    }

    private void BeginFastRename(FileSystemEntry entry)
    {
        if (!FastList.ScrollToEntry(entry)) return;
        if (FastListHost.Offset.X > 0)
            FastListHost.Offset = new Vector(0, FastListHost.Offset.Y);
        var editor = new TextBox
        {
            Text = entry.Name, MinWidth = 0, MinHeight = 0,
            TextAlignment = Avalonia.Media.TextAlignment.Left,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AppTypography.BindFontSize(editor, AppTypography.Body);
        editor.Bind(HeightProperty, editor.GetResourceObservable("TypographyInlineEditorHeight"));
        editor.Bind(TextBox.FontWeightProperty, editor.GetResourceObservable("FontWeightRegular"));
        editor.Classes.Add("inline-rename-editor");
        editor.Classes.Set("bordered", FastList.IsGrid);
        _renameEditor = editor;
        _activeRenamePath = entry.FullPath;
        FastList.EditingPath = entry.FullPath;
        FastRenameOverlay.Children.Add(editor);
        PositionFastRenameEditor();
        FastList.InvalidateVisual();
        BindRenameEditor(editor, entry);
    }

    private void PositionFastRenameEditor()
    {
        if (_renameEditor?.Parent != FastRenameOverlay || _activeRenamePath == null) return;
        var index = FastList.IndexOfPath(_activeRenamePath);
        if (index < 0)
        {
            CancelActiveRename();
            return;
        }
        var bounds = FastList.NameBounds(index);
        var origin = FastList.TranslatePoint(default, FastRenameOverlay) ?? default;
        var rowBounds = FastList.RowBounds(index);
        Canvas.SetLeft(_renameEditor, origin.X + (FastList.IsGrid ? rowBounds.X : bounds.X));
        Canvas.SetTop(_renameEditor, origin.Y + (FastList.IsGrid ? bounds.Y : FastList.RowBounds(index).Y + Math.Round((FastFileList.RowHeight - _renameEditor.Height) / 2)));
        _renameEditor.Width = FastList.IsGrid ? rowBounds.Width : Math.Min(Math.Max(72, bounds.Width + 12), Math.Max(1, FastList.ColumnWidths.Name - 8));
    }
}
