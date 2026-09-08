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
    private string? _fastRowsPath;
    private bool FastListActive => FastListHost.IsVisible;
    private bool CanUseFastFileList => ViewModel is { UseFastFileList: true, IsHomePage: false };

    private void InitializeFastFileList()
    {
        FastList.PointerPressed += (_, e) =>
        {
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
            _fastRowsPath = null;
            if (FastList.Rows.Count > 0) FastList.SetRows([]);
            UpdateFastListLoading();
            return;
        }
        var groups = vm.GroupField == GroupField.None ? null : vm.Groups;
        var entriesChanged = force || !ReferenceEquals(_fastSourceEntries, vm.Entries);
        if (entriesChanged || !ReferenceEquals(_fastSourceGroups, groups))
        {
            _fastSourceEntries = vm.Entries;
            _fastSourceGroups = groups;
            if (entriesChanged) _fastRowsPath = vm.CurrentPath;
            if (groups == null) FastList.SetRows(vm.Entries);
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
            : FindDataContextInAncestors(e.Source as Visual) as FileSystemEntry;

    private void HandleFastListNavigation(KeyEventArgs e)
    {
        if (!FastListActive || FastList.IsLoading || ViewModel == null || e.Handled || IsTextInputSource(e.Source)
            || (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt)) != 0
            || FastList.Rows.Count == 0) return;
        var current = _fastFocusedPath == null ? -1 : FastList.IndexOfPath(_fastFocusedPath);
        if (current < 0 && ViewModel.SelectedEntries.LastOrDefault() is { } selected)
            current = FastList.IndexOf(selected);
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
