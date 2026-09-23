using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Controls;

namespace MacExplorer.Views;

public partial class FileListView
{
    private string? _fastFocusedPath;
    private IReadOnlyList<FileSystemEntry>? _fastSourceEntries;
    private IReadOnlyList<FileGroup>? _fastSourceGroups;
    private IReadOnlyList<FileTreeRow>? _fastSourceTreeRows;
    private ViewMode? _fastSourceMode;
    private bool _fastSourceUsesTree;
    private string? _fastRowsPath;
    private bool FastListActive => FastListHost.IsVisible;

    private void InitializeFastFileList()
    {
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
            if (e.Handled || FastList.EntryAt(e.GetPosition(FastList), contentOnly: true) is not { } entry) return;
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
        if (entriesChanged || treeChanged || _fastSourceMode != vm.ViewMode
            || _fastSourceUsesTree != useTree
            || !ReferenceEquals(_fastSourceGroups, groups))
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
        => FastListActive && IsWithinVisual(e.Source as Visual, FastList)
            ? FastList.EntryAt(e.GetPosition(FastList))
            : null;

    private void HandleFastListNavigation(KeyEventArgs e)
    {
        if (!FastListActive || FastList.IsLoading || ViewModel == null || e.Handled || IsTextInputSource(e.Source)
            || (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt)) != 0
            || FastList.Rows.Count == 0) return;
        var current = _fastFocusedPath == null ? -1 : FastList.IndexOfPath(_fastFocusedPath);
        if (current < 0 && ViewModel.SelectedEntries.LastOrDefault() is { } selected)
            current = FastList.IndexOf(selected);
        if (ViewModel.ViewMode == ViewMode.Tree && current >= 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var row = FastList.TreeRowAt(current);
            if (row is { } treeRow && e.Key == Key.Right)
            {
                if (treeRow.CanExpand && !treeRow.IsExpanded) _ = ViewModel.ExpandTreeDirectoryAsync(treeRow.Entry);
                else if (treeRow.IsExpanded && current + 1 < FastList.Rows.Count
                         && FastList.TreeRowAt(current + 1)?.Depth > treeRow.Depth)
                    FocusTreeRow(current + 1);
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
                            FocusTreeRow(i);
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
        var entry = FastList.Rows[Math.Clamp(next, 0, FastList.Rows.Count - 1)];
        CancelSlowRename();
        _fastFocusedPath = entry.FullPath;
        FastList.KeyboardFocusPath = entry.FullPath;
        FastList.Focus(NavigationMethod.Directional);
        ViewModel.SelectEntry(entry, shiftKey: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        FastList.ScrollToEntry(entry);
        FastList.InvalidateVisual();
        e.Handled = true;
    }

    private void FocusTreeRow(int index)
    {
        if (ViewModel == null || index < 0 || index >= FastList.Rows.Count) return;
        var entry = FastList.Rows[index];
        _fastFocusedPath = entry.FullPath;
        FastList.KeyboardFocusPath = entry.FullPath;
        FastList.Focus(NavigationMethod.Directional);
        ViewModel.SelectEntry(entry);
        FastList.ScrollToEntry(entry);
        FastList.InvalidateVisual();
    }

    private void BeginFastRename(FileSystemEntry entry)
    {
        if (!FastList.ScrollToEntry(entry)) return;
        var editor = new TextBox
        {
            Text = entry.Name, MinWidth = 0, MinHeight = 0,
            TextAlignment = FastList.IsGrid ? Avalonia.Media.TextAlignment.Center : Avalonia.Media.TextAlignment.Left,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AppTypography.BindFontSize(editor, FastList.IsGrid ? AppTypography.Caption : AppTypography.Body);
        editor.Bind(HeightProperty, editor.GetResourceObservable("TypographyInlineEditorHeight"));
        editor.Bind(TextBox.FontWeightProperty, editor.GetResourceObservable("FontWeightLight"));
        editor.Classes.Add("inline-rename-editor");
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
        Canvas.SetLeft(_renameEditor, origin.X + (FastList.IsGrid ? FastList.RowBounds(index).Center.X - 50 : bounds.X));
        Canvas.SetTop(_renameEditor, origin.Y + (FastList.IsGrid ? bounds.Y : FastList.RowBounds(index).Y + Math.Round((FastFileList.RowHeight - _renameEditor.Height) / 2)));
        _renameEditor.Width = FastList.IsGrid ? 100 : Math.Min(Math.Max(72, bounds.Width + 12), Math.Max(1, FastList.ColumnWidths.Name - 8));
    }
}
