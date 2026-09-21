using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Automation;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class AiView : UserControl
{
    private FileListViewModel? _subscribedViewModel;
    private bool _hasSearched;

    public AiView()
    {
        InitializeComponent();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.TextTokens.CollectionChanged -= OnTextTokensChanged;
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnDataContextChanged(e);
        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.TextTokens.CollectionChanged += OnTextTokensChanged;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        UpdateTextTokens();
        UpdateState();
    }

    private void OnTextTokensChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateTextTokens();
        UpdateState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.Entries))
            UpdateState();
    }

    private void UpdateTextTokens()
    {
        TextTokensPanel.Children.Clear();
        if (ViewModel == null) return;

        foreach (var token in ViewModel.TextTokens)
        {
            var label = new TextBlock { Text = token.TagValue, Classes = { "tag-label" } };
            var count = new Border
            {
                Classes = { "tag-count" },
                Child = new TextBlock { Text = token.FileCount.ToString(), Classes = { "tag-count-text" } }
            };
            Grid.SetColumn(count, 1);
            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8,
                Children = { label, count }
            };
            var button = new Button
            {
                Content = content,
                Classes = { "ghost", "compact", "tag-chip" },
                Margin = new global::Avalonia.Thickness(0, 0, 6, 6),
                Tag = token.TagValue
            };
            var description = $"{token.TagValue}，{token.FileCount} 个文件";
            AutomationProperties.SetName(button, description);
            ToolTip.SetTip(button, description);
            button.Click += async (_, _) =>
            {
                if (ViewModel == null || button.Tag is not string query) return;
                AiSearchBox.Text = query;
                await SearchAsync(query);
            };
            TextTokensPanel.Children.Add(button);
        }
    }

    private void UpdateState()
    {
        var hasResults = _hasSearched && ViewModel?.Entries.Count > 0;
        ResultsView.IsVisible = hasResults;
        TokenView.IsVisible = !hasResults;
        ClearSearchButton.IsVisible = _hasSearched || !string.IsNullOrEmpty(AiSearchBox.Text);

        if (_hasSearched && ViewModel?.Entries.Count == 0)
        {
            EmptyState.IsVisible = true;
            EmptyTitle.Text = "未找到匹配结果";
            EmptyHint.Text = "试试其他关键词";
        }
        else
        {
            EmptyState.IsVisible = ViewModel?.TextTokens.Count == 0;
            EmptyTitle.Text = "还没有可搜索的识别内容";
            EmptyHint.Text = ViewModel?.IsAiAnalysisEnabled == true
                ? "浏览包含图片与 PDF 的文件夹，完成识别后可在这里搜索文字"
                : "AI 智能分析已关闭，可在设置中开启后浏览包含图片与 PDF 的文件夹";
        }
    }

    private async void OnAiSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(AiSearchBox.Text))
            await SearchAsync(AiSearchBox.Text.Trim());
        else if (e.Key == Key.Escape)
            await ClearSearchAsync();
    }

    private async Task SearchAsync(string query)
    {
        if (ViewModel == null) return;
        await ViewModel.SearchAiTagsCommand.ExecuteAsync(query);
        _hasSearched = true;
        UpdateState();
    }

    private async void ClearSearch(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => await ClearSearchAsync();

    private async Task ClearSearchAsync()
    {
        if (ViewModel == null) return;
        AiSearchBox.Text = "";
        _hasSearched = false;
        ViewModel.ClearTextSearchQuery();
        await ViewModel.RefreshCommand.ExecuteAsync(null);
        UpdateState();
    }
}
