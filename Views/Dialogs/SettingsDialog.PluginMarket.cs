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
        PluginMarketStatus.Text = MacExplorer.Services.Impl.LocalizationText.Get("正在加载…");
        try
        {
            var page = await client.ListAsync(_marketQuery, _marketPage, search.Token);
            search.Token.ThrowIfCancellationRequested();
            _marketItems.AddRange(page.Items); _marketHasMore = page.HasMore; _marketPage++;
            PluginMarketStatus.Text = _marketItems.Count == 0 ? MacExplorer.Services.Impl.LocalizationText.Get("暂无匹配插件") : ""; RenderMarket();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_pluginsClosed && !search.IsCancellationRequested) PluginMarketStatus.Text = MacExplorer.Services.Impl.LocalizationText.Get("市场加载失败，可点击搜索重试：") + ex.Message; }
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
            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            var pricing = !m.Paid ? MacExplorer.Services.Impl.LocalizationText.Get("免费") : m.TrialDays > 0 ? MacExplorer.Services.Impl.LocalizationText.Get("付费 · 试用 {0} 天", m.TrialDays) : MacExplorer.Services.Impl.LocalizationText.Get("付费");
            info.Children.Add(new TextBlock { Text = $"{m.Developer} · {m.Version}\n{pricing}", TextWrapping = TextWrapping.Wrap });
            var types = string.Join("、", m.Commands.SelectMany(c => c.Match.Extensions.Concat(c.Match.FileNames).Concat(c.Match.TextFiles ? new[] { MacExplorer.Services.Impl.LocalizationText.Get("文本文件") } : [])).Distinct());
            info.Children.Add(new TextBlock { Text = MacExplorer.Services.Impl.LocalizationText.Get("支持：") + types, TextWrapping = TextWrapping.Wrap });
            var actions = new WrapPanel();
            var details = new Button { Content = MacExplorer.Services.Impl.LocalizationText.Get("详情"), Margin = new Thickness(0, 0, 8, 0) };
            details.Click += async (_, _) =>
            {
                var dialog = new MacExplorer.Controls.DialogWindow { Title = m.Name, Width = 480, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new ScrollViewer { Content = new TextBlock { Margin = new Thickness(20), Text = MacExplorer.Services.Impl.LocalizationText.Get("{0} {1}\n{2}\n{3}\n\n{4}\n\n支持：{5}", m.Name, m.Version, m.Developer, pricing, m.Description, types), TextWrapping = TextWrapping.Wrap } } };
                await dialog.ShowDialog(this);
            };
            var install = new Button { Content = installed == null ? MacExplorer.Services.Impl.LocalizationText.Get("安装") : update ? MacExplorer.Services.Impl.LocalizationText.Get("更新") : MacExplorer.Services.Impl.LocalizationText.Get("已安装"), IsEnabled = !_marketBusy && (installed == null || update) };
            install.Click += async (_, _) =>
            {
                var client = App.Services?.GetService<PluginMarketClient>(); if (client == null || _plugins == null) return;
                _marketBusy = true; RenderMarket();
                try
                {
                    await client.InstallAsync(item, _plugins, new Progress<double?>(value => { if (!_pluginsClosed) PluginMarketStatus.Text = MacExplorer.Services.Impl.LocalizationText.Get("下载 {0}：{1:0}%", m.Name, value); }), _marketLifetime.Token);
                    if (!_pluginsClosed) PluginMarketStatus.Text = m.Name + MacExplorer.Services.Impl.LocalizationText.Get(" 已安装");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!_pluginsClosed) PluginMarketStatus.Text = ex.Message; }
                finally { _marketBusy = false; if (!_pluginsClosed) RenderMarket(); }
            };
            actions.Children.Add(details); actions.Children.Add(install);
            foreach (var button in actions.Children.OfType<Button>())
            {
                button.Classes.Add("secondary"); button.Classes.Add("plugin-action");
                button.Margin = new Thickness(0, 6, 6, 0);
            }
            info.Children.Add(actions);
            var card = new Border { Padding = new Thickness(12), Child = info }; card.Classes.Add("settings-group"); PluginMarketRows.Children.Add(card);
        }
        if (_marketHasMore)
        {
            var more = new Button { Content = MacExplorer.Services.Impl.LocalizationText.Get("加载更多") }; more.Classes.Add("secondary"); more.Classes.Add("plugin-action"); more.Click += async (_, _) => await LoadMarketAsync(true); PluginMarketRows.Children.Add(more);
        }
    }
}
