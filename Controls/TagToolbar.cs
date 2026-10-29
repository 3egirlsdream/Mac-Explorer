using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Controls;

public sealed class TagToolbar : UserControl
{
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Button _overflow = new() { Width = 28, Height = 30, Classes = { "ghost", "compact", "toolbar-btn", "tag-toolbar-more" }, Content = new PathIcon { Data = Geometry.Parse(Icons.MoreHorizontal), Classes = { "toolbar-icon" } } };
    private readonly List<(FileTagNode Node, Control Control, double Width)> _items = [];
    private FileListViewModel? _viewModel;
    private ContextMenu? _menu;
    private Control? _menuTarget;
    private bool _attached;
    private bool _rebuildQueued;
    private bool _navigating;
    private IDisposable? _openDelay;
    internal Func<Action, TimeSpan, IDisposable> ScheduleHover { get; set; } = (action, delay) => DispatcherTimer.RunOnce(action, delay);
    private IReadOnlyList<FileTagNode> _hidden = [];
    public event Action? Opening;
    public event Action? NewTagRequested;
    public event Action<FileTag>? RenameTagRequested;
    public event Action<FileTag>? CreateChildRequested;
    private bool _actionsOpen;
    internal IReadOnlyList<FileTagNode> OverflowTags => _hidden;
    internal ContextMenu? ActiveMenu => _menu;

    public TagToolbar()
    {
        ClipToBounds = true; MinWidth = 0; Height = 30;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(_strip); Grid.SetColumn(_overflow, 1); grid.Children.Add(_overflow);
        Content = grid;
        _overflow.Click += (_, _) => OpenMenu(_overflow, _hidden, LocalizationText.Get("收藏夹"));
        SizeChanged += (_, _) => Reflow();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => { _attached = true; Subscribe(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; Unsubscribe(); CloseDropdown(); };
    }

    private void Unsubscribe()
    {
        if (_viewModel == null) return;
        _viewModel.SidebarTags.CollectionChanged -= OnTagsChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        if (LocalizationService.Current is { } language) language.LanguageChanged -= OnLanguageChanged;
        _viewModel = null;
    }
    private void Subscribe()
    {
        Unsubscribe();
        if (!_attached) return;
        _viewModel = DataContext as FileListViewModel;
        if (_viewModel != null)
        {
            _viewModel.SidebarTags.CollectionChanged += OnTagsChanged;
            _viewModel.PropertyChanged += OnViewModelChanged;
            if (LocalizationService.Current is { } language) language.LanguageChanged += OnLanguageChanged;
        }
        Rebuild();
    }
    private void OnTagsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() => { _rebuildQueued = false; if (_attached) Rebuild(); });
    }
    private void OnLanguageChanged() => Rebuild();
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileListViewModel.CurrentPath) or nameof(FileListViewModel.ShowToolbarTags)) Rebuild();
    }
    private void Rebuild()
    {
        CloseDropdown();
        _items.Clear(); _strip.Children.Clear();
        IsVisible = _viewModel?.ShowToolbarTags == true;
        ToolTip.SetTip(_overflow, LocalizationText.Get("更多标签"));
        AutomationProperties.SetName(_overflow, LocalizationText.Get("更多标签"));
        if (_viewModel == null) return;
        foreach (var node in FileTagTree.Build(_viewModel.SidebarTags, hideEmpty: true))
        {
            var text = AppTypography.BindFontSize(new TextBlock { Text = node.Tag.DisplayName,
                MaxWidth = 128, TextTrimming = TextTrimming.CharacterEllipsis }, AppTypography.Label);
            text.Measure(new Size(double.PositiveInfinity, 30));
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { TagIcon(node.Tag, toolbar: true), text } };
            var name = new Button { Content = content, Width = Math.Clamp(text.DesiredSize.Width + 35, 52, 163), Height = 28,
                VerticalAlignment = VerticalAlignment.Center, Classes = { "ghost", "compact", "toolbar-btn", "tag-toolbar-name" }, Tag = node.Tag };
            ToolTip.SetTip(name, node.Tag.DisplayName); AutomationProperties.SetName(name, node.Tag.DisplayName);
            name.Click += async (_, _) => await NavigateAsync(node.Tag);
            AttachActions(name, node.Tag);
            if (node.Children.Count > 0)
            {
                AutomationProperties.SetHelpText(name, LocalizationText.Get("悬停查看子标签"));
                name.PointerEntered += (_, _) =>
                {
                    CancelHover();
                    if (_actionsOpen) return;
                    if (_menu?.IsOpen == true && _menuTarget == name) return;
                    _openDelay = ScheduleHover(() => OpenMenu(name, node.Children, node.Tag.DisplayName), TimeSpan.FromMilliseconds(250));
                };
                name.PointerExited += (_, _) => CancelHover();
                name.KeyDown += (_, e) =>
                {
                    if (e.Key is not (Key.Down or Key.Right)) return;
                    OpenMenu(name, node.Children, node.Tag.DisplayName); e.Handled = true;
                    _menu?.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag is FileTag)?.Focus();
                };
            }
            var surface = new Border { Child = name, Classes = { "tag-toolbar-item" } };
            surface.Classes.Set("active", _viewModel.CurrentTag?.Name == node.Tag.Name);
            _strip.Children.Add(surface);
            _items.Add((node, surface, name.Width));
        }
        Reflow();
    }

    private static PathIcon TagIcon(FileTag tag, bool toolbar = false)
    {
        var geometry = tag.IsFinderColor ? Icons.Tag : Icons.Folder;
        var icon = toolbar
            ? new PathIcon { Data = Geometry.Parse(geometry), Classes = { "toolbar-icon" } }
            : TagTreePanel.Icon(geometry, 16);
        if (tag.ColorId != 0) icon.Foreground = Brush.Parse(tag.ColorHex);
        return icon;
    }

    private void Reflow()
    {
        var width = Math.Max(0, Bounds.Width);
        var total = _items.Sum(i => i.Width) + Math.Max(0, _items.Count - 1) * 2;
        var remaining = width - (total > width ? 30 : 0);
        var visible = 0;
        foreach (var item in _items)
        {
            if (item.Width > remaining) break;
            remaining -= item.Width + 2; visible++;
        }
        for (var i = 0; i < _items.Count; i++) _items[i].Control.IsVisible = i < visible;
        _hidden = _items.Skip(visible).Select(i => i.Node).ToArray();
        _overflow.IsVisible = _hidden.Count > 0;
    }

    private void OpenMenu(Control target, IReadOnlyList<FileTagNode> nodes, string title)
    {
        if (_viewModel == null || _actionsOpen || nodes.Count == 0 || _menu?.IsOpen == true && _menuTarget == target) return;
        Opening?.Invoke();
        CloseDropdown();
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft, MinWidth = 240 };
        var header = new TagMenuHeader(title, () => { CloseDropdown(); NewTagRequested?.Invoke(); });
        menu.Items.Add(new MenuItem { Focusable = false, StaysOpenOnClick = true,
            Template = new FuncControlTemplate<MenuItem>((_, _) => header) });
        menu.Items.Add(new Separator());
        foreach (var node in nodes) menu.Items.Add(CreateMenuItem(node));
        _menu = menu; _menuTarget = target;
        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) { _menu = null; _menuTarget = null; } };
        menu.Open(target);
    }

    private MenuItem CreateMenuItem(FileTagNode node)
    {
        var item = new MenuItem { Header = new TextBlock { Text = node.Tag.DisplayName, MaxWidth = 280,
            TextTrimming = TextTrimming.CharacterEllipsis }, Icon = TagIcon(node.Tag), Tag = node.Tag };
        AutomationProperties.SetName(item, node.Tag.DisplayName); ToolTip.SetTip(item, node.Tag.DisplayName);
        ContextMenuPopupStyler.Attach(item);
        AttachActions(item, node.Tag);
        foreach (var child in node.Children) item.Items.Add(CreateMenuItem(child));
        item.Click += async (_, e) => { if (ReferenceEquals(e.Source, item)) await NavigateAsync(node.Tag); };
        // A parent remains a navigable tag while the standard menu owns its hover cascade.
        item.AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || !IsInside(e.Source as Visual, item)) return;
            e.Handled = true; await NavigateAsync(node.Tag);
        }, RoutingStrategies.Tunnel);
        item.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.Enter || !ReferenceEquals(e.Source, item)) return;
            e.Handled = true; await NavigateAsync(node.Tag);
        }, RoutingStrategies.Tunnel);
        return item;
    }

    private void AttachActions(Control control, FileTag tag)
    {
        control.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(control).Properties.IsRightButtonPressed) return;
            if (control is MenuItem && !ReferenceEquals(e.Source as MenuItem
                ?? (e.Source as Visual)?.FindAncestorOfType<MenuItem>(), control)) return;
            e.Handled = true;
            CancelHover();
        }, RoutingStrategies.Tunnel);
        control.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            if (control is MenuItem && !ReferenceEquals(e.Source as MenuItem
                ?? (e.Source as Visual)?.FindAncestorOfType<MenuItem>(), control)) return;
            e.Handled = true;
            OpenActions(tag);
        }, RoutingStrategies.Tunnel);
        control.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Apps && !(e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)) return;
            e.Handled = true; OpenActions(tag);
        }, RoutingStrategies.Tunnel);
    }

    private void OpenActions(FileTag tag)
    {
        Opening?.Invoke();
        CloseDropdown();
        if (_viewModel == null) return;
        tag = _viewModel.ResolveTag(tag);
        var menu = new ContextMenu();
        void Add(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, e) => { e.Handled = true; CloseDropdown(); action(); };
            menu.Items.Add(item);
        }
        Add(LocalizationText.Get("重命名"), () => RenameTagRequested?.Invoke(tag));
        Add(LocalizationText.Get("删除"), () => _viewModel?.ShowTagDeleteConfirmDialog(tag), tag.IsCustom);
        Add(LocalizationText.Get("tags.add_child_collection"), () => CreateChildRequested?.Invoke(tag));
        _menu = menu; _menuTarget = this; _actionsOpen = true;
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_menu, menu)) return;
            _menu = null; _menuTarget = null; _actionsOpen = false;
        };
        // A submenu's popup is already closed; anchor to the live toolbar, at the pointer.
        menu.Open(this);
    }

    private async Task NavigateAsync(FileTag tag)
    {
        if (_navigating || _viewModel == null) return;
        _navigating = true;
        try { CloseDropdown(); await _viewModel.NavigateToTagAsync(tag); }
        finally { _navigating = false; }
    }
    public bool IsDropdownOpen => _menu?.IsOpen == true;
    private void CancelHover() { _openDelay?.Dispose(); _openDelay = null; }
    public void CloseDropdown()
    {
        CancelHover();
        _actionsOpen = false;
        var menu = _menu; _menu = null; _menuTarget = null;
        menu?.Close();
    }
    public bool ContainsSource(Visual? source) => IsInside(source, this) || IsInside(source, _menu)
        || source is StyledElement element && element.GetLogicalAncestors().Any(parent => ReferenceEquals(parent, _menu));
    private static bool IsInside(Visual? source, Visual? target)
    {
        for (; source != null; source = source.GetVisualParent()) if (source == target) return true;
        return false;
    }
}
