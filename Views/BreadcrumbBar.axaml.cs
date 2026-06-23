using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Services;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class BreadcrumbBar : UserControl
{
    private CancellationTokenSource? _suggestionCancellation;
    private bool _suppressSuggestionRefresh;

    public BreadcrumbBar()
    {
        InitializeComponent();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    public void FocusPathInput()
    {
        if (ViewModel?.IsHomePage == true)
        {
            HomePathInput.Focus();
            return;
        }

        PathInput.Text = ViewModel?.CurrentPath ?? string.Empty;
        BrowseModePanel.IsVisible = false;
        PathInput.IsVisible = true;
        PathInput.Focus();
        PathInput.SelectAll();
        _ = RefreshSuggestionsAsync(PathInput.Text);
    }

    private async void OnHomePathKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel == null || sender is not TextBox tb) return;

        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(tb.Text))
        {
            var path = ExpandTildePath(tb.Text.Trim());
            if (Directory.Exists(path))
            {
                await ViewModel.NavigateToCommand.ExecuteAsync(path);
                tb.Text = "";
            }
        }
    }

    private void OnHomePathLostFocus(object? sender, RoutedEventArgs e)
    {
    }

    private void OnBrowseModeDoubleTapped(object? sender, TappedEventArgs e)
    {
        FocusPathInput();
        e.Handled = true;
    }

    private async void OnPathInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndPathEditing();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down && PathSuggestionList.ItemCount > 0)
        {
            PathSuggestionList.SelectedIndex = Math.Min(
                PathSuggestionList.SelectedIndex + 1,
                PathSuggestionList.ItemCount - 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up && PathSuggestionList.ItemCount > 0)
        {
            PathSuggestionList.SelectedIndex = Math.Max(PathSuggestionList.SelectedIndex - 1, 0);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;

        var path = PathSuggestionList.SelectedItem as string ?? PathInput.Text;
        if (await NavigateToPathAsync(path))
            EndPathEditing();
        e.Handled = true;
    }

    private void OnPathInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (PathInput.IsVisible && !_suppressSuggestionRefresh)
            _ = RefreshSuggestionsAsync(PathInput.Text);
    }

    private void OnPathInputLostFocus(object? sender, RoutedEventArgs e)
    {
        DispatcherTimer.RunOnce(() =>
        {
            if (!PathInput.IsKeyboardFocusWithin && !PathSuggestionList.IsKeyboardFocusWithin)
                EndPathEditing();
        }, TimeSpan.FromMilliseconds(120));
    }

    private void OnPathSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PathSuggestionList.SelectedItem is string path)
        {
            _suppressSuggestionRefresh = true;
            try
            {
                PathInput.Text = path;
                PathInput.CaretIndex = path.Length;
            }
            finally
            {
                _suppressSuggestionRefresh = false;
            }
        }
    }

    private async void OnPathSuggestionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (PathSuggestionList.SelectedItem is string path && await NavigateToPathAsync(path))
            EndPathEditing();
    }

    private async Task<bool> NavigateToPathAsync(string? value)
    {
        if (ViewModel == null || string.IsNullOrWhiteSpace(value)) return false;
        var path = ExpandTildePath(value.Trim());

        // If currently in a remote view and the path is relative/absolute (not sentinel),
        // construct the full sentinel path
        if (ViewModel.IsRemoteView && !VirtualPath.IsRemotePath(path) && ViewModel.CurrentRemoteServerId != null)
        {
            var remotePath = VirtualPath.BuildRemotePath(ViewModel.CurrentRemoteServerId, path);
            await ViewModel.NavigateToCommand.ExecuteAsync(remotePath);
            return true;
        }

        if (VirtualPath.IsRemotePath(path))
        {
            await ViewModel.NavigateToCommand.ExecuteAsync(path);
            return true;
        }

        if (!Directory.Exists(path)) return false;
        await ViewModel.NavigateToCommand.ExecuteAsync(Path.GetFullPath(path));
        return true;
    }

    private async Task RefreshSuggestionsAsync(string? value)
    {
        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = new CancellationTokenSource();
        var token = _suggestionCancellation.Token;

        string[] suggestions;
        try
        {
            suggestions = await Task.Run(() => GetPathSuggestions(value, token), token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested) return;

        PathSuggestionList.ItemsSource = suggestions;
        PathSuggestionList.SelectedIndex = -1;
        PathSuggestionsPopup.IsOpen = suggestions.Length > 0 && PathInput.IsVisible;
    }

    private static string[] GetPathSuggestions(string? value, CancellationToken token)
    {
        var path = ExpandTildePath(value?.Trim() ?? string.Empty);
        if (string.IsNullOrEmpty(path)) return [];

        var directory = path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : Path.GetDirectoryName(path);
        var prefix = path.EndsWith(Path.DirectorySeparatorChar)
            ? string.Empty
            : Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return [];

        try
        {
            return Directory.EnumerateDirectories(directory)
                .Where(candidate => Path.GetFileName(candidate)
                    .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => Path.GetFileName(candidate), StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(candidate => candidate + Path.DirectorySeparatorChar)
                .TakeWhile(_ => !token.IsCancellationRequested)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void EndPathEditing()
    {
        _suggestionCancellation?.Cancel();
        PathSuggestionsPopup.IsOpen = false;
        PathInput.IsVisible = false;
        BrowseModePanel.IsVisible = ViewModel?.IsHomePage != true;
    }

    private async void OnSegmentClicked(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel == null || sender is not Border border) return;
        if (border.Tag is not string path) return;

        // For remote paths, navigate directly without Directory.Exists check
        if (VirtualPath.IsRemotePath(path))
        {
            await ViewModel.NavigateToCommand.ExecuteAsync(path);
            return;
        }

        if (Directory.Exists(path))
        {
            await ViewModel.NavigateToCommand.ExecuteAsync(path);
        }
    }

    private static string ExpandTildePath(string path)
    {
        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home + path[1..];
        }
        return path;
    }
}
