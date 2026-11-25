using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Controls;

// Lightweight nested collection picker for assigning files and quick creation.
public sealed class TagTreePanel : UserControl
{
    internal static readonly DataFormat<string> TagDragFormat = DataFormat.CreateStringApplicationFormat("macexplorer-tag");
    private readonly FileListViewModel _viewModel;
    private readonly IReadOnlyList<string>? _filePaths;
    private readonly bool _hideEmpty;
    private readonly string? _parentName;
    private readonly IReadOnlySet<string>? _rootNames;
    private readonly TagMenuHeader _header;
    private readonly TextBox _searchInput;
    private readonly Button _clearSearchButton;
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Classes = { "tag-tree-error" }, IsVisible = false };
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _nameButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _membership = new(StringComparer.OrdinalIgnoreCase);
    private FileTag[] _tags = [];
    private bool _busy;
    private bool _attached;
    private bool _refreshQueued;
    private int _loadVersion;
    private CancellationTokenSource? _refreshCancellation;

    public event Action? NavigationRequested;
    public event Action? CloseRequested;

    public TagTreePanel(FileListViewModel viewModel, IReadOnlyList<string>? filePaths = null,
        bool hideEmpty = false, string? parentName = null, IReadOnlySet<string>? rootNames = null)
    {
        _viewModel = viewModel;
        _filePaths = filePaths;
        _hideEmpty = hideEmpty;
        _parentName = parentName;
        _rootNames = rootNames;
        MinWidth = 248;
        MaxWidth = 360;

        var content = new StackPanel { Spacing = 4 };
        _header = new TagMenuHeader(HeaderTitle, () => _ = OpenCollectionManagerAsync());
        content.Children.Add(_header);

        _searchInput = new TextBox
        {
            Name = "TagTreeSearchInput",
            Classes = { "settings-inline-input" },
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 80
        };
        _searchInput.TextChanged += (_, _) =>
        {
            _clearSearchButton!.IsVisible = !string.IsNullOrEmpty(_searchInput.Text);
            _error.IsVisible = false;
            RenderRows();
        };
        _searchInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(_searchInput.Text))
                {
                    _searchInput.Text = string.Empty;
                    RenderRows();
                }
                else
                {
                    CloseRequested?.Invoke();
                }
            }
            else if (e.Key == Key.Enter)
            {
                var query = _searchInput.Text?.Trim();
                if (string.IsNullOrEmpty(query)) return;
                e.Handled = true;
                var exact = FindExactMatch(query);
                if (exact != null)
                    await ActivateTagAsync(exact);
                else
                    await CreateFromQueryAsync(query);
            }
        };

        _clearSearchButton = new AppButton
        {
            Classes = { "ghost", "icon-action", "small" },
            Content = Icon(Icons.Close, 12),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        _clearSearchButton.Click += (_, _) =>
        {
            _searchInput.Text = string.Empty;
            _searchInput.Focus();
        };

        var searchGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 4 };
        var searchIcon = Icon(Icons.Search, 13);
        searchIcon.Bind(ForegroundProperty, new DynamicResourceExtension("TextMutedBrush"));
        searchIcon.VerticalAlignment = VerticalAlignment.Center;
        searchGrid.Children.Add(searchIcon);
        Grid.SetColumn(_searchInput, 1);
        searchGrid.Children.Add(_searchInput);
        Grid.SetColumn(_clearSearchButton, 2);
        searchGrid.Children.Add(_clearSearchButton);

        var searchHost = new Border
        {
            Classes = { "tag-tree-search" },
            Margin = new Thickness(6, 0, 6, 2),
            Child = searchGrid
        };
        content.Children.Add(searchHost);
        content.Children.Add(_error);

        var scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = 360,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        content.Children.Add(scroll);

        Content = content;
        UpdateLocalizedLabels();
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
            _attached = false;
            _loadVersion++;
            _refreshCancellation?.Cancel();
            _viewModel.SidebarTags.CollectionChanged -= OnTagsChanged;
            if (LocalizationService.Current is { } language) language.LanguageChanged -= OnLanguageChanged;
        };
    }

    private string HeaderTitle => _parentName == null ? LocalizationText.Get("收藏夹")
        : _tags.FirstOrDefault(t => string.Equals(t.Name, _parentName, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? _parentName;

    private void UpdateLocalizedLabels()
    {
        _header.Update(HeaderTitle);
        _searchInput.PlaceholderText = LocalizationText.Get("collections.search_placeholder");
        AutomationProperties.SetName(_searchInput, LocalizationText.Get("collections.search_placeholder"));
    }

    private void OnTagsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() => { _refreshQueued = false; if (_attached) _ = RefreshAsync(); });
    }

    private void OnLanguageChanged()
    {
        UpdateLocalizedLabels();
        _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        _refreshCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        try
        {
            var service = _viewModel.FileTagService;
            var selection = service != null && _filePaths != null
                ? await service.GetFileTagSelectionAsync(_filePaths, cancellation.Token) : null;
            var tags = selection?.Tags.ToArray() ?? (service == null ? _viewModel.SidebarTags.ToArray()
                : (await service.GetSidebarTagsAsync(cancellation.Token)).ToArray());
            if (version != _loadVersion || !_attached) return;
            _tags = tags;
            _header.Update(HeaderTitle);
            _membership.Clear();
            if (selection != null)
                foreach (var pair in selection.Membership) _membership[pair.Key] = pair.Value;
            RenderRows();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (version == _loadVersion && _attached) ShowError(ex); }
        finally { if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null; }
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        _nameButtons.Clear();

        var query = _searchInput.Text?.Trim() ?? string.Empty;
        var roots = FileTagTree.Build(_tags, _hideEmpty);
        if (_parentName != null) roots = FindNode(roots, _parentName)?.Children ?? [];
        if (_rootNames != null) roots = roots.Where(n => _rootNames.Contains(n.Tag.Name)).ToArray();

        if (!string.IsNullOrEmpty(query))
        {
            if (FindExactMatch(query) == null)
                AddQuickCreateRow(query);
            roots = FilterNodes(roots, query);
        }

        foreach (var node in roots)
            AddNode(node, 0, forceExpand: !string.IsNullOrEmpty(query));

        if (_rows.Children.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = LocalizationText.Get(string.IsNullOrEmpty(query) ? "tags.empty" : "collections.no_match"),
                Margin = new Thickness(8),
                Classes = { "tag-tree-empty" }
            });
        }
    }

    private void AddQuickCreateRow(string query)
    {
        var labelText = _filePaths is { Count: > 0 }
            ? LocalizationText.Get("collections.create_and_apply", query)
            : LocalizationText.Get("collections.create_named", query);
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                Icon(Icons.Plus, 14),
                new TextBlock
                {
                    Text = labelText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        var button = new AppButton
        {
            Name = "QuickCreateCollectionButton",
            Classes = { "ghost", "compact", "tag-tree-name" },
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8, 0, 8, 0)
        };
        button.Click += async (_, _) => await CreateFromQueryAsync(query);
        _rows.Children.Add(new Border { Child = button, Classes = { "tag-tree-row" } });
    }

    private FileTag? FindExactMatch(string query) =>
        _tags.FirstOrDefault(t =>
            string.Equals(t.Name, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.DisplayName, query, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<FileTagNode> FilterNodes(IReadOnlyList<FileTagNode> nodes, string query)
    {
        var result = new List<FileTagNode>();
        foreach (var node in nodes)
        {
            var selfMatch = node.Tag.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || node.Tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            var filteredChildren = FilterNodes(node.Children, query);
            if (selfMatch)
                result.Add(node);
            else if (filteredChildren.Count > 0)
                result.Add(new FileTagNode(node.Tag, filteredChildren));
        }
        return result;
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

    private void AddNode(FileTagNode node, int depth, bool forceExpand)
    {
        var tag = node.Tag;
        var isExpanded = forceExpand || _expanded.Contains(tag.Name);
        var indent = Math.Min(depth * FastFileList.TreeStep, Math.Max(0, (Bounds.Width > 0 ? Bounds.Width : MinWidth) - 140));
        var isAssignmentMenu = _filePaths != null;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(isAssignmentMenu ? "*,16" : "16,*"),
            Margin = new Thickness(8 + indent, 0, 8, 0)
        };
        var arrow = new AppButton
        {
            Classes = { "ghost", "compact", "tag-tree-arrow" },
            Content = Icon(isExpanded ? Icons.ChevronDown : Icons.ChevronRight, 12),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = node.Children.Count > 0
        };
        AutomationProperties.SetName(arrow, LocalizationText.Get("tags.expand", tag.DisplayName));
        arrow.Click += (_, _) => ToggleExpanded(tag.Name);
        Grid.SetColumn(arrow, isAssignmentMenu ? 1 : 0);
        grid.Children.Add(arrow);

        var icon = Icon(FileTagTree.IconGeometry(node), 14);
        if (tag.ColorId != 0)
            icon.Foreground = Brush.Parse(tag.ColorHex);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,22") };
        content.Children.Add(icon);
        var text = new TextBlock
        {
            Text = tag.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var label = new AppButton
        {
            Classes = { "ghost", "compact", "tag-tree-name" },
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = tag
        };
        ToolTip.SetTip(label, tag.DisplayName);
        AutomationProperties.SetName(label, tag.DisplayName);
        Grid.SetColumn(label, isAssignmentMenu ? 0 : 1);
        grid.Children.Add(label);
        _nameButtons[tag.Name] = label;

        var count = _membership.GetValueOrDefault(tag.Name);
        var check = Icon(Icons.Checkmark, 12);
        check.Classes.Add("tag-tree-check");
        check.IsVisible = _filePaths is { Count: > 0 } && count == _filePaths.Count;
        Grid.SetColumn(check, 2);
        content.Children.Add(check);

        var partial = new TextBlock
        {
            Text = "−",
            IsVisible = count > 0 && _filePaths != null && count != _filePaths.Count,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "tag-tree-partial" }
        };
        Grid.SetColumn(partial, 2);
        content.Children.Add(partial);

        if (_filePaths != null)
            AutomationProperties.SetHelpText(label, LocalizationText.Get(count == 0 ? "未选中" : count == _filePaths.Count ? "已选中" : "部分文件已选中"));

        label.Click += async (_, _) => await ActivateTagAsync(tag);

        var row = new Border { Child = grid, Tag = tag, Classes = { "tag-tree-row" } };
        row.Classes.Set("active", _filePaths == null && _viewModel.CurrentTag?.Name == tag.Name);

        // Allow dropping local files onto a collection row to assign them.
        DragDrop.SetAllowDrop(row, true);
        row.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var paths = FileListView.GetDroppedPaths(e.DataTransfer);
            e.Handled = true;
            e.DragEffects = paths.Length > 0 && paths.All(FileTagService.IsSupportedPath) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        row.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var paths = FileListView.GetDroppedPaths(e.DataTransfer);
            e.Handled = true;
            if (paths.Length == 0 || !paths.All(FileTagService.IsSupportedPath)) { e.DragEffects = DragDropEffects.None; return; }
            await GuardAsync(async () =>
            {
                await _viewModel.FileTagService!.SetTagAsync(paths, tag, true);
                await RefreshAsync();
            });
        });

        _rows.Children.Add(row);
        if (isExpanded)
        {
            foreach (var child in node.Children)
                AddNode(child, depth + 1, forceExpand);
        }
    }

    private async Task ActivateTagAsync(FileTag tag)
    {
        if (_busy) return;
        var count = _membership.GetValueOrDefault(tag.Name);
        await GuardAsync(async () =>
        {
            if (_filePaths != null && _viewModel.FileTagService is { } service)
            {
                await service.SetTagAsync(_filePaths, tag, count != _filePaths.Count);
                await RefreshAsync();
            }
            else
            {
                await _viewModel.NavigateToTagAsync(tag);
                NavigationRequested?.Invoke();
            }
        });
    }

    private async Task CreateFromQueryAsync(string name)
    {
        if (_busy || string.IsNullOrWhiteSpace(name)) return;
        await GuardAsync(async () =>
        {
            var service = _viewModel.FileTagService;
            if (service == null) return;
            var created = await service.CreateChildTagAsync(name, _parentName);
            if (_filePaths is { Count: > 0 })
                await service.SetTagAsync(_filePaths, created, true);
            _searchInput.Text = string.Empty;
            await RefreshAsync();
        });
    }

    private Task OpenCollectionManagerAsync()
    {
        var service = _viewModel.FileTagService;
        var owner = CollectionManagerDialog.FindOwnerWindow(this);
        CloseRequested?.Invoke();
        if (owner == null || service == null) return Task.CompletedTask;
        Dispatcher.UIThread.Post(() =>
            _ = CollectionManagerDialog.OpenAsync(service, owner, tag => _viewModel.NavigateToTagAsync(tag)));
        return Task.CompletedTask;
    }

    private void ToggleExpanded(string name)
    {
        if (!_expanded.Remove(name)) _expanded.Add(name);
        RenderRows();
        if (_nameButtons.TryGetValue(name, out var button)) button.Focus();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _error.IsVisible = false;
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
        finally { _busy = false; }
    }

    private void ShowError(Exception ex)
    {
        _error.Text = LocalizationText.Get("标签操作失败：{0}", ex.Message);
        _error.IsVisible = true;
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (_searchInput.IsFocused) return;
        if (e.Key == Key.Escape) { CloseRequested?.Invoke(); e.Handled = true; return; }
        var focused = _nameButtons.FirstOrDefault(p => p.Value.IsKeyboardFocusWithin);
        if (focused.Value == null) return;
        if (e.Key is Key.Right or Key.Left)
        {
            if (e.Key == Key.Right) _expanded.Add(focused.Key); else _expanded.Remove(focused.Key);
            RenderRows();
            _nameButtons.GetValueOrDefault(focused.Key)?.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            var names = _nameButtons.Keys.ToList();
            var index = names.IndexOf(focused.Key);
            _nameButtons[names[Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, names.Count - 1)]].Focus();
            e.Handled = true;
        }
    }

    internal static PathIcon Icon(string geometry, double size) => new() { Data = Geometry.Parse(geometry), Width = size, Height = size };
}
