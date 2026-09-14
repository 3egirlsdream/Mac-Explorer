using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

internal static class FileTabDragNavigation
{
    internal static void DragOver(ListBox tabs, DragEventArgs e, bool canActivate)
    {
        // A tab is a spring-loaded navigation target, NOT a file destination.
        // Keep the OS drag alive; only releasing over the file area may write files.
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
        if (!canActivate || e.DataTransfer.TryGetFiles()?.Any() != true) return;

        var hit = tabs.InputHitTest(e.GetPosition(tabs)) as Avalonia.Visual;
        var container = hit as ListBoxItem ?? hit?.FindAncestorOfType<ListBoxItem>();
        if (container?.DataContext is not ExplorerTabViewModel tab || !tabs.Items.Contains(tab)) return;

        // Preserve the two-way SelectedItem binding. MainWindow's normal tab
        // activation path updates the visible pane and active service bridges.
        // No pointer capture, await or new drag session is started here.
        tabs.SetCurrentValue(ListBox.SelectedItemProperty, tab);
    }

    internal static void Drop(DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }
}
