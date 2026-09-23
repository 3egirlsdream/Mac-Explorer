using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Icons = MacExplorer.Assets.Icons;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class FileDeliveryWindow : AppWindow
{
    private readonly FileDeliveryService _delivery;
    private readonly IFileTagService _tags;
    private readonly FileListViewModel _files;
    private readonly Dictionary<string, string[]> _selections = [];
    private string? _entryId;
    private bool _navigating;
    private bool _disposed;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    public bool IsChoosingFolder { get; private set; }
    private ContextMenu? _addEntryMenu;
    private ContextMenu? _entryContextMenu;
    private ContextMenu? _viewModeMenu;
    public bool HasOpenPopup => _addEntryMenu?.IsOpen == true || _entryContextMenu?.IsOpen == true
        || _viewModeMenu?.IsOpen == true || LocationBreadcrumb.HasOpenPopup || Files.HasOpenBrowsePopup;
    public FileListView FileList => Files;
    public event Action? DismissRequested;

    public FileDeliveryWindow(FileDeliveryService delivery, IFileTagService tags, FileListViewModel files)
    {
        InitializeComponent();
        _delivery = delivery;
        _tags = tags;
        _files = files;
        Files.DataContext = files;
        LocationBreadcrumb.DataContext = files;
        Height = Width / Math.Sqrt(2);
        ExtendClientAreaTitleBarHeightHint = 0;
        _files.PropertyChanged += OnFilePropertyChanged;
        _delivery.EntriesChanged += OnEntriesChanged;
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            _disposed = true;
            _files.PropertyChanged -= OnFilePropertyChanged;
            _delivery.EntriesChanged -= OnEntriesChanged;
        };
        RebuildTabs();
    }

    public async Task ResumeAsync()
    {
        _files.SetDirectoryNotificationsPaused(false);
        var entry = _delivery.Preferences.Entries.FirstOrDefault(e => e.Id == (_entryId ?? _delivery.Preferences.SelectedId))
            ?? _delivery.Preferences.Entries.FirstOrDefault();
        if (entry != null) await SelectEntryAsync(entry.Id, refresh: true);
    }

    public void Suspend()
    {
        SavePosition();
        _addEntryMenu?.Close();
        _entryContextMenu?.Close();
        _viewModeMenu?.Close();
        LocationBreadcrumb.CloseTransientUi();
        Files.DeactivateTabInteraction();
        _files.SetDirectoryNotificationsPaused(true);
    }

    private void SavePosition()
    {
        if (_navigating || _entryId == null || string.IsNullOrEmpty(_files.CurrentPath)) return;
        var entry = _delivery.Preferences.Entries.FirstOrDefault(e => e.Id == _entryId);
        if (entry == null || TagPathHelper.IsTagPath(_files.CurrentPath) && _files.CurrentPath != entry.Location) return;
        var state = Files.CaptureDeliveryPosition();
        _delivery.SavePosition(_entryId, _files.CurrentPath, state.Anchor, state.Offset);
        _selections[_entryId] = _files.SelectedEntries.Select(e => e.FullPath).ToArray();
    }

    private async Task SelectEntryAsync(string id, bool refresh = false)
    {
        await _navigationGate.WaitAsync();
        try
        {
            if (_disposed) return;
            SavePosition();
            var entry = _delivery.Preferences.Entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            var switchingEntry = _entryId != id;
            _entryId = id;
            _navigating = true;
            RebuildTabs();
            var path = FileDeliveryService.ResolveLocation(entry);
            if (_files.CurrentPath == path && refresh) await _files.RefreshAsync();
            else await _files.NavigateToAsync(path);
            if (_disposed) return;
            if (switchingEntry) _files.ResetBrowseHistory();
            if (_selections.TryGetValue(id, out var paths))
                _files.SetSelection(_files.Entries.Where(e => paths.Contains(e.FullPath, StringComparer.Ordinal)).ToArray());
            Files.RestoreDeliveryPosition(path, entry.ScrollAnchor, entry.ScrollOffset);
            UpdateToolbar();
        }
        catch (Exception ex) { StatusText.Text = $"无法访问：{ex.Message}，请重试。"; }
        finally { _navigating = false; _navigationGate.Release(); }
    }

    private void RebuildTabs()
    {
        _entryContextMenu?.Close();
        EntryTabs.Children.Clear();
        foreach (var entry in _delivery.Preferences.Entries)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new PathIcon { Data = Geometry.Parse(_files.GetLocationIcon(entry.Location)), Width = 14, Height = 14 });
            content.Children.Add(new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, Classes = { "ghost", "compact", "delivery-tab" } };
            AutomationProperties.SetName(button, entry.Name);
            button.Classes.Set("selected", entry.Id == _entryId);
            ToolTip.SetTip(button, entry.Location);
            button.Click += async (_, _) => await SelectEntryAsync(entry.Id);
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = "删除入口" };
            remove.Click += (_, _) =>
            {
                menu.Close();
                _delivery.Remove(entry.Id);
            };
            menu.Items.Add(remove);
            menu.Opened += (_, _) => _entryContextMenu = menu;
            menu.Closed += (_, _) => { if (_entryContextMenu == menu) _entryContextMenu = null; };
            button.ContextMenu = menu;
            EntryTabs.Children.Add(button);
        }
        EmptyEntries.IsVisible = _delivery.Preferences.Entries.Count == 0;
        Files.IsVisible = !EmptyEntries.IsVisible;
        UpdateToolbar();
    }

    private void OnEntriesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(async () =>
    {
        if (_disposed) return;
        RebuildTabs();
        foreach (var id in _selections.Keys.Where(id => !_delivery.Preferences.Entries.Any(entry => entry.Id == id)).ToArray())
            _selections.Remove(id);
        var entry = _delivery.Preferences.Entries.FirstOrDefault(item => item.Id == _entryId);
        if (entry == null)
        {
            _entryId = null;
            _files.StopDirectoryWork();
            _files.Entries.Clear();
            if (IsVisible) await ResumeAsync();
        }
        else if (IsVisible && TagPathHelper.IsTagPath(_files.CurrentPath)
                 && entry.Location != _files.CurrentPath) await SelectEntryAsync(entry.Id);
    });

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileListViewModel.CurrentPath) or nameof(FileListViewModel.StatusText)
            or nameof(FileListViewModel.CanGoBack) or nameof(FileListViewModel.CanGoForward) or nameof(FileListViewModel.ViewMode)) UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        if (_files == null) return;
        LocationBreadcrumb.IsVisible = !EmptyEntries.IsVisible;
        BackButton.IsEnabled = _files.CanGoBack;
        ForwardButton.IsEnabled = _files.CanGoForward;
        StatusText.Text = EmptyEntries.IsVisible ? "" : string.IsNullOrEmpty(_files.StatusText) ? "选择文件，拖到其他软件即可使用" : _files.StatusText;
        ViewModeIcon.Data = Geometry.Parse(_files.ViewMode switch
        {
            ViewMode.Grid => Icons.Grid,
            ViewMode.Tree => Icons.ListTree,
            _ => Icons.List
        });
    }

    private async void OnBack(object? sender, RoutedEventArgs e) => await _files.NavigateBackAsync();
    private async void OnUp(object? sender, RoutedEventArgs e) => await _files.NavigateUpAsync();
    private async void OnForward(object? sender, RoutedEventArgs e) => await _files.NavigateForwardAsync();
    private void OnViewMode(object? sender, RoutedEventArgs e)
    {
        if (_viewModeMenu?.IsOpen == true) { _viewModeMenu.Close(); return; }
        var menu = new ContextMenu
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            VerticalOffset = -10,
            HorizontalOffset = 12
        };
        AddMode("图标视图", Icons.Grid, ViewMode.Grid);
        AddMode("列表视图", Icons.List, ViewMode.List);
        AddMode("树形列表", Icons.ListTree, ViewMode.Tree);
        _viewModeMenu = menu;
        ViewModeButton.ContextMenu = menu;
        menu.Open(ViewModeButton);

        void AddMode(string title, string icon, ViewMode mode)
        {
            var item = new MenuItem
            {
                Header = title,
                Icon = new PathIcon { Data = Geometry.Parse(icon), Width = 16, Height = 16 },
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = _files.ViewMode == mode
            };
            item.Click += (_, _) => _files.SetViewMode(mode);
            menu.Items.Add(item);
        }
    }

    private async void OnAddEntry(object? sender, RoutedEventArgs e)
    {
        if (_addEntryMenu?.IsOpen == true) { _addEntryMenu.Close(); return; }
        var folders = new MenuItem { Header = "添加文件夹" };
        folders.Click += OnAddFolder;
        var favorites = new MenuItem { Header = "添加收藏夹" };
        favorites.Items.Add(new MenuItem { Header = "正在读取…", IsEnabled = false });
        ContextMenuPopupStyler.Attach(favorites);
        // The shared popup surface reserves 12px for its shadow; leave 2px of visible gap.
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight, VerticalOffset = -10, HorizontalOffset = 12 };
        menu.Items.Add(folders);
        menu.Items.Add(favorites);
        _addEntryMenu = menu;
        AddEntryButton.ContextMenu = menu;
        menu.Open(AddEntryButton);
        try
        {
            var tags = await _tags.GetSidebarTagsAsync();
            if (_disposed || _addEntryMenu != menu) return;
            favorites.Items.Clear();
            foreach (var tag in tags)
            {
                var item = new MenuItem { Header = tag.Name };
                item.Click += (_, _) => _delivery.AddTag(tag);
                favorites.Items.Add(item);
            }
            if (favorites.Items.Count == 0)
                favorites.Items.Add(new MenuItem { Header = "暂无收藏夹", IsEnabled = false });
        }
        catch (Exception ex)
        {
            favorites.Items.Clear();
            favorites.Items.Add(new MenuItem { Header = "无法读取收藏夹", IsEnabled = false });
            StatusText.Text = ex.Message;
        }
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        IsChoosingFolder = true;
        try
        {
            var start = Path.IsPathFullyQualified(_files.CurrentPath) && Directory.Exists(_files.CurrentPath)
                ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(_files.CurrentPath)) : null;
            var folders = await StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = "添加文件速递入口", AllowMultiple = true, SuggestedStartLocation = start
            });
            foreach (var folder in folders) _delivery.AddFolder(folder.Path.LocalPath);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { IsChoosingFolder = false; }
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_addEntryMenu?.IsOpen == true) _addEntryMenu.Close();
        else if (_viewModeMenu?.IsOpen == true) _viewModeMenu.Close();
        else if (LocationBreadcrumb.TryCloseTransientUi()) { }
        else if (_entryContextMenu?.IsOpen == true) _entryContextMenu.Close();
        else if (!Files.DismissBrowsePopup()) DismissRequested?.Invoke();
        e.Handled = true;
    }
}
