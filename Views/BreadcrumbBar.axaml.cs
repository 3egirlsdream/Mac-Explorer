using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class BreadcrumbBar : UserControl
{
    private CancellationTokenSource? _suggestionCancellation;
    private TextBox? _activeInput;

    public BreadcrumbBar()
    {
        InitializeComponent();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    public void FocusPathInput()
    {
        if (ViewModel?.IsHomePage == true)
        {
            ActivateInput(HomePathInput, selectAll: false);
            return;
        }

        PathInput.Text = ViewModel?.CurrentPath ?? string.Empty;
        BrowseModePanel.IsVisible = false;
        PathInput.IsVisible = true;
        ActivateInput(PathInput, selectAll: true);
    }

    private void OnInputGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox input)
            ActivateInput(input, selectAll: false);
    }

    private void ActivateInput(TextBox input, bool selectAll)
    {
        _activeInput = input;
        PathSuggestionsPopup.PlacementTarget = input;
        PathSuggestionsSurface.Width = input.Bounds.Width;
        input.Focus();
        if (selectAll)
            input.SelectAll();
        _ = RefreshSuggestionsAsync(input.Text);
    }

    private void OnBrowseModeDoubleTapped(object? sender, TappedEventArgs e)
    {
        FocusPathInput();
        e.Handled = true;
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox input)
            return;

        if (e.Key == Key.Escape)
        {
            CloseSuggestions();
            if (input == PathInput)
                EndPathEditing();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            MoveSuggestionSelection(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(input.Text))
            return;

        var suggestion = PathSuggestionList.SelectedItem as OmniboxSuggestion
                         ?? (PathSuggestionList.ItemsSource as IEnumerable<OmniboxSuggestion>)?.FirstOrDefault();
        if (suggestion != null)
            await ExecuteSuggestionAsync(suggestion);
        else if (ViewModel != null)
        {
            CloseSuggestions();
            await OmniboxService.ExecuteInputAsync(ViewModel, input.Text);
            if (input == HomePathInput)
                HomePathInput.Text = string.Empty;
            else
                EndPathEditing();
        }
        e.Handled = true;
    }

    private void MoveSuggestionSelection(int delta)
    {
        var count = PathSuggestionList.ItemCount;
        if (count == 0)
            return;

        var current = PathSuggestionList.SelectedIndex;
        var next = current < 0
            ? (delta > 0 ? 0 : count - 1)
            : (current + delta + count) % count;
        PathSuggestionList.SelectedIndex = next;
        if (PathSuggestionList.SelectedItem is { } item)
            PathSuggestionList.ScrollIntoView(item);
    }

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox input && input.IsVisible && input == _activeInput)
            _ = RefreshSuggestionsAsync(input.Text);
    }

    private void OnInputLostFocus(object? sender, RoutedEventArgs e)
    {
        DispatcherTimer.RunOnce(() =>
        {
            if (_activeInput?.IsKeyboardFocusWithin == true
                || PathSuggestionList.IsKeyboardFocusWithin)
                return;

            CloseSuggestions();
            if (sender == PathInput)
                EndPathEditing();
        }, TimeSpan.FromMilliseconds(120));
    }

    private async void OnSuggestionTapped(object? sender, TappedEventArgs e)
    {
        var suggestion = (sender as Control)?.DataContext as OmniboxSuggestion;
        if (suggestion != null)
            await ExecuteSuggestionAsync(suggestion);
        e.Handled = true;
    }

    private async Task ExecuteSuggestionAsync(OmniboxSuggestion suggestion)
    {
        if (ViewModel == null)
            return;

        CloseSuggestions();
        await OmniboxService.ExecuteAsync(ViewModel, suggestion);
        if (_activeInput == HomePathInput)
            HomePathInput.Text = string.Empty;
        else
            EndPathEditing();
    }

    private async Task RefreshSuggestionsAsync(string? value)
    {
        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = new CancellationTokenSource();
        var token = _suggestionCancellation.Token;

        if (ViewModel == null)
            return;

        IReadOnlyList<OmniboxSuggestion> suggestions;
        try
        {
            suggestions = await OmniboxService.GetSuggestionsAsync(ViewModel, value, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || _activeInput == null)
            return;

        PathSuggestionList.ItemsSource = suggestions;
        PathSuggestionList.SelectedIndex = -1;
        PathSuggestionsPopup.IsOpen = suggestions.Count > 0 && _activeInput.IsVisible;
    }

    private void CloseSuggestions()
    {
        _suggestionCancellation?.Cancel();
        PathSuggestionsPopup.IsOpen = false;
        PathSuggestionList.SelectedIndex = -1;
    }

    private void EndPathEditing()
    {
        CloseSuggestions();
        PathInput.IsVisible = false;
        BrowseModePanel.IsVisible = ViewModel?.IsHomePage != true;
        if (_activeInput == PathInput)
            _activeInput = null;
    }

    private async void OnSegmentClicked(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel == null || sender is not Border { Tag: string path })
            return;

        if (VirtualPath.IsHomePath(path)
            || VirtualPath.IsRemotePath(path)
            || Directory.Exists(path))
            await ViewModel.NavigateToAsync(path);
    }
}
