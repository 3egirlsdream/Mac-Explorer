using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
        HomeCtxMenu.Opened += OnContextMenuOpened;
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    private async void OnContextMenuOpened(object? sender, EventArgs e)
    {
        if (ViewModel == null) { HomeCtxMenu.Items.Clear(); return; }
        await ViewModel.ShowBackgroundContextMenuAsync(0, 0);
        PopulateMenu(HomeCtxMenu, ViewModel.ContextMenuActions);
    }

    private static void PopulateMenu(ItemsControl parent, System.Collections.Generic.IList<ContextMenuAction> actions)
    {
        parent.Items.Clear();
        if (actions.Count == 0) return;

        foreach (var action in actions)
        {
            if (action.IsSeparator)
            {
                parent.Items.Add(new Separator());
                continue;
            }

            var item = new MenuItem { Header = action.Label, IsEnabled = action.IsEnabled };

            if (!string.IsNullOrEmpty(action.ShortcutText))
                item.InputGesture = ParseShortcut(action.ShortcutText);

            if (!string.IsNullOrEmpty(action.IconSvg))
            {
                try { item.Icon = new PathIcon { Data = Geometry.Parse(action.IconSvg), Width = 16, Height = 16 }; }
                catch { }
            }

            if (action.Execute != null) { var c = action; item.Click += async (_, _) => await c.Execute(); }
            if (action.SubItems is { Count: > 0 })
            {
                PopulateMenu(item, action.SubItems.ToList());
                ContextMenuPopupStyler.Attach(item);
            }

            parent.Items.Add(item);
        }
    }

    private static KeyGesture? ParseShortcut(string text)
    {
        var parsed = text
            .Replace("⌘", "Cmd+").Replace("⇧", "Shift+").Replace("⌥", "Alt+")
            .Replace("⌃", "Ctrl+").Replace("⌫", "Back").Replace("⌦", "Delete")
            .Replace("↩", "Enter").Replace("⇥", "Tab").Replace("⎋", "Escape");
        try { return KeyGesture.Parse(parsed); } catch { return null; }
    }


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

        var suggestion = HomeSearchSuggestionList.SelectedItem as OmniboxSuggestion
                         ?? (HomeSearchSuggestionList.ItemsSource as IEnumerable<OmniboxSuggestion>)?.FirstOrDefault();
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
