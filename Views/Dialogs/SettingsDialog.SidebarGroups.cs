using Avalonia.Controls;
using Avalonia.Interactivity;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog
{
    private async void OnAddSidebarGroup(object? sender, RoutedEventArgs e)
    {
        if (_pinnedFolderService == null) return;
        var dialog = new TabGroupNameDialog("", "sidebar.groups.add", "sidebar.groups.name",
            async name => { await _pinnedFolderService.CreateGroupAsync(name); });
        await dialog.ShowDialog<string?>(this);
    }

    private async void OnRenameSidebarGroup(object? sender, RoutedEventArgs e)
    {
        if (_pinnedFolderService == null || sender is not Button { Tag: PinnedFolderGroup group }) return;
        var dialog = new TabGroupNameDialog(group.Name, "sidebar.groups.rename", "sidebar.groups.name",
            name => _pinnedFolderService.RenameGroupAsync(group.Id, name));
        await dialog.ShowDialog<string?>(this);
    }

    private async void OnDeleteSidebarGroup(object? sender, RoutedEventArgs e)
    {
        if (_pinnedFolderService == null || sender is not Button { Tag: PinnedFolderGroup group }) return;
        var dialog = new ConfirmDialog("sidebar.groups.delete",
            LocalizationText.Get("sidebar.groups.delete_message", group.Name), "sidebar.groups.delete");
        if (!await dialog.ShowDialog<bool>(this)) return;
        try { await _pinnedFolderService.DeleteGroupAsync(group.Id); }
        catch (Exception ex) { AppToast.ShowError(this, LocalizationText.Get("sidebar.groups.operation_failed", ex.Message)); }
    }
}
