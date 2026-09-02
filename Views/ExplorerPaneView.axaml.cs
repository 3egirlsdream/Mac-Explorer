using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MacExplorer.Models;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class ExplorerPaneView : UserControl
{
    private ExplorerTabViewModel? _tab;

    public event Action<ExplorerTabViewModel>? PaneActivated;

    public ExplorerPaneView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPanePointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public ExplorerTabViewModel? Tab => DataContext as ExplorerTabViewModel;
    public FileListView FileListView => FileListControl;

    public void SetHeaderVisible(bool visible) => PaneHeader.IsVisible = visible;
    public void RefreshState() => UpdateState();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_tab != null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab.FileList.PropertyChanged -= OnFileListPropertyChanged;
        }

        base.OnDataContextChanged(e);
        _tab = Tab;
        if (_tab != null)
        {
            _tab.PropertyChanged += OnTabPropertyChanged;
            _tab.FileList.PropertyChanged += OnFileListPropertyChanged;
        }
        UpdateState();
    }

    private void OnPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_tab != null)
            PaneActivated?.Invoke(_tab);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerTabViewModel.IsActive))
            UpdateActiveState();
    }

    private void OnFileListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileListViewModel.IsHomePage)
            or nameof(FileListViewModel.IsAiView)
            or nameof(FileListViewModel.AiViewMode))
            UpdateContentVisibility();
    }

    private void UpdateState()
    {
        UpdateActiveState();
        UpdateContentVisibility();
    }

    private void UpdateActiveState()
        => PaneSurface.Classes.Set("active", _tab?.IsActive == true);

    private void UpdateContentVisibility()
    {
        var fileList = _tab?.FileList;
        var showAiSearch = fileList?.IsAiView == true && fileList.AiViewMode == AiViewMode.TextSearch;
        FileListControl.IsVisible = fileList != null && !fileList.IsHomePage && !showAiSearch;
        HomeViewControl.IsVisible = fileList?.IsHomePage == true;
        AiViewControl.IsVisible = showAiSearch;
    }

    private async void NavigateBack(object? sender, RoutedEventArgs e)
    {
        if (_tab?.FileList.CanGoBack == true)
            await _tab.FileList.NavigateBackAsync();
    }

    private async void NavigateForward(object? sender, RoutedEventArgs e)
    {
        if (_tab?.FileList.CanGoForward == true)
            await _tab.FileList.NavigateForwardAsync();
    }

    private async void NavigateUp(object? sender, RoutedEventArgs e)
    {
        if (_tab?.FileList != null)
            await _tab.FileList.NavigateUpAsync();
    }
}
