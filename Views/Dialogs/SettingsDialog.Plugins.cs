using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MacExplorer.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog
{
    private PluginManager? _plugins;
    private bool _pluginsClosed;

    private async Task InitializePluginsAsync()
    {
        _plugins ??= App.Services?.GetService<PluginManager>();
        if (_plugins == null) return;
        _plugins.Changed += OnPluginRegistryChanged;
        Closed += OnPluginSettingsClosed;
        await RunPluginActionAsync(() => _plugins.InitializeAsync());
        _ = LoadMarketAsync();
    }

    private void OnPluginSettingsClosed(object? sender, EventArgs e)
    {
        _pluginsClosed = true;
        _marketLifetime.Cancel();
        if (_plugins != null) _plugins.Changed -= OnPluginRegistryChanged;
    }

    private void OnPluginRegistryChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_pluginsClosed) RenderPlugins();
    });

    private void RenderPlugins()
    {
        PluginRows.Children.Clear();
        RenderMarket();
        if (_plugins == null) return;
        foreach (var plugin in _plugins.Plugins.Where(p => !p.Removed))
        {
            var name = new TextBlock { Text = plugin.Manifest.Name, FontWeight = FontWeight.SemiBold };
            name.Classes.Add("settings-label");
            var status = new TextBlock
            {
                Text = $"{plugin.Manifest.Version} · {(plugin.BuiltIn ? "内置" : plugin.FromMarket ? "市场安装" : "手动安装")} · {plugin.Status}",
                TextWrapping = TextWrapping.Wrap
            };
            status.Classes.Add("settings-description");
            var details = new StackPanel { Spacing = 6, Children = { name, status } };
            if (!string.IsNullOrEmpty(plugin.SupportedTypes))
            {
                var types = new TextBlock { Text = "支持：" + plugin.SupportedTypes, TextWrapping = TextWrapping.Wrap };
                types.Classes.Add("settings-description"); details.Children.Add(types);
            }
            if (plugin.LastError is { } error)
                details.Children.Add(new TextBlock { Text = "最近错误：" + error, TextWrapping = TextWrapping.Wrap });
            var actions = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            if (!plugin.Removed)
            {
                var enabled = new ToggleSwitch { IsChecked = plugin.Enabled, Content = "启用", Margin = new Thickness(0, 0, 12, 0) };
                enabled.Classes.Add("settings-toggle");
                enabled.IsCheckedChanged += async (_, _) => await RunPluginActionAsync(() => _plugins.SetEnabledAsync(plugin.Manifest.Id, enabled.IsChecked == true));
                actions.Children.Add(enabled);
                var uninstall = new Button { Content = "卸载", Margin = new Thickness(0, 0, 8, 0) };
                uninstall.Click += async (_, _) => await RunPluginActionAsync(() => _plugins.UninstallAsync(plugin.Manifest.Id));
                actions.Children.Add(uninstall);
            }
            if (plugin.LogPath is { } path && File.Exists(path))
            {
                var log = new Button { Content = "查看日志" };
                log.Click += async (_, _) => await RunPluginActionAsync(async () =>
                {
                    var dialog = new MacExplorer.Controls.DialogWindow
                    {
                        Title = plugin.Manifest.Name + " · 日志", Width = 760, Height = 480,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new TextBox { Text = await File.ReadAllTextAsync(path), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) }
                    };
                    await dialog.ShowDialog(this);
                });
                actions.Children.Add(log);
            }
            if (plugin.Manifest.Paid)
            {
                string entitlement;
                try
                {
                    var trial = _plugins.GetTrial(plugin.Manifest.Id);
                    entitlement = trial.Active ? $"试用中 · 到期 {trial.ExpiresAt:yyyy-MM-dd HH:mm}" : trial.StartedAt != null ? "试用已到期" : plugin.Manifest.TrialDays > 0 ? $"可试用 {plugin.Manifest.TrialDays} 天" : "付费插件";
                    var access = _plugins.GetAccess(plugin.Manifest.Id);
                    if (access?.Status == MacExplorer.PluginSdk.PluginAccessStatus.Allowed) entitlement = "已授权（最近检查）";
                }
                catch (Exception ex) { entitlement = ex.Message; }
                details.Children.Add(new TextBlock { Text = entitlement, TextWrapping = TextWrapping.Wrap });
            }
            if (plugin.Manifest.HasUserInterface)
            {
                var account = new Button { Content = "账号 / 授权", IsEnabled = plugin.Enabled && !plugin.Running };
                account.Click += async (_, _) => await RunPluginActionAsync(async () =>
                {
                    await using var session = await _plugins.StartAccountAsync(plugin.Manifest.Id, _marketLifetime.Token);
                    await PluginAuthorization.EnsureAsync(_plugins, session.Manifest, session,
                        new(Guid.NewGuid().ToString("N"), "", [], session.WorkDirectory), this, _marketLifetime.Token, manageAccount: true);
                });
                actions.Children.Add(account);
            }
            if (plugin.Running)
            {
                var cancel = new Button { Content = "取消任务" };
                cancel.Click += async (_, _) => await RunPluginActionAsync(() => _plugins.CancelAsync(plugin.Manifest.Id));
                actions.Children.Add(cancel);
            }
            details.Children.Add(actions);
            var card = new Border { Padding = new Thickness(16), Child = details };
            card.Classes.Add("settings-group"); PluginRows.Children.Add(card);
        }
    }

    private async Task RunPluginActionAsync(Func<Task> action)
    {
        try { await action(); if (!_pluginsClosed) PluginStatusText.Text = ""; }
        catch (Exception ex) { if (!_pluginsClosed) PluginStatusText.Text = ex.Message; }
        finally { if (!_pluginsClosed) RenderPlugins(); }
    }

    private async void OnInstallPlugin(object? sender, RoutedEventArgs e)
    {
        if (_plugins == null) return;
        await RunPluginActionAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "安装可信插件", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Mac Explorer 插件") { Patterns = ["*.mexplug"] }]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path != null) await _plugins.InstallAsync(path);
        });
    }

    private async void OnRestoreBuiltInPlugin(object? sender, RoutedEventArgs e)
    {
        if (_plugins != null) await RunPluginActionAsync(() => _plugins.RestoreBuiltInAsync());
    }
}
