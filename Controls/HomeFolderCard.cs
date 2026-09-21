using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiquidGlassAvaloniaUI;
using MacExplorer.Models;

namespace MacExplorer.Controls;

/// <summary>A bounded icon grid with an overflow-only final expand cell and a keyboard-accessible resize grip.</summary>
public sealed class HomeFolderCard : UserControl, IDisposable
{
    private readonly Border _surface;
    private readonly UniformGrid _grid;
    private readonly TextBlock _count;
    private readonly Button _grip;
    private readonly TextBlock _resizeLabel;
    private readonly Func<FileSystemEntry, Button> _createButton;
    private readonly Action _expand;
    private readonly Action _add;
    private readonly Dictionary<string, FileSystemEntry> _entries = new(StringComparer.Ordinal);
    private HomeFolderLayout _preferred;
    private HomeFolderLayout _effective;
    private double _availableWidth = 1000;
    private Point _dragStart;
    private Size _dragSize;
    private IPointer? _pointer;
    private CancellationTokenSource? _previewCancellation;
    private bool _disposed;
    public FileTag TagDefinition { get; }
    public IReadOnlyList<string> Paths { get; private set; } = [];
    public bool HasOverflow => Paths.Count > _effective.Capacity;
    public int PreviewCapacity => _effective.Capacity - (HasOverflow ? 1 : 0);
    public IReadOnlyList<FileSystemEntry> PreviewEntries => Paths.Take(PreviewCapacity)
        .Where(_entries.ContainsKey).Select(path => _entries[path]).ToArray();
    public HomeFolderLayout PreferredLayout => _preferred;
    public event Action<HomeFolderCard>? PreviewRequested;
    public event Action<HomeFolderCard>? LayoutCommitted;

    public HomeFolderCard(FileTag tag, HomeFolderLayout layout, Func<FileSystemEntry, Button> createButton,
        Action expand, Action add, ContextMenu menu)
    {
        Classes.Add("home-card");
        Focusable = true;
        ClipToBounds = false;
        TagDefinition = tag;
        _preferred = layout.Normalize();
        _effective = _preferred;
        _createButton = createButton;
        _expand = expand;
        _add = add;
        Margin = new Thickness(8, 8, 8, 8);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;

        var root = new Grid();
        var content = new Grid { RowDefinitions = new RowDefinitions("30,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = Brush.Parse(tag.ColorHex), Margin = new Thickness(0, 0, 8, 0) };
        header.Children.Add(dot);
        var title = new TextBlock { Text = tag.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Classes = { "home-section-title" } };
        Grid.SetColumn(title, 1); header.Children.Add(title);
        _count = new TextBlock { Text = "…", Classes = { "home-secondary" }, Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_count, 2); header.Children.Add(_count);
        var more = new Button { Content = new PathIcon { Data = Geometry.Parse(MacExplorer.Assets.Icons.MoreHorizontal), Width = 16, Height = 16 },
            Classes = { "ghost", "home-card-menu" }, ContextMenu = menu };
        AutomationProperties.SetName(more, $"{tag.Name}收藏夹选项");
        more.Click += (_, _) => menu.Open(more);
        Grid.SetColumn(more, 3); header.Children.Add(more);
        content.Children.Add(header);
        _grid = new UniformGrid();
        Grid.SetRow(_grid, 1); content.Children.Add(_grid);
        _surface = new Border { Classes = { "home-folder" }, Child = content };
        var glass = new LiquidGlassSurface { Classes = { "home-glass" }, IsHitTestVisible = false };
        LiquidGlassBackdrop.SetIsExcludedFromCapture(root, true);
        root.Children.Add(glass);
        root.Children.Add(_surface);
        Tapped += (_, e) =>
        {
            if (e.Source is Visual visual && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any())) return;
            _expand(); e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (ReferenceEquals(e.Source, this) && e.Key is Key.Enter or Key.Space)
            { _expand(); e.Handled = true; }
        };
        _grip = new Button { Classes = { "ghost", "home-resize-grip" },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0), Cursor = new Cursor(StandardCursorType.BottomRightCorner) };
        ToolTip.SetTip(_grip, "拖动调整大小；聚焦后用方向键调整行列数");
        _grip.AddHandler(PointerPressedEvent, OnGripPressed, RoutingStrategies.Tunnel);
        _grip.PointerMoved += OnGripMoved;
        _grip.PointerReleased += (_, e) => { if (_pointer == e.Pointer) { FinishResize(); e.Handled = true; } };
        _grip.PointerCaptureLost += (_, _) => FinishResize();
        _grip.KeyDown += OnGripKeyDown;
        root.Children.Add(_grip);
        _resizeLabel = new TextBlock { Classes = { "home-secondary" }, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 40, 7), IsVisible = false };
        root.Children.Add(_resizeLabel);
        Content = root;
        Render();
    }

    public void SetDropHighlight(bool active) => _surface.Classes.Set("drop-target", active);

    public void SetAvailableWidth(double width)
    {
        _availableWidth = Math.Max(80, width);
        ApplyLayout(_preferred);
    }

    public void SetPreferredLayout(HomeFolderLayout layout)
    {
        if (_pointer == null) ApplyLayout(layout.Normalize());
    }

    public void SetPaths(IReadOnlyList<string> paths)
    {
        Paths = paths;
        _count.Text = paths.Count.ToString();
        Render();
        PreviewRequested?.Invoke(this);
    }

    public void SetLoadError(string message)
    {
        _count.Text = "未加载";
        ToolTip.SetTip(_count, message);
    }

    public CancellationToken BeginPreview(CancellationToken parent)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
        return _previewCancellation.Token;
    }

    public void SetEntries(IEnumerable<FileSystemEntry> entries, CancellationToken token)
    {
        if (_disposed || token.IsCancellationRequested) return;
        foreach (var entry in entries) _entries[entry.FullPath] = entry;
        Render();
    }

    private void ApplyLayout(HomeFolderLayout preferred)
    {
        _preferred = preferred;
        var next = preferred.Fit(_availableWidth);
        var changed = next != _effective;
        _effective = next;
        Width = Math.Min(_availableWidth, next.Columns * HomeFolderLayout.CellWidth + HomeFolderLayout.HorizontalChrome);
        Height = next.Rows * HomeFolderLayout.CellHeight + HomeFolderLayout.VerticalChrome;
        if (changed) { Render(); PreviewRequested?.Invoke(this); }
    }

    private void Render()
    {
        Width = Math.Min(_availableWidth, _effective.Columns * HomeFolderLayout.CellWidth + HomeFolderLayout.HorizontalChrome);
        Height = _effective.Rows * HomeFolderLayout.CellHeight + HomeFolderLayout.VerticalChrome;
        _grid.Columns = _effective.Columns;
        _grid.Rows = _effective.Rows;
        _grid.Children.Clear();
        var capacity = PreviewCapacity;
        for (var index = 0; index < capacity; index++)
        {
            if (index < Paths.Count)
            {
                var path = Paths[index];
                if (_entries.TryGetValue(path, out var entry)) _grid.Children.Add(_createButton(entry));
                else _grid.Children.Add(new TextBlock { Text = Path.GetFileName(path), Opacity = 0.45,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(6) });
            }
            else if (index == 0 && Paths.Count == 0)
            {
                var add = new Button { Content = "添加文件", Classes = { "ghost", "home-grid-item" } };
                add.Click += (_, _) => _add();
                _grid.Children.Add(add);
            }
            else _grid.Children.Add(new Border { IsHitTestVisible = false });
        }
        if (HasOverflow)
        {
            var mosaic = new UniformGrid { Columns = 2, Rows = 2, Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
            var converter = new MacExplorer.Converters.FileEntryToIconConverter();
            foreach (var extension in new[] { ".png", ".pdf", ".zip", ".txt" })
            {
                var sample = new FileSystemEntry { Name = "preview" + extension, Extension = extension,
                    IconKey = MacExplorer.Services.Impl.FileIconResolver.ResolveIconKey(extension) };
                mosaic.Children.Add(new Image { Width = 22, Height = 22,
                    Source = converter.Convert(sample, typeof(IImage), 40, System.Globalization.CultureInfo.InvariantCulture) as IImage });
            }
            mosaic.VerticalAlignment = VerticalAlignment.Center;
            var expandContent = MacExplorer.Views.HomeItemActions.CreateGridContent(mosaic,
                new TextBlock { Text = $"全部 {Paths.Count}", FontSize = 12 });
            var expand = new Button { Content = expandContent, Classes = { "ghost", "home-grid-item", "home-overflow" } };
            AutomationProperties.SetName(expand, $"展开{TagDefinition.Name}收藏夹，共{Paths.Count}项");
            ToolTip.SetTip(expand, $"展开全部 · 还有 {Paths.Count - capacity} 项");
            expand.Click += (_, _) => _expand();
            _grid.Children.Add(expand);
        }
        _grip.Content = new PathIcon { Data = Geometry.Parse(MacExplorer.Assets.Icons.Resize), Width = 16, Height = 16 };
        _resizeLabel.Text = $"{_effective.Columns} × {_effective.Rows}";
        ToolTip.SetTip(_grip, $"{_effective.Columns} 列 × {_effective.Rows} 行 · 拖动或用方向键调整");
        AutomationProperties.SetName(_grip, $"调整{TagDefinition.Name}收藏夹大小，{_effective.Columns}列{_effective.Rows}行");
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed) return;
        _grip.Focus();
        _dragStart = e.GetPosition(TopLevel.GetTopLevel(this));
        _dragSize = new Size(Width, Height);
        _grip.Classes.Add("resizing");
        _resizeLabel.IsVisible = true;
        _pointer = e.Pointer;
        _pointer.Capture(_grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_pointer != e.Pointer) return;
        var delta = e.GetPosition(TopLevel.GetTopLevel(this)) - _dragStart;
        var width = Math.Min(_availableWidth, _dragSize.Width + delta.X);
        ApplyLayout(HomeFolderLayout.FromSize(width, _dragSize.Height + delta.Y));
        e.Handled = true;
    }

    private void FinishResize()
    {
        if (_pointer == null) return;
        var pointer = _pointer;
        _pointer = null;
        _grip.Classes.Remove("resizing");
        _resizeLabel.IsVisible = false;
        pointer.Capture(null);
        if (!_disposed) LayoutCommitted?.Invoke(this);
    }

    private void OnGripKeyDown(object? sender, KeyEventArgs e)
    {
        var layout = e.Key switch
        {
            Key.Left => _preferred with { Columns = _preferred.Columns - 1 },
            Key.Right => _preferred with { Columns = _preferred.Columns + 1 },
            Key.Up => _preferred with { Rows = _preferred.Rows - 1 },
            Key.Down => _preferred with { Rows = _preferred.Rows + 1 },
            _ => null
        };
        if (layout == null) return;
        ApplyLayout(layout.Normalize());
        LayoutCommitted?.Invoke(this);
        e.Handled = true;
    }

    public void Dispose()
    {
        _disposed = true;
        FinishResize();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
    }
}
