using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class HomeView : UserControl
{
    private CancellationTokenSource? _suggestionCancellation;

    public HomeView()
    {
        InitializeComponent();
    }

    public void FocusOmnibox()
    {
        HomeSearchBox.Focus();
        HomeSearchBox.SelectAll();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    private void OnHomeSearchGotFocus(object? sender, RoutedEventArgs e)
        => _ = RefreshHomeSuggestionsAsync();

    private void OnHomeSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (HomeSearchBox.IsKeyboardFocusWithin)
            _ = RefreshHomeSuggestionsAsync();
    }

    private void OnHomeSearchLostFocus(object? sender, RoutedEventArgs e)
    {
        DispatcherTimer.RunOnce(() =>
        {
            if (!HomeSearchBox.IsKeyboardFocusWithin
                && !HomeSearchSuggestionList.IsKeyboardFocusWithin)
                CloseHomeSuggestions();
        }, TimeSpan.FromMilliseconds(120));
    }

    private async void OnHomeSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseHomeSuggestions();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            MoveHomeSuggestionSelection(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(HomeSearchBox.Text))
            return;

        var suggestion = HomeSearchSuggestionList.SelectedItem as OmniboxSuggestion;
        if (suggestion == null && !OmniboxService.IsNavigablePath(OmniboxService.NormalizePath(HomeSearchBox.Text.Trim())))
            suggestion = (HomeSearchSuggestionList.ItemsSource as IEnumerable<OmniboxSuggestion>)?.FirstOrDefault();
        if (suggestion != null)
            await ExecuteHomeSuggestionAsync(suggestion);
        else if (ViewModel != null)
        {
            CloseHomeSuggestions();
            await OmniboxService.ExecuteInputAsync(ViewModel, HomeSearchBox.Text);
        }
        e.Handled = true;
    }

    private void MoveHomeSuggestionSelection(int delta)
    {
        var count = HomeSearchSuggestionList.ItemCount;
        if (count == 0)
            return;

        var current = HomeSearchSuggestionList.SelectedIndex;
        var next = current < 0
            ? (delta > 0 ? 0 : count - 1)
            : (current + delta + count) % count;
        HomeSearchSuggestionList.SelectedIndex = next;
        if (HomeSearchSuggestionList.SelectedItem is { } item)
            HomeSearchSuggestionList.ScrollIntoView(item);
    }

    private async void OnHomeSuggestionTapped(object? sender, TappedEventArgs e)
    {
        var suggestion = (sender as Control)?.DataContext as OmniboxSuggestion;
        if (suggestion != null)
            await ExecuteHomeSuggestionAsync(suggestion);
        e.Handled = true;
    }

    private async Task ExecuteHomeSuggestionAsync(OmniboxSuggestion suggestion)
    {
        if (ViewModel == null)
            return;

        CloseHomeSuggestions();
        await OmniboxService.ExecuteAsync(ViewModel, suggestion);
    }

    private async Task RefreshHomeSuggestionsAsync()
    {
        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = new CancellationTokenSource();
        var token = _suggestionCancellation.Token;

        IReadOnlyList<OmniboxSuggestion> suggestions;
        try
        {
            if (ViewModel == null)
                return;
            suggestions = await OmniboxService.GetSuggestionsAsync(
                ViewModel,
                HomeSearchBox.Text,
                token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
            return;

        HomeSearchSuggestionList.ItemsSource = suggestions;
        HomeSearchSuggestionList.SelectedIndex = -1;
        HomeSearchSurface.Width = HomeSearchContainer.Bounds.Width;
        HomeSearchPopup.IsOpen = suggestions.Count > 0 && HomeSearchBox.IsKeyboardFocusWithin;
    }

    private void CloseHomeSuggestions()
    {
        _suggestionCancellation?.Cancel();
        HomeSearchPopup.IsOpen = false;
        HomeSearchSuggestionList.SelectedIndex = -1;
    }

    private async void NavigateHomeDirectory(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        await ViewModel.NavigateToAsync(ViewModel.HomeDirectory);
    }

    private async void NavigateDesktop(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        await ViewModel.NavigateToAsync(System.IO.Path.Combine(ViewModel.HomeDirectory, "Desktop"));
    }

    private async void NavigateMacintoshHd(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        await ViewModel.NavigateToAsync("/");
    }
}
