using Avalonia.Input;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views;

public partial class FileListView
{
    internal IShortcutService Shortcuts { get; set; } = ShortcutService.Resolve();

    private void RefreshOpenMenuShortcuts()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        { Avalonia.Threading.Dispatcher.UIThread.Post(RefreshOpenMenuShortcuts); return; }
        if (_openMenu == null) return;
        void Refresh(Avalonia.Controls.ItemsControl menu)
        {
            foreach (var item in menu.Items.OfType<Avalonia.Controls.MenuItem>())
            {
                if (item.Tag is Models.ContextMenuAction { ShortcutId: { } id })
                    item.InputGesture = Shortcuts.GetBindings(id)[0].Gesture;
                Refresh(item);
            }
        }
        Refresh(_openMenu);
        foreach (var button in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(_openMenu).OfType<Avalonia.Controls.Button>())
            if (button.Tag is Models.ContextMenuAction { ShortcutId: { } id } action)
                Avalonia.Controls.ToolTip.SetTip(button, $"{action.Label} {Shortcuts.GetDisplay(id)}");
    }

    internal bool CanExecuteShortcut(string id)
    {
        if (Services.Subscriptions.SubscriptionAccess.IsLocked) return false;
        var vm = ViewModel;
        if (vm == null || ColumnFilterPopup.IsOpen) return false;
        if ((vm.IsDirectoryLoading || FastListActive && FastList.IsLoading)
            && id is not (ShortcutIds.Home or ShortcutIds.Up or ShortcutIds.Back or ShortcutIds.Forward or ShortcutIds.Refresh))
            return false;
        if (vm.IsBrowseOnly && id is not (ShortcutIds.SelectAll or ShortcutIds.Refresh or ShortcutIds.Up
            or ShortcutIds.Back or ShortcutIds.Forward or ShortcutIds.Open)) return false;
        return id switch
        {
            ShortcutIds.Copy or ShortcutIds.Cut or ShortcutIds.Trash => !vm.IsArchiveView && vm.SelectedEntries.Count > 0,
            ShortcutIds.Paste => !vm.IsArchiveView && vm.HasPasteableContent,
            ShortcutIds.NewFile or ShortcutIds.NewFolder => !vm.IsArchiveView,
            ShortcutIds.Open or ShortcutIds.Info => vm.SelectedEntries.Count == 1,
            ShortcutIds.BatchRename => vm.CanBatchRename,
            ShortcutIds.Back => vm.CanGoBack,
            ShortcutIds.Forward => vm.CanGoForward,
            ShortcutIds.CopyPath => !string.IsNullOrEmpty(vm.CurrentPath),
            ShortcutIds.SelectAll or ShortcutIds.Refresh or ShortcutIds.Home or ShortcutIds.Up or ShortcutIds.PreviewPane => true,
            _ => false
        };
    }

    private bool TryHandleConfiguredFileShortcut(KeyEventArgs e)
    {
        if (e.Handled || Shortcuts.IsRecording || IsTextInputSource(e.Source) || ViewModel == null) return false;
        var definition = Shortcuts.Definitions.FirstOrDefault(d => d.IsEditable && d.Id != ShortcutIds.FullScreen
            && (d.Scope & ShortcutScope.Delivery) != 0 && Shortcuts.Matches(d.Id, e));
        if (definition == null) return false;
        // Consume matched commands even when unavailable, so they cannot leak into a lower-priority handler.
        e.Handled = true;
        if (!CanExecuteShortcut(definition.Id)) return true;
        DismissContextMenu();
        switch (definition.Id)
        {
            case ShortcutIds.Copy: ViewModel.CopySelected(); break;
            case ShortcutIds.Cut: ViewModel.CutSelected(); break;
            case ShortcutIds.Paste: _ = ViewModel.PasteAsync(); break;
            case ShortcutIds.SelectAll: ViewModel.SelectAll(); break;
            case ShortcutIds.Open: _ = ViewModel.OpenEntryAsync(ViewModel.SelectedEntries[0]); break;
            case ShortcutIds.Refresh: _ = ViewModel.RefreshAsync(); break;
            case ShortcutIds.Info: _ = ViewModel.ShowMetadataAsync(ViewModel.SelectedEntries[0]); break;
            case ShortcutIds.NewFile: _ = ViewModel.CreateNewFileAsync(".txt"); break;
            case ShortcutIds.NewFolder: _ = ViewModel.CreateNewFolderAsync(); break;
            case ShortcutIds.BatchRename: ViewModel.RaiseRequestBatchRename(); break;
            case ShortcutIds.CopyPath: _ = ViewModel.CopyPathAsync(); break;
            case ShortcutIds.Home: ViewModel.GoHome(); break;
            case ShortcutIds.Up: _ = ViewModel.NavigateUpAsync(); break;
            case ShortcutIds.Back: _ = ViewModel.NavigateBackAsync(); break;
            case ShortcutIds.Forward: _ = ViewModel.NavigateForwardAsync(); break;
            case ShortcutIds.PreviewPane: ViewModel.TogglePreviewPane(); break;
            case ShortcutIds.Trash: ViewModel.ShowDeleteConfirmDialog(); break;
        }
        return true;
    }
}
