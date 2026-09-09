using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class BreadcrumbBar : UserControl
{
    private CancellationTokenSource? _suggestionCancellation;
    private TextBox? _activeInput;
    private CancellationTokenSource? _directoryCancellation;
    private Button? _directoryButton;
    private IReadOnlyList<BreadcrumbSegment>? _directoryEntries;
    private FileListViewModel? _observedViewModel;

    public BreadcrumbBar()
    {
        InitializeComponent();
        DirectoryDropdownList.AddHandler(KeyDownEvent, OnDirectoryContentKeyDown, RoutingStrategies.Tunnel);
        DirectoryDropdownSearch.AddHandler(KeyDownEvent, OnDirectoryContentKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            CloseTransientUi();
            ObserveViewModel(TopLevel.GetTopLevel(this) != null ? ViewModel : null);
        };
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    public void FocusPathInput()
    {
        CloseDirectoryDropdown();
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

    public bool TryCloseTransientUi()
    {
        var hadDirectoryDropdown = DirectoryDropdownPopup.IsOpen;
        CloseDirectoryDropdown();
        if (!hadDirectoryDropdown && !PathSuggestionsPopup.IsOpen && _activeInput == null && !PathInput.IsVisible)
            return false;

        CloseSuggestions();
        if (PathInput.IsVisible)
            EndPathEditing();
        else
            _activeInput = null;
        return true;
    }

    public void CloseTransientUi() => TryCloseTransientUi();

    public void CloseTransientUiFromPointerSource(object? source)
    {
        if (source is Visual visual && visual.GetSelfAndVisualAncestors().Any(ancestor =>
                ReferenceEquals(ancestor, this)
                || ReferenceEquals(ancestor, DirectoryDropdownPopup.Child)
                || ReferenceEquals(ancestor, PathSuggestionsPopup.Child)))
            return;

        CloseTransientUi();
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
        input.Focus();
        if (selectAll)
            input.SelectAll();
        Dispatcher.UIThread.Post(() =>
        {
            if (_activeInput != input)
                return;

            PathSuggestionsPopup.PlacementTarget = input;
            PathSuggestionsSurface.Width = Math.Max(input.Bounds.Width, input.DesiredSize.Width);
            _ = RefreshSuggestionsAsync(input.Text);
        }, DispatcherPriority.Loaded);
    }

    private void OnBrowseModeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any())
            return;
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

        PathSuggestionsPopup.PlacementTarget = _activeInput;
        PathSuggestionsSurface.Width = Math.Max(_activeInput.Bounds.Width, _activeInput.DesiredSize.Width);
        PathSuggestionList.ItemsSource = suggestions;
        PathSuggestionList.SelectedIndex = -1;
        PathSuggestionsPopup.IsOpen = suggestions.Count > 0 && _activeInput.IsVisible && _activeInput.IsKeyboardFocusWithin;
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveViewModel(ViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CloseTransientUi();
        ObserveViewModel(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void ObserveViewModel(FileListViewModel? viewModel)
    {
        if (_observedViewModel != null)
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _observedViewModel = viewModel;
        if (_observedViewModel != null)
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileListViewModel.CurrentPath)
            or nameof(FileListViewModel.Breadcrumbs) or nameof(FileListViewModel.IsHomePage))
            CloseDirectoryDropdown();
    }

    private async void OnDirectoryDropdownClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button)
            return;

        if (DirectoryDropdownPopup.IsOpen && _directoryButton == button)
            CloseDirectoryDropdown();
        else
            await OpenDirectoryDropdownAsync(button);
    }

    private async Task OpenDirectoryDropdownAsync(Button button)
    {
        if (ViewModel is not { } viewModel || button.DataContext is not BreadcrumbSegment { HasDropdown: true } segment)
            return;

        CloseDirectoryDropdown();
        CloseSuggestions();
        _directoryCancellation = new CancellationTokenSource();
        var token = _directoryCancellation.Token;
        _directoryButton = button;
        button.Classes.Add("open");
        DirectoryDropdownSearch.Text = string.Empty;
        DirectoryDropdownList.ItemsSource = null;
        DirectoryDropdownList.SelectedIndex = -1;
        DirectoryDropdownList.IsVisible = false;
        DirectoryDropdownStatus.Text = "正在加载…";
        DirectoryDropdownStatus.IsVisible = true;
        DirectoryDropdownPopup.PlacementTarget = button;
        DirectoryDropdownPopup.OverlayInputPassThroughElement = BrowseModePanel;
        DirectoryDropdownPopup.OverlayDismissEventPassThrough = true;
        DirectoryDropdownPopup.IsOpen = true;
        DirectoryDropdownSearch.Focus();

        try
        {
            var directories = await viewModel.GetBreadcrumbDirectoriesAsync(segment.FullPath, token);
            if (token.IsCancellationRequested)
                return;

            _directoryEntries = directories;
            ApplyDirectoryFilter();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
                DirectoryDropdownStatus.Text = "无法读取此目录";
        }
    }

    private void OnDirectorySearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (DirectoryDropdownPopup.IsOpen)
            ApplyDirectoryFilter();
    }

    private void ApplyDirectoryFilter()
    {
        if (_directoryEntries == null)
            return;

        var query = DirectoryDropdownSearch.Text?.Trim() ?? string.Empty;
        var directories = query.Length == 0
            ? _directoryEntries
            : _directoryEntries.Where(directory =>
                directory.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || directory.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        DirectoryDropdownList.ItemsSource = directories;
        DirectoryDropdownList.SelectedIndex = -1;
        DirectoryDropdownList.IsVisible = directories.Count > 0;
        DirectoryDropdownStatus.Text = _directoryEntries.Count == 0 ? "没有子目录" : "没有匹配的目录";
        DirectoryDropdownStatus.IsVisible = directories.Count == 0;
        if (directories.Count > 0)
            DirectoryDropdownList.ScrollIntoView(directories[0]);
    }

    private async void OnDropdownKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && sender is Button button)
        {
            e.Handled = true;
            await OpenDirectoryDropdownAsync(button);
        }
        else if (e.Key == Key.Escape && DirectoryDropdownPopup.IsOpen)
        {
            e.Handled = true;
            CloseDirectoryDropdown();
        }
    }

    private async void OnDirectoryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext
                is not BreadcrumbSegment directory)
            return;

        e.Handled = true;
        await NavigateToDirectoryAsync(directory);
    }

    private async void OnDirectoryContentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            var button = _directoryButton;
            CloseDirectoryDropdown();
            button?.Focus();
        }
        else if (sender == DirectoryDropdownSearch && e.Key == Key.Down)
        {
            e.Handled = true;
            if (DirectoryDropdownList.ItemCount > 0)
            {
                DirectoryDropdownList.SelectedIndex = 0;
                DirectoryDropdownList.Focus();
                DirectoryDropdownList.ScrollIntoView(DirectoryDropdownList.SelectedItem!);
            }
        }
        else if (e.Key == Key.Enter && DirectoryDropdownList.SelectedItem is BreadcrumbSegment directory)
        {
            e.Handled = true;
            await NavigateToDirectoryAsync(directory);
        }
    }

    private async Task NavigateToDirectoryAsync(BreadcrumbSegment directory)
    {
        var viewModel = ViewModel;
        CloseDirectoryDropdown();
        if (viewModel != null)
            await viewModel.NavigateToAsync(directory.FullPath);
    }

    private void OnDirectoryDropdownClosed(object? sender, EventArgs e) => CloseDirectoryDropdown();

    private void CloseDirectoryDropdown()
    {
        _directoryCancellation?.Cancel();
        _directoryCancellation?.Dispose();
        _directoryCancellation = null;
        _directoryButton?.Classes.Remove("open");
        _directoryButton = null;
        _directoryEntries = null;
        DirectoryDropdownPopup.IsOpen = false;
        DirectoryDropdownList.ItemsSource = null;
    }

    private async void OnSegmentClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseDirectoryDropdown();
        if (ViewModel == null || sender is not Button { Tag: string path })
            return;

        if (VirtualPath.IsHomePath(path)
            || VirtualPath.IsRemotePath(path)
            || ArchivePathHelper.IsArchivePath(path)
            || Directory.Exists(path))
            await ViewModel.NavigateToAsync(path);
    }
}
