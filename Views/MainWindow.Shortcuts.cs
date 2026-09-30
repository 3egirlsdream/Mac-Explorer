using Avalonia.Controls;
using Avalonia.VisualTree;
using MacExplorer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class MainWindow
{
    internal override bool IsShortcutAvailable(ShortcutDefinition definition)
    {
        if (base.IsShortcutAvailable(definition)) return true;
        var focused = FocusManager?.GetFocusedElement() as Avalonia.Visual;
        var text = IsInsideTextInput(focused);
        if (definition.Id.StartsWith("editor."))
            return IsMarkdownEditorOpen || text && definition.Id is "editor.copy" or "editor.cut" or "editor.paste" or "editor.all" or "editor.undo" or "editor.redo";
        if (IsModalInteractionBlocked || IsMarkdownEditorOpen || _homeFolderTransition != null
            || GlobalSearchOverlay.IsVisible || SuperPreviewControl.IsVisible) return false;
        if (text && Shortcuts.GetBindings(definition.Id).Any(b => Services.Impl.ShortcutService.IsTextEditingGesture(
            new Avalonia.Input.KeyEventArgs { Key = b.Key, KeyModifiers = b.Modifiers }))) return false;
        return definition.Id switch
        {
            ShortcutIds.GlobalSearch or ShortcutIds.NewTab => true,
            ShortcutIds.CloseTab or ShortcutIds.NextTab or ShortcutIds.PreviousTab => _vm?.SelectedTab != null,
            ShortcutIds.PageSearch or ShortcutIds.PathInput => ActiveWorkspace != null,
            ShortcutIds.Undo => !text && App.Services?.GetService<IFileOperationHistoryService>()?.CanUndo == true,
            "fixed.preview" or "fixed.rename" or "fixed.delete" => !text && ActiveWorkspace?.FileListView.IsVisible == true
                && _vm?.FileList.SelectedEntries.Count > 0,
            "fixed.up" => !text && ActiveWorkspace?.FileListView.IsVisible == true,
            _ => !text && ActiveWorkspace?.FileListView.IsVisible == true
                && ActiveWorkspace.FileListView.CanExecuteShortcut(definition.Id)
        };
    }
}
