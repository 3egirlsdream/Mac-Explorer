using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Views.Dialogs;

internal sealed class CollectionManagerDialog : DialogWindow
{
    private const string DragGripGeometry =
        "M9 5.5A1.25 1.25 0 1 1 9 8A1.25 1.25 0 0 1 9 5.5ZM15 5.5A1.25 1.25 0 1 1 15 8A1.25 1.25 0 0 1 15 5.5ZM9 10.75A1.25 1.25 0 1 1 9 13.25A1.25 1.25 0 0 1 9 10.75ZM15 10.75A1.25 1.25 0 1 1 15 13.25A1.25 1.25 0 0 1 15 10.75ZM9 16A1.25 1.25 0 1 1 9 18.5A1.25 1.25 0 0 1 9 16ZM15 16A1.25 1.25 0 1 1 15 18.5A1.25 1.25 0 0 1 15 16Z";

    private readonly IFileTagService _service;
    private readonly Func<FileTag, Task>? _navigateAsync;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _rowsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _buttonsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StackPanel> _childrenPanelsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PathIcon> _arrowIconsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileTagNode> _visibleNodes = [];
    private IReadOnlyList<FileTagNode> _renderedRoots = [];

    private readonly TextBlock _titleText;
    private readonly TextBox _searchBox;
    private readonly Button _clearSearchButton;
    private readonly ScrollViewer _treeScroll;
    private readonly StackPanel _treeRows = new() { Spacing = 0 };
    private readonly Border _rootDropSpacer;
    private readonly TextBlock _statusHintText;
    private readonly TextBlock _errorText;
    private readonly Button _newRootButton;
    private readonly Button _newChildButton;

    private FileTag[] _tags = [];
    private string? _selectedName;
    private string? _editingTargetName;
    private bool _isCreating;
    private string? _draftParentName;
    private TextBox? _inlineEditor;
    private bool _committingEdit;
    private bool _suppressBlurCommit;
    private bool _busy;
    private bool _refreshQueued;
    private bool _startRenameOnOpen;
    private bool _isDragging;
    private Point _dragStart;
    private FileTag? _dragTag;
    private PointerPressedEventArgs? _dragPress;
    private Border? _dropRow;
    private DispatcherTimer? _hoverExpandTimer;

    internal CollectionManagerDialog(
        IFileTagService service,
        FileTag? initialTarget = null,
        bool startCreate = false,
        FileTag? createParent = null,
        bool startRename = false,
        Func<FileTag, Task>? navigateAsync = null)
    {
        _service = service;
        _navigateAsync = navigateAsync;
        _isCreating = startCreate;
        _draftParentName = createParent?.Name;
        _selectedName = startCreate ? createParent?.Name : initialTarget?.Name;
        _editingTargetName = !startCreate && startRename ? initialTarget?.Name : null;
        _startRenameOnOpen = !startCreate && startRename && initialTarget != null;

        Title = LocalizationText.Get("collections.manager.title");
        _titleText = new TextBlock { Text = Title, Classes = { "dialog-window-title", "centered" } };
        ToolTip.SetTip(_titleText, Title);
        TitleBarContent = _titleText;
        Width = 420;
        Height = 480;
        MinWidth = 340;
        MinHeight = 320;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new StyleInclude(new Uri("avares://MacExplorer/"))
            { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });

        // Top search bar.
        _searchBox = new TextBox
        {
            Name = "CollectionSearchInput",
            Classes = { "settings-inline-input" },
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _searchBox.TextChanged += (_, _) =>
        {
            _clearSearchButton!.IsVisible = !string.IsNullOrEmpty(_searchBox.Text);
            RenderTree(preserveScroll: false);
        };
        _clearSearchButton = new AppButton
        {
            Name = "ClearSearchButton",
            Classes = { "ghost", "icon-action", "small" },
            Content = CreateIcon(Icons.Close, 12),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        _clearSearchButton.Click += (_, _) =>
        {
            _searchBox.Text = string.Empty;
            _searchBox.Focus();
        };

        var searchGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 4 };
        var searchIcon = CreateIcon(Icons.Search, 14);
        searchIcon.Bind(ForegroundProperty, new DynamicResourceExtension("TextMutedBrush"));
        searchIcon.VerticalAlignment = VerticalAlignment.Center;
        searchGrid.Children.Add(searchIcon);
        Grid.SetColumn(_searchBox, 1);
        searchGrid.Children.Add(_searchBox);
        Grid.SetColumn(_clearSearchButton, 2);
        searchGrid.Children.Add(_clearSearchButton);
        var searchSurface = new Border
        {
            Classes = { "tag-tree-search" },
            Child = searchGrid,
            Margin = new Thickness(12, 10, 12, 8)
        };

        // Main single-pane outline tree with transparent trailing spacer for dropping back to top level.
        _rootDropSpacer = new Border
        {
            Name = "RootDropSpacer",
            Height = 20,
            Background = Brushes.Transparent
        };
        DragDrop.SetAllowDrop(_rootDropSpacer, true);
        _rootDropSpacer.AddHandler(DragDrop.DragOverEvent, OnRootDropZoneDragOver);
        _rootDropSpacer.AddHandler(DragDrop.DropEvent, OnRootDropZoneDrop);

        var treeContainer = new StackPanel { Spacing = 0, Margin = new Thickness(10, 0, 10, 8) };
        treeContainer.Children.Add(_treeRows);
        treeContainer.Children.Add(_rootDropSpacer);
        DragDrop.SetAllowDrop(treeContainer, true);
        treeContainer.AddHandler(DragDrop.DragOverEvent, OnRootDropZoneDragOver);
        treeContainer.AddHandler(DragDrop.DropEvent, OnRootDropZoneDrop);

        _treeScroll = new ScrollViewer
        {
            Name = "CollectionTreeScrollViewer",
            Content = treeContainer,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _treeScroll.KeyDown += OnTreeKeyDown;

        _errorText = new TextBlock
        {
            Name = "CollectionManagerError",
            Classes = { "settings-description" },
            Margin = new Thickness(14, 0, 14, 6),
            IsVisible = false
        };
        _errorText.Bind(ForegroundProperty, new DynamicResourceExtension("DangerBrush"));

        _statusHintText = new TextBlock
        {
            Name = "DropHintText",
            Classes = { "settings-description" },
            Margin = new Thickness(14, 0, 14, 6),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _newRootButton = new AppButton
        {
            Name = "NewCollectionButton",
            Classes = { "ghost", "compact" }
        };
        _newRootButton.Click += (_, _) => BeginCreate(null);

        _newChildButton = new AppButton
        {
            Name = "NewChildCollectionButton",
            Classes = { "ghost", "compact" }
        };
        _newChildButton.Click += (_, _) =>
        {
            if (SelectedTag is { } parent) BeginCreate(parent.Name);
        };

        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { _newRootButton, _newChildButton }
            }
        };
        footer.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("DialogDividerBrush"));

        var rootGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto") };
        rootGrid.Children.Add(searchSurface);
        Grid.SetRow(_treeScroll, 1);
        rootGrid.Children.Add(_treeScroll);
        Grid.SetRow(_errorText, 2);
        rootGrid.Children.Add(_errorText);
        Grid.SetRow(_statusHintText, 3);
        rootGrid.Children.Add(_statusHintText);
        Grid.SetRow(footer, 4);
        rootGrid.Children.Add(footer);

        Content = rootGrid;
        KeyDown += OnDialogKeyDown;
        ApplyLocalization();

        Opened += async (_, _) =>
        {
            _service.TagsChanged += OnServiceTagsChanged;
            if (LocalizationService.Current is { } language) language.LanguageChanged += OnLanguageChanged;
            await RefreshAsync(preserveScroll: false);
            if (_isCreating || _startRenameOnOpen)
            {
                _startRenameOnOpen = false;
                FocusInlineEditor();
            }
            else if (_selectedName != null && _rowsByName.TryGetValue(_selectedName, out var selectedRow))
            {
                Dispatcher.UIThread.Post(() => selectedRow.BringIntoView(), DispatcherPriority.Loaded);
            }
        };
        Closed += (_, _) =>
        {
            _service.TagsChanged -= OnServiceTagsChanged;
            if (LocalizationService.Current is { } language) language.LanguageChanged -= OnLanguageChanged;
            ClearDropTarget();
        };
    }

    private FileTag? SelectedTag => _selectedName == null ? null
        : _tags.FirstOrDefault(t => SameName(t.Name, _selectedName));

    private bool IsEditingInline => _isCreating || _editingTargetName != null;

    private void ApplyLocalization()
    {
        Title = LocalizationText.Get("collections.manager.title");
        _titleText.Text = Title;
        ToolTip.SetTip(_titleText, Title);

        _searchBox.PlaceholderText = LocalizationText.Get("collections.search_placeholder");
        AutomationProperties.SetName(_searchBox, LocalizationText.Get("collections.search_placeholder"));
        AutomationProperties.SetName(_clearSearchButton, LocalizationText.Get("清空"));

        _newRootButton.Content = CreateActionButtonContent(Icons.Plus, LocalizationText.Get("collections.new"));
        AutomationProperties.SetName(_newRootButton, LocalizationText.Get("collections.new"));
        _newChildButton.Content = CreateActionButtonContent(Icons.StarAdd, LocalizationText.Get("collections.new_child"));
        AutomationProperties.SetName(_newChildButton, LocalizationText.Get("collections.new_child"));

        ResetStatusHint();
    }

    private void ResetStatusHint()
    {
        _statusHintText.Text = LocalizationText.Get("collections.indent_hint");
    }

    private void OnLanguageChanged()
    {
        ApplyLocalization();
        RenderTree(preserveScroll: true);
    }

    private void OnServiceTagsChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _refreshQueued = false;
            if (IsVisible && !IsEditingInline) await RefreshAsync(preserveScroll: true);
        });
    }

    internal async Task RefreshAsync(bool preserveScroll = true)
    {
        try
        {
            _tags = (await _service.GetSidebarTagsAsync()).ToArray();
            if (_isCreating)
            {
                if (_draftParentName != null && !_tags.Any(t => SameName(t.Name, _draftParentName)))
                    _draftParentName = null;
                if (_draftParentName != null) ExpandAncestors(_draftParentName, includeSelf: true);
            }
            else if (_editingTargetName != null && !_tags.Any(t => SameName(t.Name, _editingTargetName)))
            {
                _editingTargetName = null;
            }

            if (_selectedName == null || !_tags.Any(t => SameName(t.Name, _selectedName)))
                _selectedName = FileTagTree.Build(_tags).FirstOrDefault()?.Tag.Name;
            if (_selectedName != null && !preserveScroll)
                ExpandAncestors(_selectedName, includeSelf: false);

            RenderTree(preserveScroll);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ExpandAncestors(string name, bool includeSelf)
    {
        if (includeSelf) _expanded.Add(name);
        var current = _tags.FirstOrDefault(t => SameName(t.Name, name));
        var guard = 0;
        while (current?.ParentName != null && guard++ < 64)
        {
            _expanded.Add(current.ParentName);
            current = _tags.FirstOrDefault(t => SameName(t.Name, current.ParentName));
        }
    }

    private void RenderTree(bool preserveScroll = true)
    {
        var savedOffset = _treeScroll.Offset;
        ClearDropTarget();
        _inlineEditor = null;
        _rowsByName.Clear();
        _buttonsByName.Clear();
        _childrenPanelsByName.Clear();
        _arrowIconsByName.Clear();
        _visibleNodes.Clear();

        var builtRows = new List<Control>();
        var query = _searchBox.Text?.Trim() ?? string.Empty;
        var allRoots = FileTagTree.Build(_tags);
        var roots = string.IsNullOrEmpty(query) ? allRoots : FilterNodes(allRoots, query);
        _renderedRoots = roots;

        if (_isCreating && _draftParentName == null)
            builtRows.Add(CreateDraftRow(0));

        foreach (var node in roots)
            BuildNodeRows(builtRows, node, 0, forceExpand: !string.IsNullOrEmpty(query));

        RebuildVisibleNodes();

        if (builtRows.Count == 0)
        {
            builtRows.Add(new TextBlock
            {
                Text = LocalizationText.Get(string.IsNullOrEmpty(query) ? "tags.empty" : "collections.no_match"),
                Classes = { "tag-tree-empty" },
                Margin = new Thickness(12, 20),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        _treeRows.Children.Clear();
        _treeRows.Children.AddRange(builtRows);

        _newChildButton.IsEnabled = !IsEditingInline && SelectedTag != null;

        if (preserveScroll)
        {
            _treeScroll.Offset = savedOffset;
            Dispatcher.UIThread.Post(() =>
            {
                var maxY = Math.Max(0, _treeScroll.Extent.Height - _treeScroll.Viewport.Height);
                _treeScroll.Offset = new Vector(savedOffset.X, Math.Clamp(savedOffset.Y, 0, maxY));
            }, DispatcherPriority.Render);
        }
    }

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

    private void BuildNodeRows(List<Control> targetRows, FileTagNode node, int depth, bool forceExpand)
    {
        var tag = node.Tag;
        var hasDraftChild = _isCreating && SameName(_draftParentName, tag.Name);
        var isExpanded = forceExpand || hasDraftChild || _expanded.Contains(tag.Name);
        var indent = Math.Min(depth * 18, 108);
        var isEditingThisRow = !_isCreating && SameName(_editingTargetName, tag.Name);

        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,18,*,Auto"),
            Margin = new Thickness(4 + indent, 0, 6, 0)
        };

        var dragGrip = CreateIcon(DragGripGeometry, 12);
        dragGrip.Classes.Add("collection-drag-grip");
        dragGrip.VerticalAlignment = VerticalAlignment.Center;
        dragGrip.HorizontalAlignment = HorizontalAlignment.Center;
        dragGrip.Bind(ForegroundProperty, new DynamicResourceExtension("TextMutedBrush"));
        rowGrid.Children.Add(dragGrip);

        var arrowIcon = CreateIcon(isExpanded ? Icons.ChevronDown : Icons.ChevronRight, 11);
        var arrow = new AppButton
        {
            Classes = { "ghost", "compact", "tag-tree-arrow" },
            Content = arrowIcon,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            IsVisible = node.Children.Count > 0
        };
        _arrowIconsByName[tag.Name] = arrowIcon;
        AutomationProperties.SetName(arrow, LocalizationText.Get("tags.expand", tag.DisplayName));
        arrow.Click += (_, _) => ToggleExpanded(tag.Name, keepFocus: false);
        Grid.SetColumn(arrow, 1);
        rowGrid.Children.Add(arrow);

        var icon = CreateCollectionIcon(node, 14);
        var trailingPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (isEditingThisRow)
        {
            var editGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*"), ColumnSpacing = 6 };
            editGrid.Children.Add(icon);
            var editor = CreateInlineEditor(tag.DisplayName, LocalizationText.Get("重命名"));
            Grid.SetColumn(editor, 1);
            editGrid.Children.Add(editor);
            Grid.SetColumn(editGrid, 2);
            rowGrid.Children.Add(editGrid);

            var confirmBtn = new AppButton
            {
                Name = "CommitInlineEditButton",
                Classes = { "ghost", "icon-action", "small" },
                Content = CreateIcon(Icons.Checkmark, 13),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(confirmBtn, LocalizationText.Get("保存"));
            AutomationProperties.SetName(confirmBtn, LocalizationText.Get("保存"));
            confirmBtn.AddHandler(PointerPressedEvent, (_, _) => _suppressBlurCommit = true, RoutingStrategies.Tunnel);
            confirmBtn.Click += async (_, _) => { _suppressBlurCommit = false; await CommitInlineEditAsync(); };

            var cancelBtn = new AppButton
            {
                Name = "CancelInlineEditButton",
                Classes = { "ghost", "icon-action", "small" },
                Content = CreateIcon(Icons.Close, 10),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(cancelBtn, LocalizationText.Get("取消"));
            AutomationProperties.SetName(cancelBtn, LocalizationText.Get("取消"));
            cancelBtn.AddHandler(PointerPressedEvent, (_, _) => _suppressBlurCommit = true, RoutingStrategies.Tunnel);
            cancelBtn.Click += (_, _) => { _suppressBlurCommit = false; CancelInlineEdit(); };

            trailingPanel.Children.Add(confirmBtn);
            trailingPanel.Children.Add(cancelBtn);
        }
        else
        {
            var labelStack = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*"), ColumnSpacing = 6 };
            labelStack.Children.Add(icon);
            var nameBlock = new TextBlock
            {
                Text = tag.DisplayName,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameBlock, 1);
            labelStack.Children.Add(nameBlock);

            var selectButton = new AppButton
            {
                Classes = { "ghost", "compact", "tag-tree-name" },
                Content = labelStack,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = tag
            };
            ToolTip.SetTip(selectButton, tag.DisplayName);
            AutomationProperties.SetName(selectButton, tag.DisplayName);
            selectButton.Click += (_, _) =>
            {
                if (_isDragging) return;
                SelectCollection(tag.Name);
            };
            selectButton.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                BeginRename(tag);
            };
            Grid.SetColumn(selectButton, 2);
            rowGrid.Children.Add(selectButton);
            _buttonsByName[tag.Name] = selectButton;

            var pinBtn = new AppButton
            {
                Classes = { "ghost", "icon-action", "small" },
                Content = CreateIcon(tag.IsPinned ? Icons.PinOff : Icons.Pin, 12),
                VerticalAlignment = VerticalAlignment.Center
            };
            var pinTip = LocalizationText.Get(tag.IsPinned ? "tags.unpin-home" : "tags.pin-home");
            ToolTip.SetTip(pinBtn, pinTip);
            AutomationProperties.SetName(pinBtn, pinTip);
            pinBtn.Click += async (_, _) => await TogglePinAsync(tag);

            var addChildBtn = new AppButton
            {
                Classes = { "ghost", "icon-action", "small" },
                Content = CreateIcon(Icons.Plus, 12),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(addChildBtn, LocalizationText.Get("collections.new_child"));
            AutomationProperties.SetName(addChildBtn, LocalizationText.Get("collections.new_child"));
            addChildBtn.Click += (_, _) => BeginCreate(tag.Name);

            var renameBtn = new AppButton
            {
                Classes = { "ghost", "icon-action", "small" },
                Content = CreateIcon(Icons.Edit, 12),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(renameBtn, LocalizationText.Get("重命名"));
            AutomationProperties.SetName(renameBtn, LocalizationText.Get("重命名"));
            renameBtn.Click += (_, _) => BeginRename(tag);

            var hoverActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 1,
                Classes = { "collection-row-actions" },
                Children = { pinBtn, addChildBtn, renameBtn }
            };
            if (tag.IsCustom)
            {
                var deleteBtn = new AppButton
                {
                    Classes = { "ghost", "icon-action", "small" },
                    Content = CreateIcon(Icons.Delete, 12),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(deleteBtn, LocalizationText.Get("删除"));
                AutomationProperties.SetName(deleteBtn, LocalizationText.Get("删除"));
                deleteBtn.Click += async (_, _) => await ConfirmAndDeleteAsync(tag);
                hoverActions.Children.Add(deleteBtn);
            }

            trailingPanel.Children.Add(hoverActions);

            if (tag.IsPinned)
            {
                var pinnedBadge = CreateIcon(Icons.Pin, 11);
                pinnedBadge.Bind(ForegroundProperty, new DynamicResourceExtension("AccentBrush"));
                pinnedBadge.Margin = new Thickness(2, 0, 2, 0);
                pinnedBadge.VerticalAlignment = VerticalAlignment.Center;
                ToolTip.SetTip(pinnedBadge, LocalizationText.Get("tags.pin-home"));
                trailingPanel.Children.Add(pinnedBadge);
            }

            var countText = new TextBlock
            {
                Text = tag.ItemCount.ToString(),
                Classes = { "settings-description" },
                Margin = new Thickness(4, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            trailingPanel.Children.Add(countText);
        }

        Grid.SetColumn(trailingPanel, 3);
        rowGrid.Children.Add(trailingPanel);

        var row = new Border
        {
            Child = rowGrid,
            Tag = tag,
            Classes = { "collection-tree-row" },
            ContextMenu = isEditingThisRow ? null : CreateRowContextMenu(tag)
        };
        row.Classes.Set("selected", SameName(_selectedName, tag.Name));

        if (!isEditingThisRow)
        {
            DragDrop.SetAllowDrop(row, true);
            row.AddHandler(DragDrop.DragOverEvent, (_, e) => OnRowDragOver(row, node, e));
            row.AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
            {
                if (_dropRow == row) ClearDropTarget();
            });
            row.AddHandler(DragDrop.DropEvent, (_, e) => OnRowDrop(row, tag, e));
            row.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed || IsEditingInline) return;
                if (e.Source is Visual v && (IsInside(v, arrow) || IsInside(v, trailingPanel))) return;
                _dragStart = e.GetPosition(row);
                _dragTag = tag;
                _dragPress = e;
            }, RoutingStrategies.Tunnel);
            row.PointerReleased += (_, _) => { _dragTag = null; _dragPress = null; };
            row.PointerMoved += async (_, e) =>
            {
                if (_dragPress == null || _dragTag != tag || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
                var delta = e.GetPosition(row) - _dragStart;
                if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 6) return;
                var press = _dragPress;
                _dragPress = null;
                _isDragging = true;
                var data = new DataTransfer();
                var item = new DataTransferItem();
                item.Set(TagTreePanel.TagDragFormat, tag.Name);
                data.Add(item);
                try
                {
                    await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
                }
                finally
                {
                    _isDragging = false;
                    _dragTag = null;
                    ClearDropTarget();
                }
            };
        }

        _rowsByName[tag.Name] = row;
        targetRows.Add(row);

        if (hasDraftChild || node.Children.Count > 0)
        {
            var childControls = new List<Control>();
            if (hasDraftChild)
                childControls.Add(CreateDraftRow(depth + 1));
            foreach (var child in node.Children)
                BuildNodeRows(childControls, child, depth + 1, forceExpand);

            var childrenPanel = new StackPanel
            {
                Spacing = 0,
                IsVisible = isExpanded
            };
            childrenPanel.Children.AddRange(childControls);
            _childrenPanelsByName[tag.Name] = childrenPanel;
            targetRows.Add(childrenPanel);
        }
    }

    private void RebuildVisibleNodes()
    {
        _visibleNodes.Clear();
        var forceExpand = !string.IsNullOrWhiteSpace(_searchBox.Text);
        void Visit(IReadOnlyList<FileTagNode> nodes)
        {
            foreach (var node in nodes)
            {
                _visibleNodes.Add(node);
                var hasDraftChild = _isCreating && SameName(_draftParentName, node.Tag.Name);
                if (forceExpand || hasDraftChild || _expanded.Contains(node.Tag.Name))
                    Visit(node.Children);
            }
        }
        Visit(_renderedRoots);
    }

    private Border CreateDraftRow(int depth)
    {
        var indent = Math.Min(depth * 18, 108);
        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,18,*,Auto"),
            Margin = new Thickness(4 + indent, 0, 6, 0)
        };

        var editGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*"), ColumnSpacing = 6 };
        var icon = CreateIcon(Icons.Tag, 14);
        icon.Bind(ForegroundProperty, new DynamicResourceExtension("TextMutedBrush"));
        editGrid.Children.Add(icon);
        var editor = CreateInlineEditor(string.Empty, LocalizationText.Get("collections.new_default_name"));
        Grid.SetColumn(editor, 1);
        editGrid.Children.Add(editor);
        Grid.SetColumn(editGrid, 2);
        rowGrid.Children.Add(editGrid);

        var confirmBtn = new AppButton
        {
            Name = "CommitInlineEditButton",
            Classes = { "ghost", "icon-action", "small" },
            Content = CreateIcon(Icons.Checkmark, 13),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(confirmBtn, LocalizationText.Get("collections.create"));
        AutomationProperties.SetName(confirmBtn, LocalizationText.Get("collections.create"));
        confirmBtn.AddHandler(PointerPressedEvent, (_, _) => _suppressBlurCommit = true, RoutingStrategies.Tunnel);
        confirmBtn.Click += async (_, _) => { _suppressBlurCommit = false; await CommitInlineEditAsync(); };

        var cancelBtn = new AppButton
        {
            Name = "CancelInlineEditButton",
            Classes = { "ghost", "icon-action", "small" },
            Content = CreateIcon(Icons.Close, 10),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(cancelBtn, LocalizationText.Get("取消"));
        AutomationProperties.SetName(cancelBtn, LocalizationText.Get("取消"));
        cancelBtn.AddHandler(PointerPressedEvent, (_, _) => _suppressBlurCommit = true, RoutingStrategies.Tunnel);
        cancelBtn.Click += (_, _) => { _suppressBlurCommit = false; CancelInlineEdit(); };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { confirmBtn, cancelBtn }
        };
        Grid.SetColumn(actions, 3);
        rowGrid.Children.Add(actions);

        return new Border
        {
            Name = "InlineDraftRow",
            Child = rowGrid,
            Classes = { "collection-tree-row", "selected" }
        };
    }

    private TextBox CreateInlineEditor(string initialText, string placeholder)
    {
        var editor = new TextBox
        {
            Name = "CollectionNameInput",
            Text = initialText,
            PlaceholderText = placeholder,
            MaxLength = 80,
            Classes = { "inline-rename-editor", "bordered" },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(editor, placeholder);
        editor.TextChanged += (_, _) => _errorText.IsVisible = false;
        editor.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await CommitInlineEditAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelInlineEdit();
            }
        };
        editor.LostFocus += async (_, _) =>
        {
            if (_suppressBlurCommit || _committingEdit || !ReferenceEquals(_inlineEditor, editor) || !IsVisible) return;
            var text = editor.Text?.Trim();
            if (_isCreating && string.IsNullOrEmpty(text))
                CancelInlineEdit();
            else
                await CommitInlineEditAsync();
        };
        _inlineEditor = editor;
        return editor;
    }

    private ContextMenu CreateRowContextMenu(FileTag tag)
    {
        var menu = new ContextMenu();
        void Add(string text, string icon, Action action, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = LocalizationText.Get(text),
                Icon = CreateIcon(icon, 16),
                IsEnabled = enabled
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("collections.new_child", Icons.StarAdd, () => BeginCreate(tag.Name));
        Add("重命名", Icons.Edit, () => BeginRename(tag));
        if (tag.IsFinderColor && !string.IsNullOrWhiteSpace(tag.DisplayNameOverride))
            Add("collections.restore_default_name", Icons.ArrowUndo, () => _ = RestoreSystemDefaultNameAsync(tag));
        if (tag.ParentName != null)
            Add("collections.drop_to_root", Icons.Up, () => _ = MoveToRootAsync(tag));
        Add(tag.IsPinned ? "tags.unpin-home" : "tags.pin-home", tag.IsPinned ? Icons.PinOff : Icons.Pin,
            () => _ = TogglePinAsync(tag));
        if (_navigateAsync != null)
            Add("打开", Icons.Open, () => _ = OpenCollectionAsync(tag));
        menu.Items.Add(new Separator());
        Add("删除", Icons.Delete, () => _ = ConfirmAndDeleteAsync(tag), tag.IsCustom);
        return menu;
    }

    private void ToggleExpanded(string name, bool keepFocus)
    {
        var nowExpanded = !_expanded.Remove(name);
        if (nowExpanded) _expanded.Add(name);
        SetExpandedInPlace(name, nowExpanded);
        if (keepFocus && _buttonsByName.TryGetValue(name, out var btn))
            btn.Focus();
    }

    private void SetExpandedInPlace(string name, bool expanded)
    {
        if (expanded) _expanded.Add(name);
        else _expanded.Remove(name);
        if (_childrenPanelsByName.TryGetValue(name, out var panel))
            panel.IsVisible = expanded;
        if (_arrowIconsByName.TryGetValue(name, out var arrowIcon))
            arrowIcon.Data = expanded ? Icons.ChevronDownGeometry : Icons.ChevronRightGeometry;
        RebuildVisibleNodes();
    }

    private void SelectCollection(string name)
    {
        if (SameName(_selectedName, name)) return;
        _selectedName = name;
        _errorText.IsVisible = false;
        foreach (var (key, row) in _rowsByName)
            row.Classes.Set("selected", SameName(key, _selectedName));
        _newChildButton.IsEnabled = !IsEditingInline && SelectedTag != null;
    }

    internal void BeginCreate(string? parentName)
    {
        _isCreating = true;
        _editingTargetName = null;
        _draftParentName = parentName;
        _errorText.IsVisible = false;
        if (parentName != null) ExpandAncestors(parentName, includeSelf: true);
        RenderTree(preserveScroll: true);
        FocusInlineEditor();
    }

    internal void BeginRename(FileTag tag)
    {
        _isCreating = false;
        _draftParentName = null;
        _selectedName = tag.Name;
        _editingTargetName = tag.Name;
        _errorText.IsVisible = false;
        ExpandAncestors(tag.Name, includeSelf: false);
        RenderTree(preserveScroll: true);
        FocusInlineEditor();
    }

    private void FocusInlineEditor()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_inlineEditor == null) return;
            _inlineEditor.BringIntoView();
            _inlineEditor.Focus();
            _inlineEditor.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void CancelInlineEdit()
    {
        var refocus = _editingTargetName ?? _selectedName;
        _suppressBlurCommit = false;
        _isCreating = false;
        _editingTargetName = null;
        _draftParentName = null;
        _inlineEditor = null;
        _errorText.IsVisible = false;
        RenderTree(preserveScroll: true);
        if (refocus != null && _buttonsByName.TryGetValue(refocus, out var btn))
            btn.Focus();
    }

    private async Task CommitInlineEditAsync()
    {
        if (_committingEdit || _busy || _inlineEditor == null) return;
        var text = _inlineEditor.Text?.Trim() ?? string.Empty;
        if (_isCreating)
        {
            if (string.IsNullOrEmpty(text))
            {
                CancelInlineEdit();
                return;
            }
            _committingEdit = true;
            try
            {
                await GuardAsync(async () =>
                {
                    var parent = _draftParentName;
                    var created = await _service.CreateChildTagAsync(text, parent);
                    _isCreating = false;
                    _draftParentName = null;
                    _inlineEditor = null;
                    _selectedName = created.Name;
                    if (parent != null) ExpandAncestors(parent, includeSelf: true);
                    await RefreshAsync(preserveScroll: true);
                });
            }
            finally { _committingEdit = false; }
            return;
        }

        var target = _editingTargetName == null ? null
            : _tags.FirstOrDefault(t => SameName(t.Name, _editingTargetName));
        if (target == null)
        {
            CancelInlineEdit();
            return;
        }
        if (string.IsNullOrEmpty(text) || string.Equals(text, target.DisplayName, StringComparison.Ordinal))
        {
            CancelInlineEdit();
            return;
        }

        _committingEdit = true;
        try
        {
            await GuardAsync(async () =>
            {
                await _service.RenameTagAsync(target, text);
                _editingTargetName = null;
                _inlineEditor = null;
                if (target.IsCustom) _selectedName = text;
                await RefreshAsync(preserveScroll: true);
            });
        }
        finally { _committingEdit = false; }
    }

    private async Task RestoreSystemDefaultNameAsync(FileTag tag)
    {
        if (!tag.IsFinderColor) return;
        await GuardAsync(async () =>
        {
            await _service.RenameTagAsync(tag, tag.Name);
            await RefreshAsync(preserveScroll: true);
        });
    }

    private async Task TogglePinAsync(FileTag tag)
    {
        await GuardAsync(async () =>
        {
            await _service.SetTagPinnedAsync(tag, !tag.IsPinned);
            await RefreshAsync(preserveScroll: true);
        });
    }

    private async Task OpenCollectionAsync(FileTag tag)
    {
        if (_navigateAsync == null) return;
        await _navigateAsync(tag);
        Close();
    }

    private async Task ConfirmAndDeleteAsync(FileTag tag)
    {
        if (!tag.IsCustom || _busy) return;
        var childCount = FileTagTree.Descendants(_tags, tag.Name).Count(t => t.IsCustom);
        var message = LocalizationText.Get("tags.delete-tree", tag.DisplayName, childCount);
        var confirm = new ConfirmDialog("collections.delete_button", message, "删除", isDanger: true);
        if (!await confirm.ShowDialog<bool>(this)) return;
        await GuardAsync(async () =>
        {
            var nextSelection = FindFallbackSelectionAfterDelete(tag);
            await _service.DeleteTagAsync(tag);
            _selectedName = nextSelection;
            await RefreshAsync(preserveScroll: true);
        });
    }

    private string? FindFallbackSelectionAfterDelete(FileTag deleting)
    {
        var subtree = FileTagTree.Descendants(_tags, deleting.Name).Select(t => t.Name)
            .Append(deleting.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = _visibleNodes.FindIndex(n => SameName(n.Tag.Name, deleting.Name));
        if (index >= 0)
        {
            for (var i = index + 1; i < _visibleNodes.Count; i++)
                if (!subtree.Contains(_visibleNodes[i].Tag.Name)) return _visibleNodes[i].Tag.Name;
            for (var i = index - 1; i >= 0; i--)
                if (!subtree.Contains(_visibleNodes[i].Tag.Name)) return _visibleNodes[i].Tag.Name;
        }
        return _tags.FirstOrDefault(t => !subtree.Contains(t.Name))?.Name;
    }

    private async Task MoveToRootAsync(FileTag tag)
    {
        if (tag.ParentName == null) return;
        await GuardAsync(async () =>
        {
            var rootCount = _tags.Count(t => t.ParentName == null && !SameName(t.Name, tag.Name));
            await _service.MoveTagAsync(tag, null, rootCount);
            _selectedName = tag.Name;
            await RefreshAsync(preserveScroll: true);
        });
    }

    private async void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsEditingInline || _visibleNodes.Count == 0) return;
        var currentIndex = _selectedName == null
            ? -1
            : _visibleNodes.FindIndex(n => SameName(n.Tag.Name, _selectedName));

        if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            var nextIndex = currentIndex < 0 ? 0 : Math.Clamp(currentIndex + (e.Key == Key.Down ? 1 : -1), 0, _visibleNodes.Count - 1);
            var target = _visibleNodes[nextIndex].Tag.Name;
            SelectCollection(target);
            if (_buttonsByName.TryGetValue(target, out var btn)) btn.Focus();
        }
        else if (e.Key == Key.Right && currentIndex >= 0)
        {
            var node = _visibleNodes[currentIndex];
            if (node.Children.Count > 0)
            {
                e.Handled = true;
                if (!_expanded.Contains(node.Tag.Name))
                {
                    ToggleExpanded(node.Tag.Name, keepFocus: true);
                }
                else
                {
                    var firstChild = node.Children[0].Tag.Name;
                    SelectCollection(firstChild);
                    if (_buttonsByName.TryGetValue(firstChild, out var childBtn)) childBtn.Focus();
                }
            }
        }
        else if (e.Key == Key.Left && currentIndex >= 0)
        {
            var node = _visibleNodes[currentIndex];
            e.Handled = true;
            if (node.Children.Count > 0 && _expanded.Contains(node.Tag.Name))
            {
                ToggleExpanded(node.Tag.Name, keepFocus: true);
            }
            else if (node.Tag.ParentName != null)
            {
                SelectCollection(node.Tag.ParentName);
                if (_buttonsByName.TryGetValue(node.Tag.ParentName, out var parentBtn)) parentBtn.Focus();
            }
        }
        else if (e.Key == Key.Tab && SelectedTag is { } selected && e.Source is Visual v && IsInside(v, _treeRows))
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                await OutdentSelectedAsync(selected);
            else
                await IndentSelectedAsync(selected);
        }
        else if (e.Key is Key.Enter or Key.F2 && SelectedTag is { } renameTarget)
        {
            e.Handled = true;
            BeginRename(renameTarget);
        }
        else if ((e.Key == Key.Delete || (e.Key == Key.Back && e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
                 && SelectedTag is { IsCustom: true } removable)
        {
            e.Handled = true;
            await ConfirmAndDeleteAsync(removable);
        }
    }

    private async Task IndentSelectedAsync(FileTag selected)
    {
        var roots = FileTagTree.Build(_tags);
        var siblingList = selected.ParentName == null
            ? roots
            : FindNode(roots, selected.ParentName)?.Children ?? [];
        var idx = -1;
        for (var i = 0; i < siblingList.Count; i++)
            if (SameName(siblingList[i].Tag.Name, selected.Name)) { idx = i; break; }
        if (idx <= 0) return;

        var newParent = siblingList[idx - 1];
        await GuardAsync(async () =>
        {
            await _service.MoveTagAsync(selected, newParent.Tag.Name, newParent.Children.Count);
            _expanded.Add(newParent.Tag.Name);
            await RefreshAsync(preserveScroll: true);
            if (_buttonsByName.TryGetValue(selected.Name, out var btn)) btn.Focus();
        });
    }

    private async Task OutdentSelectedAsync(FileTag selected)
    {
        if (selected.ParentName == null) return;
        var parent = _tags.FirstOrDefault(t => SameName(t.Name, selected.ParentName));
        if (parent == null) return;

        var grandParentName = parent.ParentName;
        var roots = FileTagTree.Build(_tags);
        var grandSiblings = grandParentName == null
            ? roots
            : FindNode(roots, grandParentName)?.Children ?? [];
        var parentIdx = -1;
        for (var i = 0; i < grandSiblings.Count; i++)
            if (SameName(grandSiblings[i].Tag.Name, parent.Name)) { parentIdx = i; break; }
        var insertIndex = parentIdx >= 0 ? parentIdx + 1 : grandSiblings.Count;

        await GuardAsync(async () =>
        {
            await _service.MoveTagAsync(selected, grandParentName, insertIndex);
            await RefreshAsync(preserveScroll: true);
            if (_buttonsByName.TryGetValue(selected.Name, out var btn)) btn.Focus();
        });
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled) return;
        if (IsEditingInline)
        {
            e.Handled = true;
            CancelInlineEdit();
            return;
        }
        e.Handled = true;
        Close();
    }

    private bool CanDrop(string? sourceName, string? targetParentName) =>
        sourceName != null
        && _tags.Any(t => SameName(t.Name, sourceName))
        && !SameName(sourceName, targetParentName)
        && !FileTagTree.Descendants(_tags, sourceName).Any(t => SameName(t.Name, targetParentName));

    private static int GetDropZone(Border row, DragEventArgs e)
    {
        var height = row.Bounds.Height > 0 ? row.Bounds.Height : 32;
        var edge = Math.Clamp(height * 0.25, 6, 9);
        var y = e.GetPosition(row).Y;
        return y < edge ? -1 : y > height - edge ? 1 : 0;
    }

    private void OnRowDragOver(Border row, FileTagNode node, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(TagTreePanel.TagDragFormat);
        if (source == null) return;
        var zone = GetDropZone(row, e);
        var targetParent = zone == 0 ? node.Tag.Name : node.Tag.ParentName;
        var allowed = !SameName(source, node.Tag.Name) && CanDrop(source, targetParent);
        e.Handled = true;
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        if (!allowed)
        {
            ClearDropTarget();
            return;
        }

        if (_dropRow != row)
        {
            ClearDropTarget();
            _dropRow = row;
        }
        row.Classes.Set("drop-inside", zone == 0);
        row.Classes.Set("drop-before", zone < 0);
        row.Classes.Set("drop-after", zone > 0);
        _statusHintText.Text = zone == 0
            ? LocalizationText.Get("collections.drop_into", node.Tag.DisplayName)
            : LocalizationText.Get("collections.indent_hint");

        if (zone != 0)
        {
            _hoverExpandTimer?.Stop();
            _hoverExpandTimer = null;
        }
        else if (node.Children.Count > 0 && !_expanded.Contains(node.Tag.Name) && _hoverExpandTimer == null)
        {
            _hoverExpandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _hoverExpandTimer.Tick += (_, _) =>
            {
                _hoverExpandTimer?.Stop();
                _hoverExpandTimer = null;
                SetExpandedInPlace(node.Tag.Name, true);
            };
            _hoverExpandTimer.Start();
        }
    }

    private async void OnRowDrop(Border row, FileTag target, DragEventArgs e)
    {
        var sourceName = e.DataTransfer.TryGetValue(TagTreePanel.TagDragFormat);
        if (sourceName == null) return;
        e.Handled = true;
        var zone = GetDropZone(row, e);
        var targetParent = zone == 0 ? target.Name : target.ParentName;
        var source = _tags.FirstOrDefault(t => SameName(t.Name, sourceName));
        ClearDropTarget();
        if (source == null || SameName(sourceName, target.Name) || !CanDrop(sourceName, targetParent))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var siblings = _tags.Where(t => SameName(t.ParentName, targetParent) && !SameName(t.Name, sourceName))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var index = zone == 0
            ? siblings.Count
            : siblings.FindIndex(t => SameName(t.Name, target.Name)) + (zone > 0 ? 1 : 0);
        if (index < 0) index = siblings.Count;

        e.DragEffects = DragDropEffects.Move;
        await GuardAsync(async () =>
        {
            await _service.MoveTagAsync(source, targetParent, index);
            if (targetParent != null) ExpandAncestors(targetParent, includeSelf: true);
            _selectedName = source.Name;
            await RefreshAsync(preserveScroll: true);
        });
    }

    private void OnRootDropZoneDragOver(object? sender, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(TagTreePanel.TagDragFormat);
        if (source == null) return;
        ClearDropTarget();
        e.Handled = true;
        var allowed = CanDrop(source, null);
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        if (allowed)
            _statusHintText.Text = LocalizationText.Get("collections.drop_to_root");
    }

    private async void OnRootDropZoneDrop(object? sender, DragEventArgs e)
    {
        var sourceName = e.DataTransfer.TryGetValue(TagTreePanel.TagDragFormat);
        if (sourceName == null) return;
        e.Handled = true;
        ClearDropTarget();
        var source = _tags.FirstOrDefault(t => SameName(t.Name, sourceName));
        if (source == null) { e.DragEffects = DragDropEffects.None; return; }
        e.DragEffects = DragDropEffects.Move;
        await GuardAsync(async () =>
        {
            var rootCount = _tags.Count(t => t.ParentName == null && !SameName(t.Name, sourceName));
            await _service.MoveTagAsync(source, null, rootCount);
            _selectedName = source.Name;
            await RefreshAsync(preserveScroll: true);
        });
    }

    private void ClearDropTarget()
    {
        _hoverExpandTimer?.Stop();
        _hoverExpandTimer = null;
        if (_dropRow != null)
        {
            _dropRow.Classes.Remove("drop-inside");
            _dropRow.Classes.Remove("drop-before");
            _dropRow.Classes.Remove("drop-after");
            _dropRow = null;
        }
        ResetStatusHint();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _errorText.IsVisible = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.IsVisible = true;
    }

    private static FileTagNode? FindNode(IReadOnlyList<FileTagNode> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (SameName(node.Tag.Name, name)) return node;
            if (FindNode(node.Children, name) is { } found) return found;
        }
        return null;
    }

    private static PathIcon CreateCollectionIcon(FileTagNode node, double size)
    {
        var icon = CreateIcon(FileTagTree.IconGeometry(node), size);
        if (node.Tag.ColorId != 0)
            icon.Foreground = Brush.Parse(node.Tag.ColorHex);
        return icon;
    }

    private static PathIcon CreateIcon(string geometry, double size) =>
        new() { Data = Geometry.Parse(geometry), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel CreateActionButtonContent(string iconGeometry, string label) =>
        new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                CreateIcon(iconGeometry, 12),
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }
            }
        };

    internal static Window? FindOwnerWindow(Visual? source)
    {
        if (source != null)
        {
            var top = TopLevel.GetTopLevel(source);
            if (top is Window direct)
                return direct;
            if (top is PopupRoot popupRoot && popupRoot.Parent is Visual host
                && TopLevel.GetTopLevel(host) is Window hostWindow)
                return hostWindow;
        }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow ?? desktop.Windows.FirstOrDefault();
        return null;
    }

    internal static async Task OpenAsync(
        IFileTagService service,
        Visual? ownerSource,
        Func<FileTag, Task>? navigateAsync = null,
        FileTag? initialTarget = null,
        bool startCreate = false,
        FileTag? createParent = null,
        bool startRename = false)
    {
        var owner = FindOwnerWindow(ownerSource);
        if (owner == null) return;
        var dialog = new CollectionManagerDialog(
            service,
            initialTarget: initialTarget,
            startCreate: startCreate,
            createParent: createParent,
            startRename: startRename,
            navigateAsync: navigateAsync);
        using var modalBlock = owner is MainWindow mainWindow
            ? mainWindow.BlockModalParentInteraction()
            : null;
        await dialog.ShowDialog(owner);
    }

    private static bool SameName(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(Visual? source, Visual? target)
    {
        for (; source != null; source = source.GetVisualParent())
            if (ReferenceEquals(source, target)) return true;
        return false;
    }
}
