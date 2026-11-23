using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog
{
    private void LoadArchiveSettings()
    {
        GetPageControl<ComboBox>("general", "ArchiveDoubleClickCombo").SelectedIndex =
            _settingsService.Get(ArchivePreferences.ExtractOnDoubleClick, false) ? 1 : 0;
        GetPageControl<ComboBox>("general", "ArchiveFormatCombo").SelectedIndex = (int)ArchivePreferences.ReadFormat(_settingsService);
        GetPageControl<ToggleSwitch>("general", "ArchiveThumbnailsToggle").IsChecked =
            _settingsService.Get(ArchivePreferences.ShowThumbnails, true);
        GetPageControl<ToggleSwitch>("general", "ArchivePasswordsToggle").IsChecked =
            _settingsService.Get(ArchivePreferences.SavePasswords, false);
    }

    private void OnArchiveChoiceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || sender is not ComboBox { SelectedIndex: >= 0 } choice) return;
        if (choice.Name == "ArchiveDoubleClickCombo")
            _settingsService.Set(ArchivePreferences.ExtractOnDoubleClick, choice.SelectedIndex == 1);
        else
            _settingsService.Set(ArchivePreferences.DefaultFormat, ((ArchiveFormat)choice.SelectedIndex).ToString());
    }

    private void OnArchiveToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleSwitch toggle) return;
        var preference = toggle.Name == "ArchiveThumbnailsToggle" ? ArchivePreferences.ShowThumbnails : ArchivePreferences.SavePasswords;
        _settingsService.Set(preference, toggle.IsChecked == true);
    }
}
