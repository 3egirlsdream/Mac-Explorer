using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;
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
    private BreadcrumbOverflowPanel? _overflowPanel;
    private int _hiddenBreadcrumbCount;
    private string? _suggestionInput;
    private bool _executingApplication;
    internal ApplicationOpenCommandService? ApplicationCommands { get; set; }
    private ApplicationOpenCommandService? GetApplicationCommands()
        => ApplicationCommands ??= App.Services?.GetService<ApplicationOpenCommandService>();

    public BreadcrumbBar()
    {
        InitializeComponent();
        DirectoryDropdownList.AddHandler(KeyDownEvent, OnDirectoryContentKeyDown, RoutingStrategies.Tunnel);
        DirectoryDropdownSearch.AddHandler(KeyDownEvent, OnDirectoryContentKeyDown, RoutingStrategies.Tunnel);
        HiddenBreadcrumbList.AddHandler(KeyDownEvent, OnHiddenBreadcrumbKeyDown, RoutingStrategies.Tunnel);
        LayoutUpdated += OnLayoutUpdated;
        DataContextChanged += (_, _) =>
        {
            CloseTransientUi();
            ObserveViewModel(TopLevel.GetTopLevel(this) != null ? ViewModel : null);
        };
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    public bool HasOpenPopup => DirectoryDropdownPopup.IsOpen || HiddenBreadcrumbPopup.IsOpen || PathSuggestionsPopup.IsOpen;

    public void FocusPathInput()
    {
        if (ViewModel?.IsNetworkPage == true || ViewModel?.IsDeliveryStaging == true) return;
        CloseDirectoryDropdown();
        CloseHiddenBreadcrumbs();
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
        var hadHiddenBreadcrumbs = HiddenBreadcrumbPopup.IsOpen;
        CloseDirectoryDropdown();
        CloseHiddenBreadcrumbs();
        if (!hadDirectoryDropdown && !hadHiddenBreadcrumbs && !PathSuggestionsPopup.IsOpen
            && _activeInput == null && !PathInput.IsVisible)
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
                || ReferenceEquals(ancestor, HiddenBreadcrumbPopup.Child)
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

    private void OnBrowseModePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!BrowseModePanel.IsVisible || ViewModel?.IsHomePage == true
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
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

        e.Handled = true;
        var suggestion = PathSuggestionList.SelectedItem as OmniboxSuggestion;
        var explicitApplication = ApplicationOpenCommandService.TryParse(input.Text, out _);
        if ((explicitApplication || suggestion?.Kind == OmniboxSuggestionKind.Application
                || (suggestion == null && !OmniboxService.IsNavigablePath(OmniboxService.NormalizePath(input.Text.Trim()))))
            && await ExecuteApplicationInputAsync(input,
                explicitApplication || suggestion?.Kind == OmniboxSuggestionKind.Application))
        {
            e.Handled = true;
            return;
        }

        if (suggestion == null && !OmniboxService.IsNavigablePath(OmniboxService.NormalizePath(input.Text.Trim())))
            suggestion = (PathSuggestionList.ItemsSource as IEnumerable<OmniboxSuggestion>)?.FirstOrDefault();
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

    private async Task<bool> ExecuteApplicationInputAsync(TextBox input, bool explicitCommand = true)
    {
        if (_executingApplication) return true;
        if (ViewModel == null) return false;
        var text = input.Text!;
        var selected = _suggestionInput == text ? PathSuggestionList.SelectedItem as OmniboxSuggestion : null;
        _executingApplication = true;
        try
        {
            var commands = GetApplicationCommands();
            var suggestions = commands == null ? []
                : await commands.GetSuggestionsAsync(ViewModel, text, CancellationToken.None);
            if (input.Text != text || _activeInput != input) return true;
            if (suggestions.Count == 0 && !explicitCommand) return false;
            if (!ApplicationOpenCommandService.HasLocalDirectory(ViewModel))
            {
                ViewModel.StatusText = LocalizationText.Get("请先进入本地目录");
                return true;
            }
            var target = selected?.Kind == OmniboxSuggestionKind.Application
                ? suggestions.FirstOrDefault(item => item.Value == selected.Value) : null;
            if (target == null && suggestions.Count == 1) target = suggestions[0];
            if (target != null)
                await ExecuteSuggestionAsync(target);
            else
            {
                if (explicitCommand || _suggestionInput != text)
                    ShowSuggestions(suggestions, text);
                ViewModel.StatusText = LocalizationText.Get(suggestions.Count == 0
                    ? "未找到匹配的应用" : "请选择要打开的应用");
            }
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = LocalizationText.Get("打开应用失败：{0}", ex.Message);
            return true;
        }
        finally { _executingApplication = false; }
    }

    internal async Task<IReadOnlyList<OmniboxSuggestion>> GetInputSuggestionsAsync(
        string? input, CancellationToken cancellationToken)
    {
        if (ViewModel == null) return [];
        var commands = GetApplicationCommands();
        var applicationTask = commands == null
            ? Task.FromResult<IReadOnlyList<OmniboxSuggestion>>([])
            : commands.GetSuggestionsAsync(ViewModel, input ?? string.Empty, cancellationToken);
        if (ApplicationOpenCommandService.TryParse(input, out _))
            return await applicationTask;
        var fileTask = OmniboxService.GetSuggestionsAsync(ViewModel, input, cancellationToken);
        await Task.WhenAll(applicationTask, fileTask);
        return [.. await applicationTask, .. await fileTask];
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
        {
            if (suggestion.Kind == OmniboxSuggestionKind.Application && _activeInput != null)
            {
                PathSuggestionList.SelectedItem = suggestion;
                await ExecuteApplicationInputAsync(_activeInput);
            }
            else
                await ExecuteSuggestionAsync(suggestion);
        }
        e.Handled = true;
    }

    private async Task ExecuteSuggestionAsync(OmniboxSuggestion suggestion)
    {
        if (ViewModel == null)
            return;

        var input = _activeInput;
        var text = input?.Text;
        try
        {
            await OmniboxService.ExecuteAsync(ViewModel, suggestion);
        }
        catch (Exception ex) when (suggestion.Kind == OmniboxSuggestionKind.Application
            || ApplicationOpenCommandService.TryParse(text, out _))
        {
            ViewModel.StatusText = LocalizationText.Get("打开应用失败：{0}", ex.Message);
            return;
        }
        if (input != _activeInput || input?.Text != text) return;
        CloseSuggestions();
        if (_activeInput == HomePathInput)
            HomePathInput.Text = string.Empty;
        else
            EndPathEditing();
    }

    private async Task RefreshSuggestionsAsync(string? value)
    {
        _suggestionCancellation?.Cancel();
        PathSuggestionList.SelectedIndex = -1;
        PathSuggestionList.ItemsSource = null;
        PathSuggestionsPopup.IsOpen = false;
        _suggestionInput = null;
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = new CancellationTokenSource();
        var token = _suggestionCancellation.Token;

        if (ViewModel == null)
            return;

        IReadOnlyList<OmniboxSuggestion> suggestions;
        try
        {
            suggestions = await GetInputSuggestionsAsync(value, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                ViewModel.StatusText = LocalizationText.Get("打开应用失败：{0}", ex.Message);
            return;
        }

        if (token.IsCancellationRequested || _activeInput == null || _activeInput.Text != value)
            return;

        ShowSuggestions(suggestions, value);
    }

    private void ShowSuggestions(IReadOnlyList<OmniboxSuggestion> suggestions, string? input)
    {
        if (_activeInput == null) return;
        _suggestionInput = input;
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

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        _overflowPanel ??= BrowseModePanel.GetVisualDescendants().OfType<BreadcrumbOverflowPanel>().FirstOrDefault();
        if (_overflowPanel == null || _hiddenBreadcrumbCount == _overflowPanel.HiddenCount)
            return;

        _hiddenBreadcrumbCount = _overflowPanel.HiddenCount;
        OverflowButton.IsVisible = _hiddenBreadcrumbCount > 0 && BrowseModePanel.IsVisible;
        if (_hiddenBreadcrumbCount == 0)
            CloseHiddenBreadcrumbs();
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
        {
            CloseDirectoryDropdown();
            CloseHiddenBreadcrumbs();
        }
    }

    private void OnOverflowClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (HiddenBreadcrumbPopup.IsOpen)
        {
            CloseHiddenBreadcrumbs();
            return;
        }

        var breadcrumbs = ViewModel?.Breadcrumbs;
        if (breadcrumbs == null || _hiddenBreadcrumbCount == 0)
            return;

        CloseDirectoryDropdown();
        HiddenBreadcrumbList.ItemsSource = breadcrumbs.Take(_hiddenBreadcrumbCount).ToArray();
        HiddenBreadcrumbList.SelectedIndex = -1;
        HiddenBreadcrumbPopup.PlacementTarget = OverflowButton;
        HiddenBreadcrumbPopup.OverlayInputPassThroughElement = BrowseModePanel;
        HiddenBreadcrumbPopup.OverlayDismissEventPassThrough = true;
        HiddenBreadcrumbPopup.IsOpen = true;
        HiddenBreadcrumbList.Focus();
    }

    private async void OnHiddenBreadcrumbTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext
                is not BreadcrumbSegment segment)
            return;

        e.Handled = true;
        CloseHiddenBreadcrumbs();
        await NavigateToDirectoryAsync(segment);
    }

    private async void OnHiddenBreadcrumbKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseHiddenBreadcrumbs();
            OverflowButton.Focus();
        }
        else if (e.Key == Key.Enter && HiddenBreadcrumbList.SelectedItem is BreadcrumbSegment segment)
        {
            e.Handled = true;
            CloseHiddenBreadcrumbs();
            await NavigateToDirectoryAsync(segment);
        }
    }

    private void CloseHiddenBreadcrumbs()
    {
        HiddenBreadcrumbPopup.IsOpen = false;
        HiddenBreadcrumbList.ItemsSource = null;
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
        DirectoryDropdownStatus.Text = MacExplorer.Services.Impl.LocalizationText.Get("正在加载…");
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
                DirectoryDropdownStatus.Text = MacExplorer.Services.Impl.LocalizationText.Get("无法读取此目录");
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
        DirectoryDropdownStatus.Text = _directoryEntries.Count == 0 ? MacExplorer.Services.Impl.LocalizationText.Get("没有子目录") : MacExplorer.Services.Impl.LocalizationText.Get("没有匹配的目录");
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
