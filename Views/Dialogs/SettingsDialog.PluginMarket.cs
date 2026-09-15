using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using MacExplorer.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog
{
    private readonly CancellationTokenSource _marketLifetime = new();
    private CancellationTokenSource? _marketSearchCancellation;
    private readonly List<PluginMarketItem> _marketItems = [];
    private int _marketPage;
    private bool _marketHasMore;
    private bool _marketBusy;
    private string _marketQuery = "";

    private async void OnSearchMarket(object? sender, RoutedEventArgs e) => await LoadMarketAsync();
    private async void OnMarketSearchKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await LoadMarketAsync(); } }
    private async Task LoadMarketAsync(bool next = false)
    {
        var client = App.Services?.GetService<PluginMarketClient>();
        if (client == null) return;
        _marketSearchCancellation?.Cancel();
        using var search = CancellationTokenSource.CreateLinkedTokenSource(_marketLifetime.Token);
        _marketSearchCancellation = search;
        if (!next) { _marketQuery = PluginMarketSearch.Text?.Trim() ?? ""; _marketPage = 0; _marketItems.Clear(); }
        PluginMarketStatus.Text = "正在加载…";
        try
        {
            var page = await client.ListAsync(_marketQuery, _marketPage, search.Token);
            search.Token.ThrowIfCancellationRequested();
            _marketItems.AddRange(page.Items); _marketHasMore = page.HasMore; _marketPage++;
            PluginMarketStatus.Text = _marketItems.Count == 0 ? "暂无匹配插件" : ""; RenderMarket();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_pluginsClosed && !search.IsCancellationRequested) PluginMarketStatus.Text = "市场加载失败，可点击搜索重试：" + ex.Message; }
        finally { if (ReferenceEquals(_marketSearchCancellation, search)) _marketSearchCancellation = null; }
    }
    private void RenderMarket()
    {
        if (PluginMarketRows == null) return;
        PluginMarketRows.Children.Clear();
        foreach (var item in _marketItems)
        {
            var m = item.Manifest;
            var installed = _plugins?.Plugins.FirstOrDefault(p => p.Manifest.Id == m.Id && !p.Removed);
            var update = installed != null && Version.TryParse(m.Version, out var available) && Version.TryParse(installed.Manifest.Version, out var current) && available > current;
            var info = new StackPanel { Spacing = 8 };
            info.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            var pricing = !m.Paid ? "免费" : m.TrialDays > 0 ? $"付费 · 试用 {m.TrialDays} 天" : "付费";
            info.Children.Add(new TextBlock { Text = $"{m.Developer} · {m.Version}\n{pricing}", TextWrapping = TextWrapping.Wrap });
            var types = string.Join("、", m.Commands.SelectMany(c => c.Match.Extensions.Concat(c.Match.FileNames).Concat(c.Match.TextFiles ? new[] { "文本文件" } : [])).Distinct());
            info.Children.Add(new TextBlock { Text = "支持：" + types, TextWrapping = TextWrapping.Wrap });
            var actions = new WrapPanel();
            var details = new Button { Content = "详情", Margin = new Thickness(0, 0, 8, 0) };
            details.Click += async (_, _) =>
            {
                var dialog = new MacExplorer.Controls.DialogWindow { Title = m.Name, Width = 480, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new ScrollViewer { Content = new TextBlock { Margin = new Thickness(20), Text = $"{m.Name} {m.Version}\n{m.Developer}\n{pricing}\n\n{m.Description}\n\n支持：{types}", TextWrapping = TextWrapping.Wrap } } };
                await dialog.ShowDialog(this);
            };
            var install = new Button { Content = installed == null ? "安装" : update ? "更新" : "已安装", IsEnabled = !_marketBusy && (installed == null || update) };
            install.Click += async (_, _) =>
            {
                var client = App.Services?.GetService<PluginMarketClient>(); if (client == null || _plugins == null) return;
                _marketBusy = true; RenderMarket();
                try
                {
                    await client.InstallAsync(item, _plugins, new Progress<double?>(value => { if (!_pluginsClosed) PluginMarketStatus.Text = $"下载 {m.Name}：{value:0}%"; }), _marketLifetime.Token);
                    if (!_pluginsClosed) PluginMarketStatus.Text = m.Name + " 已安装";
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!_pluginsClosed) PluginMarketStatus.Text = ex.Message; }
                finally { _marketBusy = false; if (!_pluginsClosed) RenderMarket(); }
            };
            actions.Children.Add(details); actions.Children.Add(install); info.Children.Add(actions);
            var card = new Border { Padding = new Thickness(14), Child = info }; card.Classes.Add("settings-group"); PluginMarketRows.Children.Add(card);
        }
        if (_marketHasMore)
        {
            var more = new Button { Content = "加载更多" }; more.Click += async (_, _) => await LoadMarketAsync(true); PluginMarketRows.Children.Add(more);
        }
    }
}
