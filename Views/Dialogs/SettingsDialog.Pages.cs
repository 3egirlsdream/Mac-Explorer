using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using MacExplorer.ViewModels;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog
{
    private readonly HashSet<string> _initializedPages = [];
    private bool _pagesReady;
    private LazyTabItem Page(string key) => SettingsTabs.Items.OfType<LazyTabItem>().Single(p => p.PageKey == key);
    private bool IsPageLoaded(string key) => Page(key).IsContentLoaded;

    private T GetPageControl<T>(string key, string name) where T : Control
    {
        var page = Page(key);
        page.EnsureContent();
        return page.FindContentControl<T>(name)
            ?? throw new InvalidOperationException($"Missing settings control {name} in {key}.");
    }

    // Page templates have independent name scopes; searching must not materialize unopened pages.
    internal T? FindControl<T>(string name) where T : Control =>
        this.FindNameScope()?.Find<T>(name)
        ?? SettingsTabs.Items.OfType<LazyTabItem>().Select(p => p.FindContentControl<T>(name)).FirstOrDefault(c => c != null);

    private void OnSettingsPageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pagesReady && ReferenceEquals(e.Source, SettingsTabs)) EnsureSelectedPage();
    }

    private void EnsureSelectedPage()
    {
        if (SettingsTabs.SelectedItem is not LazyTabItem page || !_initializedPages.Add(page.PageKey)) return;
        var initializing = _initializing;
        _initializing = true;
        try
        {
            page.EnsureContent();
            switch (page.PageKey)
            {
                case "general": LoadSettings(); break;
                case "send":
                    if (_localSendService != null)
                    {
                        LocalSendEnabledToggle.IsChecked = _localSendService.Enabled;
                        LocalSendAliasBox.Text = _localSendService.Alias;
                        LocalSendDirectoryText.Text = _localSendService.ReceiveDirectory;
                        ToolTip.SetTip(LocalSendDirectoryText, _localSendService.ReceiveDirectory);
                        UpdateLocalSendStatus();
                    }
                    break;
                case "locations": LoadSearchLocations(); break;
                case "display":
                    FolderPhotoCoversToggle.IsChecked = _settingsService.Get(FileListViewModel.FolderPhotoCoverSettingKey, false);
                    if (ViewModel != null)
                    {
                        HideSystemFilesToggle.IsChecked = ViewModel.HideSystemFiles;
                        HideDotFilesToggle.IsChecked = ViewModel.HideDotFiles;
                        HideDotFoldersToggle.IsChecked = ViewModel.HideDotFolders;
                    }
                    break;
                case "sidebar":
                    UsernameSettingLabel.Text = ViewModel?.UserName;
                    foreach (var toggle in PanelSidebar.GetLogicalDescendants().OfType<ToggleSwitch>())
                    {
                        if (toggle.Tag is not string key) continue;
                        _sidebarToggles[key] = toggle;
                        toggle.IsChecked = _settingsService.Get(key, true);
                    }
                    break;
                case "appearance":
                    _interactionStyleService.Initialize();
                    LoadInteractionStyleSettings();
                    ThemeModeCombo.SelectedIndex = _settingsService.Get("theme_mode", "system") switch { "light" => 1, "dark" => 2, _ => 0 };
                    TypographyPresetCombo.SelectedIndex = _typographyService.CurrentPreset switch { FontSizePreset.Small => 0, FontSizePreset.Large => 2, _ => 1 };
                    break;
                case "copilot": LoadCopilotSettings(); RefreshCopilotConsent(); break;
                case "about":
                    AboutVersion.Text = _localizationService.Get("settings.version", _appUpdateService.CurrentVersion);
                    PrivacyPolicyStatus.Text = PrivacyPolicy.Status;
                    SubscriptionSettings.IsVisible = DistributionChannel.IsAppStore;
                    UpdateButton.IsVisible = !DistributionChannel.IsAppStore;
                    break;
                case "shortcuts": ShortcutSettingsHost.Content = new ShortcutSettingsView(ShortcutService.Resolve()); break;
            }
        }
        catch { _initializedPages.Remove(page.PageKey); throw; }
        finally { _initializing = initializing; }

        if (page.PageKey is "send" or "plugins" or "openwith")
            Dispatcher.UIThread.Post(() => _ = LoadPageDataAsync(page.PageKey), DispatcherPriority.Background);
    }

    private async Task LoadPageDataAsync(string key)
    {
        if (_updateCancellation.IsCancellationRequested || Design.IsDesignMode) return;
        try
        {
            switch (key)
            {
                case "send" when _localSendService?.Enabled == true
                    && Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow }:
                    await _localSendService.StartAsync();
                    if (!_updateCancellation.IsCancellationRequested) UpdateLocalSendStatus();
                    break;
                case "plugins": await InitializePluginsAsync(); break;
                case "openwith":
                    await _openWithService.RemoveUnavailableAppsAsync();
                    if (!_updateCancellation.IsCancellationRequested) await LoadOpenWithAppsAsync();
                    break;
            }
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_updateCancellation.IsCancellationRequested) AppToast.ShowError(this, ex.Message);
        }
    }
}
