using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Controls;

// One tree serves file assignment and tag management.
public sealed class TagTreePanel : UserControl
{
    internal static readonly DataFormat<string> TagDragFormat = DataFormat.CreateStringApplicationFormat("macexplorer-tag");
    private readonly FileListViewModel _viewModel;
    private readonly IReadOnlyList<string>? _filePaths;
    private readonly bool _hideEmpty;
    private readonly string? _parentName;
    private readonly IReadOnlySet<string>? _rootNames;
    private readonly TagMenuHeader _header;
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Classes = { "tag-tree-error" }, IsVisible = false };
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _nameButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _membership = new(StringComparer.OrdinalIgnoreCase);
    private FileTag[] _tags = [];
    private TextBox? _editor;
    private FileTag? _renaming;
    private string? _newParent;
    private bool _busy;
    private bool _attached;
    private bool _refreshQueued;
    private int _loadVersion;
    private Border? _dropRow;
    private DispatcherTimer? _hoverTimer;
    private Point _dragStart;
    private FileTag? _dragTag;
    private PointerPressedEventArgs? _dragPress;

    public event Action? NavigationRequested;
    public event Action? CloseRequested;

    public TagTreePanel(FileListViewModel viewModel, IReadOnlyList<string>? filePaths = null,
        bool hideEmpty = false, string? parentName = null, IReadOnlySet<string>? rootNames = null)
    {
        _viewModel = viewModel; _filePaths = filePaths; _hideEmpty = hideEmpty;
        _parentName = parentName; _rootNames = rootNames;
        MinWidth = 240; MaxWidth = 380;
        var content = new StackPanel { Spacing = 4 };
        _header = new TagMenuHeader(HeaderTitle, () => StartCreate(null));
        content.Children.Add(_header);
        content.Children.Add(new Separator());
        content.Children.Add(_error);
        var scroll = new ScrollViewer { Content = _rows, MaxHeight = 420,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        content.Children.Add(scroll);
        Content = content;
        DragDrop.SetAllowDrop(_rows, true);
        _rows.AddHandler(DragDrop.DragOverEvent, OnRootDragOver);
        _rows.AddHandler(DragDrop.DropEvent, OnRootDrop);
        KeyDown += OnTreeKeyDown;
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            _viewModel.SidebarTags.CollectionChanged += OnTagsChanged;
            if (LocalizationService.Current is { } language) language.LanguageChanged += OnLanguageChanged;
            _ = RefreshAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false; _loadVersion++;
            _viewModel.SidebarTags.CollectionChanged -= OnTagsChanged;
            if (LocalizationService.Current is { } language) language.LanguageChanged -= OnLanguageChanged;
            ClearDropTarget();
        };
    }

    private void OnTagsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() => { _refreshQueued = false; if (_attached) _ = RefreshAsync(); });
    }
    private string HeaderTitle => _parentName == null ? LocalizationText.Get("收藏夹")
        : _tags.FirstOrDefault(t => string.Equals(t.Name, _parentName, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? _parentName;

    private void OnLanguageChanged()
    {
        _header.Update(HeaderTitle);
        _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        try
        {
            var service = _viewModel.FileTagService;
            var tags = service == null ? _viewModel.SidebarTags.ToArray() : (await service.GetSidebarTagsAsync()).ToArray();
            var membership = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (_filePaths != null && service != null)
                foreach (var path in _filePaths)
                    foreach (var tag in await service.GetFileTagsAsync(path))
                        membership[tag.Name] = membership.GetValueOrDefault(tag.Name) + 1;
            if (version != _loadVersion || !_attached) return;
            _tags = tags;
            _header.Update(HeaderTitle);
            _membership.Clear();
            foreach (var pair in membership) _membership[pair.Key] = pair.Value;
            if (_editor == null) RenderRows();
        }
        catch (Exception ex) { if (version == _loadVersion && _attached) ShowError(ex); }
    }

    private void RenderRows()
    {
        ClearDropTarget();
        _rows.Children.Clear(); _nameButtons.Clear();
        var roots = FileTagTree.Build(_tags, _hideEmpty);
        if (_parentName != null) roots = FindNode(roots, _parentName)?.Children ?? [];
        if (_rootNames != null) roots = roots.Where(n => _rootNames.Contains(n.Tag.Name)).ToArray();
        foreach (var node in roots) AddNode(node, 0);
        if (_rows.Children.Count == 0)
            _rows.Children.Add(new TextBlock { Text = LocalizationText.Get("暂无标签"), Margin = new Thickness(8), Classes = { "tag-tree-empty" } });
        // A root-level drop area remains available even when every branch is expanded.
        var rootDrop = new Border { Height = 20, Background = Brushes.Transparent };
        _rows.Children.Add(rootDrop);
    }

    private static FileTagNode? FindNode(IEnumerable<FileTagNode> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Tag.Name, name, StringComparison.OrdinalIgnoreCase)) return node;
            if (FindNode(node.Children, name) is { } child) return child;
        }
        return null;
    }

    private void AddNode(FileTagNode node, int depth)
    {
        var tag = node.Tag;
        var indent = Math.Min(depth * FastFileList.TreeStep, Math.Max(0, (Bounds.Width > 0 ? Bounds.Width : MinWidth) - 140));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*"), Margin = new Thickness(8 + indent, 0, 8, 0) };
        var arrow = new Button { Classes = { "ghost", "compact", "tag-tree-arrow" }, Width = FastFileList.TreeDisclosureWidth, Height = 22,
            Content = Icon(_expanded.Contains(tag.Name) ? Icons.ChevronDown : Icons.ChevronRight, 12),
            VerticalAlignment = VerticalAlignment.Center, IsVisible = node.Children.Count > 0 };
        AutomationProperties.SetName(arrow, LocalizationText.Get("展开或收起标签“{0}”", tag.DisplayName));
        arrow.Click += (_, _) => ToggleExpanded(tag.Name);
        grid.Children.Add(arrow);
        var icon = Icon(Icons.Tag, 14); icon.Foreground = Brush.Parse(tag.ColorHex);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,22") };
        content.Children.Add(icon);
        var text = new TextBlock { Text = tag.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(text, 1); content.Children.Add(text);
        var label = new Button { Classes = { "ghost", "compact", "tag-tree-name" }, Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = tag, Height = FastFileList.RowHeight };
        ToolTip.SetTip(label, tag.DisplayName);
        AutomationProperties.SetName(label, tag.DisplayName);
        Grid.SetColumn(label, 1); grid.Children.Add(label); _nameButtons[tag.Name] = label;
        var count = _membership.GetValueOrDefault(tag.Name);
        var check = Icon(Icons.Checkmark, 12);
        check.Classes.Add("tag-tree-check");
        check.IsVisible = _filePaths is { Count: > 0 } && count == _filePaths.Count;
        Grid.SetColumn(check, 2); content.Children.Add(check);
        var partial = new TextBlock { Text = "−", IsVisible = count > 0 && _filePaths != null && count != _filePaths.Count,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Classes = { "tag-tree-partial" } };
        Grid.SetColumn(partial, 2); content.Children.Add(partial);
        if (_filePaths != null)
            AutomationProperties.SetHelpText(label, LocalizationText.Get(count == 0 ? "未选中" : count == _filePaths.Count ? "已选中" : "部分文件已选中"));
        label.Click += async (_, _) =>
        {
            if (_dragPress == null && _dragTag != null || _busy) return;
            _dragTag = null; _dragPress = null;
            await GuardAsync(async () =>
            {
                if (_filePaths != null && _viewModel.FileTagService is { } service)
                {
                    await service.SetTagAsync(_filePaths, tag, count != _filePaths.Count);
                    await RefreshAsync();
                }
                else { await _viewModel.NavigateToTagAsync(tag); NavigationRequested?.Invoke(); }
            });
        };
        var row = new Border { Child = grid, Tag = tag, Height = FastFileList.RowHeight, Classes = { "tag-tree-row" } };
        row.Classes.Set("active", _filePaths == null && _viewModel.CurrentTag?.Name == tag.Name);
        if (!_hideEmpty) row.ContextMenu = CreateRowMenu(tag);
        DragDrop.SetAllowDrop(row, true);
        row.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(TagDragFormat) != null) return;
            var paths = FileListView.GetDroppedPaths(e.DataTransfer);
            e.Handled = true;
            e.DragEffects = paths.Length > 0 && paths.All(FileTagService.IsSupportedPath) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        row.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(TagDragFormat) != null) return;
            var paths = FileListView.GetDroppedPaths(e.DataTransfer);
            e.Handled = true;
            if (paths.Length == 0 || !paths.All(FileTagService.IsSupportedPath)) { e.DragEffects = DragDropEffects.None; return; }
            await GuardAsync(async () => { await _viewModel.FileTagService!.SetTagAsync(paths, tag, true); await RefreshAsync(); });
        });
        if (!_hideEmpty)
        {
            row.AddHandler(DragDrop.DragOverEvent, (_, e) => { if (!e.Handled) OnRowDragOver(row, node, e); });
            row.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropTarget());
            row.AddHandler(DragDrop.DropEvent, (_, e) => { if (!e.Handled) OnRowDrop(row, tag, e); });
            row.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed && e.Source is not TextBox)
                { _dragStart = e.GetPosition(row); _dragTag = tag; _dragPress = e; }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            row.PointerReleased += (_, _) => { _dragTag = null; _dragPress = null; };
            row.PointerMoved += async (_, e) =>
            {
                if (_dragPress == null || _dragTag != tag || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed
                    || Math.Abs(e.GetPosition(row).X - _dragStart.X) + Math.Abs(e.GetPosition(row).Y - _dragStart.Y) < 6) return;
                var press = _dragPress; _dragPress = null;
                var data = new DataTransfer(); var item = new DataTransferItem(); item.Set(TagDragFormat, tag.Name); data.Add(item);
                try { await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move); }
                finally { _dragTag = null; ClearDropTarget(); }
            };
        }
        _rows.Children.Add(row);
        if (_expanded.Contains(tag.Name)) foreach (var child in node.Children) AddNode(child, depth + 1);
    }

    private void ToggleExpanded(string name)
    {
        if (!_expanded.Remove(name)) _expanded.Add(name);
        RenderRows();
        if (_nameButtons.TryGetValue(name, out var button)) button.Focus();
    }

    private ContextMenu CreateRowMenu(FileTag tag)
    {
        var menu = new ContextMenu();
        void Add(string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = LocalizationText.Get(text), IsEnabled = enabled };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("添加子标签", () => StartCreate(tag.Name));
        Add("重命名", () => StartEdit(tag), tag.IsCustom);
        Add(tag.IsPinned ? "从首页取消置顶" : "在首页置顶", () => _ = GuardAsync(() => _viewModel.SetTagPinnedAsync(tag, !tag.IsPinned)));
        Add("删除", () => { CloseRequested?.Invoke(); _viewModel.ShowTagDeleteConfirmDialog(tag); }, tag.IsCustom);
        return menu;
    }

    internal void BeginCreateTag() => StartCreate(null);

    private void StartCreate(string? parentName) { _newParent = parentName; _renaming = null; StartEditor(LocalizationText.Get("新标签")); }
    private void StartEdit(FileTag tag) { _renaming = tag; _newParent = tag.ParentName; StartEditor(tag.Name); }
    private void StartEditor(string text)
    {
        if (_busy) return;
        _editor = new TextBox { Text = text, Classes = { "settings-inline-input" }, Height = 30,
            Margin = new Thickness(24, 0, 4, 0) };
        AutomationProperties.SetName(_editor, LocalizationText.Get(_renaming == null ? "新标签" : "重命名"));
        _editor.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; EndEdit(); }
            else if (e.Key == Key.Enter) { e.Handled = true; await CommitEditAsync(); }
        };
        RenderRows();
        var editorRow = new Border { Child = _editor, Classes = { "tag-tree-row" } };
        var insertAt = 0;
        if (_renaming != null)
        {
            var existing = _rows.Children.OfType<Border>().FirstOrDefault(b => b.Tag is FileTag t && t.Name == _renaming.Name);
            if (existing != null) { insertAt = _rows.Children.IndexOf(existing); _rows.Children.Remove(existing); }
        }
        else if (_newParent != null)
        {
            _expanded.Add(_newParent); RenderRows();
            var parentRow = _rows.Children.OfType<Border>().FirstOrDefault(b => b.Tag is FileTag t && t.Name == _newParent);
            if (parentRow != null) insertAt = _rows.Children.IndexOf(parentRow) + 1;
        }
        _rows.Children.Insert(insertAt, editorRow);
        Dispatcher.UIThread.Post(() => { _editor?.Focus(); _editor?.SelectAll(); });
    }

    private async Task CommitEditAsync()
    {
        var name = _editor?.Text?.Trim();
        if (_busy || string.IsNullOrEmpty(name)) return;
        await GuardAsync(async () =>
        {
            var service = _viewModel.FileTagService;
            if (service == null) return;
            if (_renaming != null) await service.RenameTagAsync(_renaming, name);
            else
            {
                var tag = await service.CreateChildTagAsync(name, _newParent);
                if (_filePaths is { Count: > 0 }) await service.SetTagAsync(_filePaths, tag, true);
            }
            _editor = null; _renaming = null; _newParent = null;
            await RefreshAsync();
        });
    }

    private void EndEdit() { _editor = null; _renaming = null; _newParent = null; _error.IsVisible = false; RenderRows(); }
    private async Task GuardAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _error.IsVisible = false;
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
        finally { _busy = false; }
    }
    private void ShowError(Exception ex) { _error.Text = LocalizationText.Get("标签操作失败：{0}", ex.Message); _error.IsVisible = true; }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor != null) return;
        if (e.Key == Key.Escape) { CloseRequested?.Invoke(); e.Handled = true; return; }
        var focused = _nameButtons.FirstOrDefault(p => p.Value.IsKeyboardFocusWithin);
        if (focused.Value == null) return;
        if (e.Key is Key.Right or Key.Left)
        {
            if (e.Key == Key.Right) _expanded.Add(focused.Key); else _expanded.Remove(focused.Key);
            RenderRows(); _nameButtons.GetValueOrDefault(focused.Key)?.Focus(); e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            var names = _nameButtons.Keys.ToList(); var index = names.IndexOf(focused.Key);
            _nameButtons[names[Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, names.Count - 1)]].Focus(); e.Handled = true;
        }
    }

    private bool CanDrop(string? name, string? parent) => name != null && _tags.Any(t => t.Name == name)
        && !string.Equals(name, parent, StringComparison.OrdinalIgnoreCase)
        && !FileTagTree.Descendants(_tags, name).Any(t => string.Equals(t.Name, parent, StringComparison.OrdinalIgnoreCase));
    private static int DropZone(Border row, DragEventArgs e) => e.GetPosition(row).Y < 7 ? -1 : e.GetPosition(row).Y > row.Bounds.Height - 7 ? 1 : 0;
    private void OnRowDragOver(Border row, FileTagNode node, DragEventArgs e)
    {
        var zone = DropZone(row, e); var parent = zone == 0 ? node.Tag.Name : node.Tag.ParentName;
        var source = e.DataTransfer.TryGetValue(TagDragFormat);
        var allowed = !string.Equals(source, node.Tag.Name, StringComparison.OrdinalIgnoreCase) && CanDrop(source, parent);
        e.Handled = true; e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        if (!allowed) { ClearDropTarget(); return; }
        if (_dropRow != row) { ClearDropTarget(); _dropRow = row; }
        row.Classes.Set("drop-inside", zone == 0); row.Classes.Set("drop-before", zone < 0); row.Classes.Set("drop-after", zone > 0);
        if (zone != 0) { _hoverTimer?.Stop(); _hoverTimer = null; }
        if (zone == 0 && node.Children.Count > 0 && !_expanded.Contains(node.Tag.Name) && _hoverTimer == null)
        {
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _hoverTimer.Tick += (_, _) => { _expanded.Add(node.Tag.Name); RenderRows(); };
            _hoverTimer.Start();
        }
    }
    private async void OnRowDrop(Border row, FileTag target, DragEventArgs e)
    {
        e.Handled = true;
        var name = e.DataTransfer.TryGetValue(TagDragFormat); var zone = DropZone(row, e);
        var parent = zone == 0 ? target.Name : target.ParentName;
        var source = _tags.FirstOrDefault(t => t.Name == name);
        if (source == null || string.Equals(name, target.Name, StringComparison.OrdinalIgnoreCase) || !CanDrop(name, parent)) { e.DragEffects = DragDropEffects.None; return; }
        var siblings = _tags.Where(t => string.Equals(t.ParentName, parent, StringComparison.OrdinalIgnoreCase) && t.Name != name).ToList();
        var index = zone == 0 ? 0 : siblings.FindIndex(t => t.Name == target.Name) + (zone > 0 ? 1 : 0);
        e.DragEffects = DragDropEffects.Move;
        await GuardAsync(async () => { await _viewModel.FileTagService!.MoveTagAsync(source, parent, index); await RefreshAsync(); });
        ClearDropTarget();
    }
    private void OnRootDragOver(object? sender, DragEventArgs e)
    {
        if (_hideEmpty) return;
        ClearDropTarget(); e.Handled = true;
        e.DragEffects = CanDrop(e.DataTransfer.TryGetValue(TagDragFormat), null) ? DragDropEffects.Move : DragDropEffects.None;
    }
    private async void OnRootDrop(object? sender, DragEventArgs e)
    {
        if (_hideEmpty) return;
        e.Handled = true;
        var tag = _tags.FirstOrDefault(t => t.Name == e.DataTransfer.TryGetValue(TagDragFormat));
        if (tag == null) { e.DragEffects = DragDropEffects.None; return; }
        e.DragEffects = DragDropEffects.Move;
        await GuardAsync(async () => { await _viewModel.FileTagService!.MoveTagAsync(tag, null, _tags.Count(t => t.ParentName == null)); await RefreshAsync(); });
    }
    private void ClearDropTarget()
    {
        _hoverTimer?.Stop(); _hoverTimer = null;
        if (_dropRow != null) { _dropRow.Classes.Remove("drop-inside"); _dropRow.Classes.Remove("drop-before"); _dropRow.Classes.Remove("drop-after"); _dropRow = null; }
    }

    internal static PathIcon Icon(string geometry, double size) => new() { Data = Geometry.Parse(geometry), Width = size, Height = size };
}
