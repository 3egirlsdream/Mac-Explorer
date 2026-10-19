using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views;

public partial class InfoPanelView
{
    private int _tagSuggestionGeneration;
    private bool _closingTagSuggestions;

    private void OnTagInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        CloseTagSuggestions();
        _ = RefreshTagSuggestionsAsync();
    }

    private void OnTagInputGotFocus(object? sender, RoutedEventArgs e) => _ = RefreshTagSuggestionsAsync();

    private void OnTagInputLostFocus(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!TagInput.IsKeyboardFocusWithin && !TagSuggestions.IsKeyboardFocusWithin)
            CloseTagSuggestions();
    });

    private async Task<bool> RefreshTagSuggestionsAsync()
    {
        if (_closingTagSuggestions) return false;
        var generation = ++_tagSuggestionGeneration;
        var query = TagInput.Text?.Trim();
        var path = _currentFilePath;
        if (string.IsNullOrEmpty(query) || path == null || !this.IsAttachedToVisualTree()
            || ViewModel?.IsInfoPanelVisible != true
            || !TagInput.IsKeyboardFocusWithin && !TagSuggestions.IsKeyboardFocusWithin)
            return false;
        try
        {
            var tags = _fileTagService != null ? await _fileTagService.GetSidebarTagsAsync()
                : ViewModel.SidebarTags.ToArray();
            if (generation != _tagSuggestionGeneration || path != _currentFilePath) return false;
            var applied = GetDisplayedFinderTags().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = tags.Where(tag => tag.IsCustom && !FileTagCatalog.TryGetFinderColor(tag.Name, out _)
                    && !applied.Contains(tag.Name) && tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(tag => string.Equals(tag.Name, query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(tag => tag.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var selected = (TagSuggestions.SelectedItem as FileTag)?.Name;
            TagSuggestions.ItemsSource = matches;
            TagSuggestions.SelectedIndex = Math.Max(0, Array.FindIndex(matches, tag => tag.Name == selected));
            TagSuggestionsSurface.Width = TagInput.Bounds.Width;
            TagSuggestionsPopup.IsOpen = matches.Length > 0;
            return true;
        }
        catch (Exception ex)
        {
            if (generation == _tagSuggestionGeneration && ViewModel != null)
                ViewModel.StatusText = LocalizationText.Get("标签操作失败：{0}", ex.Message);
            return false;
        }
    }

    private async void OnTagInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseTagSuggestions(); e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up && TagSuggestionsPopup.IsOpen)
        {
            var count = TagSuggestions.ItemCount;
            if (count > 0)
            {
                var index = TagSuggestions.SelectedIndex;
                TagSuggestions.SelectedIndex = (index + (e.Key == Key.Down ? 1 : -1) + count) % count;
                if (TagSuggestions.SelectedItem is { } item) TagSuggestions.ScrollIntoView(item);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(TagInput.Text))
        {
            e.Handled = true;
            if (!TagSuggestionsPopup.IsOpen && !await RefreshTagSuggestionsAsync()) return;
            AddTag((TagSuggestions.SelectedItem as FileTag)?.Name);
            TagInput.Focus();
        }
    }

    private void OnTagSuggestionTapped(object? sender, TappedEventArgs e)
    {
        var source = e.Source as Control;
        var row = source as ListBoxItem ?? source?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not FileTag tag) return;
        AddTag(tag.Name);
        TagInput.Focus();
        e.Handled = true;
    }

    private void CloseTagSuggestions()
    {
        if (_closingTagSuggestions) return;
        _tagSuggestionGeneration++;
        _closingTagSuggestions = true;
        try
        {
            TagSuggestionsPopup.IsOpen = false;
            TagSuggestions.ItemsSource = null;
        }
        finally { _closingTagSuggestions = false; }
    }
}
