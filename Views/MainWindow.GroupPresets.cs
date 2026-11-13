using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Views;

public partial class MainWindow
{
    private bool _openingGroupPreset;

    private void ShowGroupPresets(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control target || _vm == null) return;
        if (_tabContextMenu is { IsOpen: true } current && target.Classes.Contains("active"))
        {
            current.Close();
            e.Handled = true;
            return;
        }
        _tabContextMenu?.Close();
        PaneLayoutPopup.IsOpen = false;
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight, MinWidth = 260 };
        var presets = TabGroupPresets.Read(App.Services.GetRequiredService<ISettingsService>());
        MenuItem Item(string title, Func<Task> action, string? icon = null, bool enabled = true)
        {
            var item = new MenuItem { Header = title, Icon = CreateTabMenuIcon(icon), IsEnabled = enabled };
            item.Click += async (_, args) => { args.Handled = true; menu.Close(); await action(); };
            return item;
        }
        foreach (var preset in presets)
            menu.Items.Add(Item(string.Format(LocalizationText.Get("tabs.preset.item"), preset.Name, preset.Session.Paths.Length),
                () => OpenGroupPresetAsync(preset), Icons.Folder, !_openingGroupPreset && !_closingTabs));
        if (presets.Length == 0)
            menu.Items.Add(new MenuItem { Header = LocalizationText.Get("tabs.preset.none"), IsEnabled = false });
        menu.Items.Add(new Separator());
        var group = _vm.SelectedTab?.Group;
        menu.Items.Add(Item(LocalizationText.Get("tabs.preset.save"), () => SaveGroupPresetAsync(group!), Icons.Save,
            group != null && !_closingTabs));
        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = LocalizationText.Get("tabs.preset.manage"),
            Icon = CreateTabMenuIcon(Icons.Settings), IsEnabled = presets.Length > 0 && !_closingTabs };
        foreach (var preset in presets)
        {
            var entry = new MenuItem { Header = preset.Name, Icon = CreateTabMenuIcon(Icons.Folder) };
            entry.Items.Add(Item(LocalizationText.Get("tabs.preset.rename"), () => RenameGroupPresetAsync(preset), Icons.Rename));
            entry.Items.Add(Item(LocalizationText.Get("tabs.preset.delete"), () => DeleteGroupPresetAsync(preset), Icons.Delete));
            manage.Items.Add(entry);
        }
        menu.Items.Add(manage);
        menu.Opened += (_, _) => target.Classes.Add("active");
        menu.Closed += (_, _) => target.Classes.Remove("active");
        OpenTabMenu(menu, target);
        e.Handled = true;
    }

    private async Task SaveGroupPresetAsync(TabGroupViewModel group)
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        using var modal = BlockModalParentInteraction();
        var name = await new TabGroupNameDialog(group.Name, "tabs.preset.save", "tabs.preset.name").ShowDialog<string?>(this);
        if (name == null || _vm?.Groups.Contains(group) != true) return;
        var savedGroup = _vm.CaptureGroups().Single(item => item.Id == group.Id);
        var preset = TabGroupPreset.Capture(name, _vm.Tabs.Select(tab =>
            tab.FileList.IsSearchMode || tab.FileList.IsArchiveView ? null : tab.FileList.CurrentPath), savedGroup,
            viewStates: _vm.Tabs.Select((tab, i) => (tab, i)).ToDictionary(p => p.i, p => p.tab.FileList.CaptureTabViewState()));
        if (preset == null) { AppToast.ShowError(this, LocalizationText.Get("tabs.preset.empty")); return; }
        var existing = TabGroupPresets.Read(settings).FirstOrDefault(item => SamePresetName(item.Name, name));
        if (existing != null)
        {
            var dialog = new ConfirmDialog("tabs.preset.replace", string.Format(LocalizationText.Get("tabs.preset.replace_message"), name),
                "tabs.preset.replace");
            if (!await dialog.ShowDialog<bool>(this)) return;
            preset = preset with { Id = existing.Id };
        }
        TabGroupPresets.Save(settings, preset);
        group.PresetId = preset.Id;
        ShowPresetNotice(LocalizationText.Get("tabs.preset.saved"));
    }

    private async Task RenameGroupPresetAsync(TabGroupPreset preset)
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        using var modal = BlockModalParentInteraction();
        var name = await new TabGroupNameDialog(preset.Name, "tabs.preset.rename", "tabs.preset.name").ShowDialog<string?>(this);
        if (name == null) return;
        var presets = TabGroupPresets.Read(settings);
        if (presets.Any(item => item.Id != preset.Id && SamePresetName(item.Name, name)))
        { AppToast.ShowError(this, LocalizationText.Get("tabs.preset.name_taken")); return; }
        if (presets.FirstOrDefault(item => item.Id == preset.Id) is { } current)
            TabGroupPresets.Save(settings, current with { Name = name });
    }

    private async Task DeleteGroupPresetAsync(TabGroupPreset preset)
    {
        using var modal = BlockModalParentInteraction();
        var dialog = new ConfirmDialog("tabs.preset.delete", string.Format(LocalizationText.Get("tabs.preset.delete_message"), preset.Name),
            "tabs.preset.delete");
        if (await dialog.ShowDialog<bool>(this))
            TabGroupPresets.Delete(App.Services.GetRequiredService<ISettingsService>(), preset.Id);
    }

    internal async Task OpenGroupPresetAsync(TabGroupPreset preset)
    {
        if (_openingGroupPreset || _shutdownStarted || _vm == null) return;
        var existing = _vm.Groups.FirstOrDefault(group => group.PresetId == preset.Id
            || group.Id == preset.Session.Groups[0].Id);
        if (existing != null)
        {
            existing.PresetId = preset.Id;
            if (existing.IsCollapsed) _vm.ToggleGroup(existing);
            _vm.ActivateGroup(existing);
            return;
        }
        _openingGroupPreset = true;
        var model = _vm;
        try
        {
            // Validate off the UI thread; unavailable volumes must not alter the saved template.
            var session = await Task.Run(() => preset.CreateSession(CanRestoreFolder));
            if (_shutdownStarted || !ReferenceEquals(_vm, model)) return;
            if (session == null) { AppToast.ShowError(this, LocalizationText.Get("tabs.preset.unavailable")); return; }
            var ordered = await PrepareRestoredFolderTabsAsync(session, append: true);
            if (session.Paths.Length < preset.Session.Paths.Length)
                ShowPresetNotice(string.Format(LocalizationText.Get("tabs.preset.skipped"), preset.Session.Paths.Length - session.Paths.Length));
            await LoadRestoredFolderTabsAsync(ordered);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Opening group preset failed: {ex}");
            if (!_shutdownStarted) AppToast.ShowError(this, LocalizationText.Get("tabs.preset.open_failed"));
        }
        finally { _openingGroupPreset = false; }
    }

    private static bool SamePresetName(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private void ShowPresetNotice(string message) => AppToast.For(this).Show(new Notification(
        LocalizationText.Get("tabs.preset.title"), message, NotificationType.Information, TimeSpan.FromSeconds(5)));
}
