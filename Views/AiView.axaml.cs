using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
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
            var button = new Button
            {
                Content = $"{token.TagValue}  {token.FileCount}",
                Padding = new global::Avalonia.Thickness(12, 6),
                Margin = new global::Avalonia.Thickness(0, 0, 8, 8),
                CornerRadius = new global::Avalonia.CornerRadius(6),
                FontSize = 12,
                Tag = token.TagValue
            };
            button.Classes.Add("secondary");
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
            EmptyTitle.Text = "还没有识别到文字内容";
            EmptyHint.Text = "浏览包含图片的文件夹后会自动建立文字索引";
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
