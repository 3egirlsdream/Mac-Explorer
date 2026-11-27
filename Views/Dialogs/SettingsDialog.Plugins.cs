using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog
{
    private PluginManager? _plugins;
    private bool _pluginsClosed;

    private async Task InitializePluginsAsync()
    {
        _plugins = App.Services?.GetService<PluginManager>();
        if (_plugins == null) return;
        _plugins.Changed += OnPluginRegistryChanged;
        Closed += (_, _) =>
        {
            _pluginsClosed = true;
            _plugins.Changed -= OnPluginRegistryChanged;
        };
        await RunPluginActionAsync(() => _plugins.InitializeAsync());
    }

    private void OnPluginRegistryChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_pluginsClosed) RenderPlugins();
    });

    private void RenderPlugins()
    {
        PluginRows.Children.Clear();
        if (_plugins == null) return;
        foreach (var plugin in _plugins.Plugins)
        {
            var details = new StackPanel { Spacing = 4 };
            details.Children.Add(new TextBlock
            {
                Text = LocalizationText.Get("ui.367"), Classes = { "settings-label" },
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
            });
            details.Children.Add(new TextBlock
            {
                Text = $"{plugin.Manifest.Version} · {LocalizationText.Get("内置")} · {plugin.Status}",
                Classes = { "settings-description" }, TextWrapping = TextWrapping.Wrap
            });
            if (plugin.SupportedTypes is { Length: > 0 } types)
                details.Children.Add(new TextBlock
                {
                    Text = LocalizationText.Get("支持：") + types,
                    Classes = { "settings-description" }, TextWrapping = TextWrapping.Wrap
                });
            if (plugin.LastError is { } error)
                details.Children.Add(new TextBlock { Text = LocalizationText.Get("最近错误：") + error, TextWrapping = TextWrapping.Wrap });
            if (plugin.Running)
            {
                var cancel = new AppButton
                {
                    Content = LocalizationText.Get("取消任务"), Classes = { "secondary", "compact" },
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
                };
                cancel.Click += async (_, _) => await RunPluginActionAsync(() => _plugins.CancelAsync(plugin.Manifest.Id));
                details.Children.Add(cancel);
            }
            PluginRows.Children.Add(new Border { Padding = new Thickness(12), Child = details, Classes = { "settings-group" } });
        }
    }

    private async Task RunPluginActionAsync(Func<Task> action)
    {
        try { await action(); if (!_pluginsClosed) PluginStatusText.Text = ""; }
        catch (OperationCanceledException) { if (!_pluginsClosed) PluginStatusText.Text = LocalizationText.Get("已取消"); }
        catch (Exception ex) { if (!_pluginsClosed) PluginStatusText.Text = ex.Message; }
        finally { if (!_pluginsClosed) RenderPlugins(); }
    }
}
