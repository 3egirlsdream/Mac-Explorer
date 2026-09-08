using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Icons = MacExplorer.Assets.Icons;
using MacExplorer.Models;
using MacExplorer.Performance;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

/// <summary>Visible files and group headers share geometry for painting, selection and scrolling.</summary>
public sealed class FastFileList : Control, ILogicalScrollable
{
    public const double RowHeight = FileListScrollAnchor.DetailsRowHeight;
    private const double Inset = 13;
    private const double IconSlot = 22;
    private const int TextCacheLimit = 1024;

    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Background));
    public static readonly StyledProperty<bool> IsLoadingProperty = AvaloniaProperty.Register<FastFileList, bool>(nameof(IsLoading));
    public static readonly StyledProperty<bool> IsGridProperty = AvaloniaProperty.Register<FastFileList, bool>(nameof(IsGrid));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Foreground), Brushes.Black);
    public static readonly StyledProperty<IBrush?> SecondaryProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Secondary), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> HoverProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Hover));
    public static readonly StyledProperty<IBrush?> SelectedProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Selected));
    public static readonly StyledProperty<IBrush?> SelectedHoverProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(SelectedHover));
    public static readonly StyledProperty<IBrush?> DropBrushProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(DropBrush));
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = AvaloniaProperty.Register<FastFileList, FontFamily>(nameof(FontFamily), FontFamily.Default);
    public static readonly StyledProperty<FontWeight> FontWeightProperty = AvaloniaProperty.Register<FastFileList, FontWeight>(nameof(FontWeight), FontWeight.Normal);
    public static readonly StyledProperty<double> FontSizeProperty = AvaloniaProperty.Register<FastFileList, double>(nameof(FontSize), 13);
    public static readonly StyledProperty<double> DetailFontSizeProperty = AvaloniaProperty.Register<FastFileList, double>(nameof(DetailFontSize), 12);
    public static readonly StyledProperty<double> CaptionFontSizeProperty = AvaloniaProperty.Register<FastFileList, double>(nameof(CaptionFontSize), 11);
    public static readonly StyledProperty<double> MetaFontSizeProperty = AvaloniaProperty.Register<FastFileList, double>(nameof(MetaFontSize), 10);
    public static readonly StyledProperty<double> GroupMinHeightProperty = AvaloniaProperty.Register<FastFileList, double>(nameof(GroupMinHeight), 28);
    public static readonly StyledProperty<IBrush?> MutedProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Muted), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> DividerProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Divider), Brushes.LightGray);
    public static readonly StyledProperty<IBrush?> FocusRingProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(FocusRing));
    public static readonly StyledProperty<BoxShadows> SelectionOutlineProperty = AvaloniaProperty.Register<FastFileList, BoxShadows>(nameof(SelectionOutline));
    public static readonly StyledProperty<BoxShadows> FocusOutlineProperty = AvaloniaProperty.Register<FastFileList, BoxShadows>(nameof(FocusOutline));
    public static readonly StyledProperty<CornerRadius> RowCornerRadiusProperty = AvaloniaProperty.Register<FastFileList, CornerRadius>(nameof(RowCornerRadius), new CornerRadius(4));

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public bool IsLoading { get => GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public bool IsGrid { get => GetValue(IsGridProperty); set => SetValue(IsGridProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? Secondary { get => GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }
    public IBrush? Hover { get => GetValue(HoverProperty); set => SetValue(HoverProperty, value); }
    public IBrush? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public IBrush? SelectedHover { get => GetValue(SelectedHoverProperty); set => SetValue(SelectedHoverProperty, value); }
    public IBrush? DropBrush { get => GetValue(DropBrushProperty); set => SetValue(DropBrushProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public double DetailFontSize { get => GetValue(DetailFontSizeProperty); set => SetValue(DetailFontSizeProperty, value); }
    public double CaptionFontSize { get => GetValue(CaptionFontSizeProperty); set => SetValue(CaptionFontSizeProperty, value); }
    public double MetaFontSize { get => GetValue(MetaFontSizeProperty); set => SetValue(MetaFontSizeProperty, value); }
    public double GroupMinHeight { get => GetValue(GroupMinHeightProperty); set => SetValue(GroupMinHeightProperty, value); }
    public IBrush? Muted { get => GetValue(MutedProperty); set => SetValue(MutedProperty, value); }
    public IBrush? Divider { get => GetValue(DividerProperty); set => SetValue(DividerProperty, value); }
    public IBrush? FocusRing { get => GetValue(FocusRingProperty); set => SetValue(FocusRingProperty, value); }
    public BoxShadows SelectionOutline { get => GetValue(SelectionOutlineProperty); set => SetValue(SelectionOutlineProperty, value); }
    public BoxShadows FocusOutline { get => GetValue(FocusOutlineProperty); set => SetValue(FocusOutlineProperty, value); }
    public CornerRadius RowCornerRadius { get => GetValue(RowCornerRadiusProperty); set => SetValue(RowCornerRadiusProperty, value); }

    private IReadOnlyList<FileSystemEntry> _rows = Array.Empty<FileSystemEntry>();
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<RowText>> _texts = new(StringComparer.Ordinal);
    private readonly LinkedList<RowText> _textLru = new();
    private readonly Dictionary<(FileListColumn Column, string Value), FormattedText> _cellTexts = [];
    private readonly Queue<(FileListColumn Column, string Value)> _cellTextOrder = new();
    private readonly HashSet<FileSystemEntry> _observed = [];
    private readonly Dictionary<string, FormattedText> _badges = [];
    private readonly FastFileListImages _images = new();
    private readonly FastFileListLayout _layout = new();
    private IReadOnlyList<FastFileListGroup> _groups = [];
    private readonly Dictionary<string, (FormattedText Title, FormattedText Count)> _headerTexts = [];
    private readonly Dictionary<string, FormattedText> _gridNames = [];
    private Dictionary<string, double> _gridNameHeights = [];
    private bool _hasVirtualRows;
    private readonly Geometry _fileFallback = Geometry.Parse(Icons.File);
    private readonly Geometry _folderFallback = Geometry.Parse(Icons.Folder);
    private FileListColumnWidths _columns = FileListColumnLayoutService.Defaults;
    private double _offset;
    private bool _visibleUpdateQueued;
    private bool _attached;
    private TopLevel? _topLevel;
    private Control[] _visibilityAncestors = [];
    private Point? _pointerPosition;
    private string? _dropPath;
    private string? _editingPath;

    internal Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>>? ThumbnailProvider
    {
        get => _images.ThumbnailProvider;
        set { _images.ThumbnailProvider = value; ScheduleVisibleUpdate(); }
    }
    public IReadOnlyList<FileSystemEntry> Rows => _rows;
    internal int CachedTextCount => _texts.Count;
    internal int ObservedRowCount => _observed.Count;
    internal int LastRenderedRowCount { get; private set; }
    internal int LastRenderedSkeletonRowCount { get; private set; }
    internal int LastRenderedHeaderCount { get; private set; }
    internal int GridColumns => _layout.Columns;
    internal double ItemHeight => _layout.ItemHeight;
    internal double LastRenderMilliseconds { get; private set; }
    public string? EditingPath
    {
        get => _editingPath;
        set
        {
            if (_editingPath == value) return;
            _editingPath = value;
            if (value != null)
            {
                ToolTip.SetIsOpen(this, false);
                ToolTip.SetTip(this, null);
            }
            InvalidateVisual();
        }
    }
    internal string? KeyboardFocusPath { get; set; }
    public string? DropTargetPath
    {
        get => _dropPath;
        set { if (_dropPath == value) return; _dropPath = value; InvalidateVisual(); }
    }
    internal FileListColumnWidths ColumnWidths
    {
        get => _columns;
        set { if (_columns == value) return; _columns = value; ClearTexts(); InvalidateVisual(); }
    }

    public FastFileList()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        _images.Changed += InvalidateVisual;
    }

    static FastFileList()
    {
        AffectsRender<FastFileList>(IsGridProperty, IsLoadingProperty, BackgroundProperty, ForegroundProperty, SecondaryProperty, HoverProperty,
            SelectedProperty, SelectedHoverProperty, DropBrushProperty, FontFamilyProperty, FontWeightProperty,
            FontSizeProperty, DetailFontSizeProperty, CaptionFontSizeProperty, MetaFontSizeProperty, GroupMinHeightProperty,
            MutedProperty, DividerProperty, FocusRingProperty, SelectionOutlineProperty, FocusOutlineProperty, RowCornerRadiusProperty);
    }

    public void SetRows(IReadOnlyList<FileSystemEntry> rows)
        => SetRows(rows, []);

    internal void SetRows(IReadOnlyList<FileSystemEntry> rows, IReadOnlyList<FastFileListGroup> groups)
    {
        _rows = rows;
        _groups = groups;
        _headerTexts.Clear();
        _hasVirtualRows = rows.Any(entry => entry.IsVirtual);
        _indices.Clear();
        for (var i = 0; i < rows.Count; i++) _indices[rows[i].FullPath] = i;
        BuildLayout();
        _offset = ClampOffset(_offset);
        RaiseScrollInvalidated(EventArgs.Empty);
        ScheduleVisibleUpdate();
        InvalidateVisual();
    }

    private void BuildLayout()
    {
        var previousHeights = _gridNameHeights;
        _gridNameHeights = [];
        double? countHeight = null;
        _layout.Build(_rows.Count, _groups, IsGrid, Bounds.Width, _hasVirtualRows,
            IsGrid ? EntryHeight : null, GroupMinHeight + 6);
        _headerTexts.Clear();

        double EntryHeight(int index)
        {
            var entry = _rows[index];
            var name = entry.IconDisplayName;
            if (!_gridNameHeights.TryGetValue(name, out var height))
            {
                if (!previousHeights.TryGetValue(name, out height)) height = Math.Ceiling(GridName(entry).Height);
                _gridNameHeights[name] = height;
            }
            // Keep only compact metrics for this directory; formatted text stays bounded to the viewport cache.
            return 106 + height + (entry.IsVirtual
                ? 2 + (countHeight ??= Math.Ceiling(Format("0 张照片", MetaFontSize, Foreground, 100).Height)) : 0);
        }
    }

    public Size Extent => new(Viewport.Width, _layout.Height);
    public Size Viewport => Bounds.Size;
    public Vector Offset
    {
        get => new(0, _offset);
        set
        {
            var next = ClampOffset(value.Y);
            if (_offset == next) return;
            _offset = next;
            ScheduleVisibleUpdate();
            InvalidateVisual();
            ViewportChanged?.Invoke();
        }
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public bool IsLogicalScrollEnabled => true;
    public Size ScrollSize => new(0, _layout.ItemHeight);
    public Size PageScrollSize => new(0, Math.Max(_layout.ItemHeight, Viewport.Height - _layout.ItemHeight));
    public event EventHandler? ScrollInvalidated;
    public event Action? ViewportChanged;
    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);
    public bool BringIntoView(Control target, Rect targetRect) => false;
    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;
    private double ClampOffset(double value) => double.IsFinite(value)
        ? Math.Clamp(value, 0, Math.Max(0, Extent.Height - Viewport.Height)) : 0;

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? availableSize.Width : Inset * 2 + IconSlot + _columns.Total,
        double.IsFinite(availableSize.Height) ? availableSize.Height : Math.Min(Extent.Height, RowHeight * 20));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var typographyChanged = change.Property == FontFamilyProperty || change.Property == FontWeightProperty || change.Property == FontSizeProperty
            || change.Property == DetailFontSizeProperty || change.Property == MetaFontSizeProperty
            || change.Property == CaptionFontSizeProperty;
        if (change.Property == FontFamilyProperty || change.Property == FontWeightProperty || change.Property == DetailFontSizeProperty) _gridNameHeights.Clear();
        if (typographyChanged || change.Property == ForegroundProperty || change.Property == SecondaryProperty
            || change.Property == MutedProperty || change.Property == IsGridProperty)
            ClearTexts();
        if (change.Property == IsGridProperty)
            RenderOptions.SetBitmapInterpolationMode(this, IsGrid ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.LowQuality);
        if (change.Property == IsLoadingProperty)
        {
            _pointerPosition = null;
            ToolTip.SetTip(this, null);
            ScheduleVisibleUpdate();
        }
        if (change.Property == BoundsProperty || change.Property == IsGridProperty || typographyChanged || change.Property == GroupMinHeightProperty)
        {
            var anchor = VisibleRange.First;
            var y = anchor < _rows.Count ? RowBounds(anchor).Y : 0;
            BuildLayout();
            if (anchor < _rows.Count) _offset = _layout.Bounds(anchor).Y - y;
            _offset = ClampOffset(_offset);
            RaiseScrollInvalidated(EventArgs.Empty);
            ScheduleVisibleUpdate();
            ViewportChanged?.Invoke();
        }
        if (change.Property == IsVisibleProperty) ScheduleVisibleUpdate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null) _topLevel.ScalingChanged += OnScalingChanged;
        _visibilityAncestors = this.GetVisualAncestors().OfType<Control>().ToArray();
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged += OnAncestorChanged;
        ScheduleVisibleUpdate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_topLevel != null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorChanged;
        _visibilityAncestors = [];
        ObserveRows([]);
        _images.Clear();
        _gridNameHeights.Clear();
        ClearTexts();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        ClearTexts();
        ScheduleVisibleUpdate();
        InvalidateVisual();
    }

    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) ScheduleVisibleUpdate();
    }

    public (int First, int End) VisibleRange => _layout.VisibleRange(_offset, _offset + Viewport.Height);

    public int IndexOf(FileSystemEntry entry) => _indices.GetValueOrDefault(entry.FullPath, -1);
    public int IndexOfPath(string path) => _indices.GetValueOrDefault(path, -1);
    public int RowIndexAt(Point point)
    {
        if (IsLoading) return -1;
        if (point.X < 0 || point.X >= Bounds.Width || point.Y < 0 || point.Y >= Bounds.Height) return -1;
        return _layout.IndexAt(new Point(point.X, point.Y + _offset));
    }

    public FileSystemEntry? EntryAt(Point point, bool contentOnly = false)
    {
        var index = RowIndexAt(point);
        if (index < 0) return null;
        return !contentOnly || ContentBounds(index).Any(rect => rect.Contains(point)) ? _rows[index] : null;
    }

    public Rect RowBounds(int index) => _layout.Bounds(index).Translate(new Vector(0, -_offset));
    public Rect NameBounds(int index)
    {
        var text = Texts(_rows[index]).Name;
        var row = RowBounds(index);
        var width = Math.Ceiling(Math.Min(IsGrid ? 92 : _columns.Name - 16, text.WidthIncludingTrailingWhitespace));
        var height = Math.Ceiling(text.Height);
        if (IsGrid)
            return new Rect(Math.Round(row.Center.X - width / 2), row.Y + 91, width, height);
        return new Rect(Inset + IconSlot + 8, row.Y + Math.Round((RowHeight - height) / 2), width, height);
    }

    internal Rect GridIconTargetBounds(int index)
    {
        var row = RowBounds(index);
        return new Rect(row.Center.X - 36, row.Y + 14, 72, 72);
    }

    internal Rect GridNameTargetBounds(int index) => NameBounds(index).Inflate(new Thickness(4, 1));

    private FormattedText GridName(FileSystemEntry entry)
    {
        var name = entry.IconDisplayName;
        if (_gridNames.TryGetValue(name, out var text)) return text;
        if (_gridNames.Count >= TextCacheLimit) _gridNames.Clear();
        return _gridNames[name] = Format(name, DetailFontSize, Foreground, 92, 2);
    }

    private IEnumerable<Rect> ContentBounds(int index)
    {
        var text = Texts(_rows[index]);
        var row = RowBounds(index);
        if (IsGrid)
        {
            yield return GridIconTargetBounds(index);
            yield return GridNameTargetBounds(index);
            yield break;
        }
        var y = row.Y;
        yield return new Rect(Inset, y + 4, IconSlot, IconSlot);
        yield return NameBounds(index);
        var modifiedX = Inset + IconSlot + _columns.Name;
        yield return new Rect(modifiedX, y + 4, text.Modified.Width, 22);
        var sizeRight = modifiedX + _columns.Modified + _columns.Size - 12;
        yield return new Rect(sizeRight - text.Size.Width, y + 4, text.Size.Width, 22);
        yield return new Rect(modifiedX + _columns.Modified + _columns.Size, y + 4, text.Kind.Width, 22);
    }

    public IEnumerable<FileSystemEntry> RowsWithCentersIn(double top, double bottom)
        => EntriesInRectangle(new Rect(0, top, Bounds.Width, Math.Max(0, bottom - top)));

    internal IEnumerable<FileSystemEntry> EntriesInRectangle(Rect rectangle)
    {
        if (IsLoading) yield break;
        var (first, end) = _layout.VisibleRange(rectangle.Top, rectangle.Bottom + 0.0001);
        for (var i = first; i < end; i++)
        {
            var center = _layout.Bounds(i).Center;
            if (center.Y >= rectangle.Top && center.Y <= rectangle.Bottom
                && (!IsGrid || center.X >= rectangle.Left && center.X <= rectangle.Right))
                yield return _rows[i];
        }
    }

    internal int MoveVertical(int index, int delta) => _layout.MoveVertical(index, delta);

    public bool ScrollToEntry(FileSystemEntry entry, double? viewportY = null)
    {
        var index = IndexOf(entry);
        if (index < 0) return false;
        var bounds = _layout.Bounds(index);
        var top = bounds.Top;
        var target = viewportY.HasValue ? top - viewportY.Value
            : top < _offset ? top
            : bounds.Bottom > _offset + Viewport.Height ? bounds.Bottom - Viewport.Height : _offset;
        ScrollToOffset(target);
        return true;
    }

    public void ScrollToOffset(double y)
    {
        // Publish geometry before asking the presenter to coerce the desired offset.
        RaiseScrollInvalidated(EventArgs.Empty);
        if (this.FindAncestorOfType<ScrollViewer>() is { } owner)
            owner.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, ClampOffset(y)));
        else Offset = new Vector(0, y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointerPosition = e.GetPosition(this);
        ToolTip.SetTip(this, EditingPath == null ? EntryAt(_pointerPosition.Value)?.DisplayName : null);
        InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerPosition = null;
        InvalidateVisual();
    }

    private void ScheduleVisibleUpdate()
    {
        if (!_attached || _visibleUpdateQueued) return;
        _visibleUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _visibleUpdateQueued = false;
            if (!_attached) return;
            var (first, end) = VisibleRange;
            var visible = IsEffectivelyVisible && !IsLoading ? Enumerable.Range(first, end - first).Select(i => _rows[i]).ToArray() : [];
            ObserveRows(visible);
            _images.UpdateVisible(visible, FileThumbnailSizing.GetPixelSize(IsGrid ? 56 : 18, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1), IsGrid ? 128 : 48);
        }, DispatcherPriority.Background);
    }

    private void ObserveRows(IEnumerable<FileSystemEntry> visible)
    {
        var next = visible.ToHashSet();
        foreach (var old in _observed.Where(e => !next.Contains(e)).ToArray())
        {
            old.PropertyChanged -= OnEntryChanged;
            _observed.Remove(old);
        }
        foreach (var entry in next)
            if (_observed.Add(entry)) entry.PropertyChanged += OnEntryChanged;
    }
    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateVisual();
        if (e.PropertyName is nameof(FileSystemEntry.IconUrl) or nameof(FileSystemEntry.ThumbnailUrl))
            ScheduleVisibleUpdate();
    }

    private void RenderSkeleton(DrawingContext context)
    {
        using var opacity = context.PushOpacity(0.16);
        var rows = (int)Math.Ceiling(Bounds.Height / _layout.ItemHeight);
        for (var i = 0; i < rows; i++)
        {
            var y = i * _layout.ItemHeight;
            if (IsGrid)
            {
                for (var column = 0; column < _layout.Columns; column++)
                {
                    var center = FastFileListLayout.GridInset + column * FastFileListLayout.CellWidth + FastFileListLayout.CellWidth / 2;
                    Bar(center - 28, y + 22, 56, 56);
                    Bar(center - 40, y + 93, 80, 10);
                }
                continue;
            }
            Bar(Inset + 2, y + 6, 18, 18);
            var nameX = Inset + IconSlot + 8;
            Bar(nameX, y + 10, Math.Max(0, _columns.Name - 24) * (0.40 + i % 4 * 0.12), 10);
            var modifiedX = Inset + IconSlot + _columns.Name;
            Bar(modifiedX, y + 10, Math.Max(0, _columns.Modified - 16) * 0.70, 10);
            var sizeWidth = Math.Max(0, _columns.Size - 24) * 0.60;
            Bar(modifiedX + _columns.Modified + _columns.Size - 12 - sizeWidth, y + 10, sizeWidth, 10);
            Bar(modifiedX + _columns.Modified + _columns.Size, y + 10, Math.Max(0, _columns.Type - 16) * 0.55, 10);
        }
        LastRenderedSkeletonRowCount = rows;

        void Bar(double x, double y, double width, double height)
        {
            width = Math.Min(width, Bounds.Width - x - Inset);
            if (width > 0)
                context.DrawRectangle(Secondary, null, new RoundedRect(new Rect(x, y, width, height), 3));
        }
    }

    public override void Render(DrawingContext context)
    {
        var start = Stopwatch.GetTimestamp();
        context.DrawRectangle(Background, null, new Rect(Bounds.Size));
        LastRenderedSkeletonRowCount = 0;
        LastRenderedHeaderCount = 0;
        if (IsLoading)
        {
            RenderSkeleton(context);
            LastRenderedRowCount = 0;
            LastRenderMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return;
        }
        var (first, end) = VisibleRange;
        foreach (var section in _layout.VisibleHeaders(_offset, _offset + Bounds.Height))
        {
            var label = $"{section.Name} · {section.Count} 项";
            if (!_headerTexts.TryGetValue(label, out var text))
            {
                if (_headerTexts.Count >= TextCacheLimit) _headerTexts.Clear();
                var title = Format(section.Name!, DetailFontSize, Foreground, Math.Max(1, Bounds.Width - 28));
                title.SetFontWeight(FontWeight.SemiBold);
                _headerTexts[label] = text = (title, Format($"· {section.Count} 项", CaptionFontSize, Muted, Math.Max(1, Bounds.Width - 28)));
            }
            var y = section.Top - _offset + 6;
            context.DrawText(text.Title, new Point(14, y + Math.Round((GroupMinHeight - Math.Ceiling(text.Title.Height)) / 2)));
            var countX = 14 + Math.Ceiling(text.Title.Width) + 6;
            context.DrawText(text.Count, new Point(countX, y + Math.Round((GroupMinHeight - Math.Ceiling(text.Count.Height)) / 2)));
            var lineX = countX + Math.Ceiling(text.Count.Width) + 10;
            if (lineX < Bounds.Width - 14)
                context.DrawRectangle(Divider, null, new Rect(lineX, y + Math.Round((GroupMinHeight - 1) / 2), Bounds.Width - 14 - lineX, 1));
            LastRenderedHeaderCount++;
        }
        var hover = _pointerPosition is { } position ? RowIndexAt(position) : -1;
        for (var index = first; index < end; index++)
        {
            var entry = _rows[index];
            var row = RowBounds(index);
            var fill = entry.IsSelected ? index == hover ? SelectedHover : Selected
                : index == hover ? Hover : null;
            if (IsGrid)
            {
                RenderGridEntry(context, index);
                continue;
            }
            context.DrawRectangle(fill, HasKeyboardFocus(entry) ? new Pen(FocusRing, 1) : null, new RoundedRect(row.Deflate(0.5), Math.Max(0, RowCornerRadius.TopLeft - 0.5)));
            using var opacity = context.PushOpacity(entry.IsCut ? 0.45 : 1);
            context.DrawRectangle(null, null, new RoundedRect(row.Deflate(1), RowCornerRadius), OutlineFor(entry));
            if (entry.FullPath == _dropPath)
                foreach (var content in ContentBounds(index)) context.DrawRectangle(DropBrush, null, content);
            var iconRect = new Rect(Inset + 2, row.Y + 6, 18, 18);
            DrawIcon(context, entry, iconRect);
            var text = Texts(entry);
            if (entry.FullPath != EditingPath) DrawText(text.Name, Inset + IconSlot + 8);
            var modifiedX = Inset + IconSlot + _columns.Name;
            DrawText(text.Modified, modifiedX);
            DrawText(text.Size, modifiedX + _columns.Modified + _columns.Size - 12 - Math.Ceiling(text.Size.Width));
            DrawText(text.Kind, modifiedX + _columns.Modified + _columns.Size);
            void DrawText(FormattedText value, double x) => context.DrawText(value,
                new Point(Math.Round(x), row.Y + Math.Round((RowHeight - Math.Ceiling(value.Height)) / 2)));
        }
        LastRenderedRowCount = end - first;
        LastRenderMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        FileListPerformanceMetrics.FastListRendered(LastRenderMilliseconds, LastRenderedRowCount);
    }

    private bool HasKeyboardFocus(FileSystemEntry entry)
        => IsFocused && PseudoClasses.Contains(":focus-visible") && entry.FullPath == KeyboardFocusPath;

    private BoxShadows OutlineFor(FileSystemEntry entry)
        => HasKeyboardFocus(entry) ? FocusOutline : entry.IsSelected ? SelectionOutline : default;

    private void RenderGridEntry(DrawingContext context, int index)
    {
        var entry = _rows[index];
        var row = RowBounds(index);
        var name = NameBounds(index);
        using var opacity = context.PushOpacity(entry.IsCut ? 0.45 : 1);
        DrawTarget(GridIconTargetBounds(index), RowCornerRadius);
        if (entry.FullPath != EditingPath) DrawTarget(GridNameTargetBounds(index), new CornerRadius(4));
        DrawIcon(context, entry, new Rect(row.Center.X - 28, row.Y + 22, 56, 56));
        var text = Texts(entry);
        if (entry.FullPath != EditingPath) context.DrawText(text.Name, name.Position);
        if (entry.IsVirtual)
            context.DrawText(text.Size, new Point(Math.Round(row.Center.X - Math.Ceiling(text.Size.Width) / 2), name.Bottom + 3));

        void DrawTarget(Rect target, CornerRadius radius)
        {
            var hovered = _pointerPosition is { } point && target.Contains(point);
            var fill = entry.FullPath == _dropPath ? DropBrush
                : entry.IsSelected ? hovered ? SelectedHover : Selected : hovered ? Hover : null;
            context.DrawRectangle(fill, null, new RoundedRect(target, radius), OutlineFor(entry));
        }
    }

    private void DrawIcon(DrawingContext context, FileSystemEntry entry, Rect bounds)
    {
        var image = _images.Get(entry);
        if (image != null)
        {
            var scale = Math.Min(bounds.Width / image.Size.Width, bounds.Height / image.Size.Height);
            var size = new Size(image.Size.Width * scale, image.Size.Height * scale);
            context.DrawImage(image, new Rect(bounds.Center - new Vector(size.Width / 2, size.Height / 2), size));
        }
        else
        {
            using var transform = context.PushTransform(Matrix.CreateScale(bounds.Width / 24, bounds.Height / 24)
                * Matrix.CreateTranslation(bounds.X, bounds.Y));
            context.DrawGeometry(Secondary, null, entry.IsDirectory ? _folderFallback : _fileFallback);
        }
        if (!entry.HasGitBadge) return;
        var badgeSize = IsGrid ? 16 : 10;
        var outset = IsGrid ? 4 : 2;
        var badge = new Rect(bounds.Right - badgeSize + outset, bounds.Bottom - badgeSize + outset, badgeSize, badgeSize);
        var stroke = IsGrid ? 1.5 : 1;
        context.DrawRectangle(Brush.Parse(entry.GitBadgeColor), new Pen(Brushes.White, stroke),
            badge.Deflate(stroke / 2), (badgeSize - stroke) / 2, (badgeSize - stroke) / 2);
        if (!_badges.TryGetValue(entry.GitBadgeText, out var glyph))
            _badges[entry.GitBadgeText] = glyph = new FormattedText(entry.GitBadgeText, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(FontFamily, weight: FontWeight.Bold), IsGrid ? 9 : 6, Brushes.White);
        context.DrawText(glyph, new Point(Math.Round(badge.Center.X - Math.Ceiling(glyph.Width) / 2), Math.Round(badge.Center.Y - Math.Ceiling(glyph.Height) / 2)));
    }

    private RowText Texts(FileSystemEntry entry)
    {
        if (_texts.TryGetValue(entry.FullPath, out var node))
        {
            if (node.Value.Matches(entry))
            {
                _textLru.Remove(node);
                _textLru.AddFirst(node);
                return node.Value;
            }
            _texts.Remove(entry.FullPath);
            _textLru.Remove(node);
        }
        var text = new RowText(entry,
            IsGrid ? GridName(entry) : Format(entry.DisplayName, FontSize, Foreground, _columns.Name - 16),
            CellText(FileListColumn.Modified, IsGrid ? "" : entry.ModifiedText, _columns.Modified - 8),
            IsGrid ? Format(entry.VirtualCountText, MetaFontSize, Foreground, 100) : CellText(FileListColumn.Size, entry.FormattedSize, _columns.Size - 12),
            CellText(FileListColumn.Type, IsGrid ? "" : entry.KindText, _columns.Type - 8));
        _texts[entry.FullPath] = _textLru.AddFirst(text);
        if (_texts.Count > TextCacheLimit && _textLru.Last is { } last)
        {
            _texts.Remove(last.Value.Entry.FullPath);
            _textLru.RemoveLast();
        }
        return text;
    }
    private FormattedText Format(string value, double size, IBrush? foreground, double width, int lines = 1) => new(
        value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily, weight: FontWeight), size, foreground)
    { MaxTextWidth = Math.Max(1, width), MaxLineCount = lines, Trimming = lines > 1 ? TextTrimming.None : TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Left };
    private FormattedText CellText(FileListColumn column, string value, double width)
    {
        var key = (column, value);
        if (_cellTexts.TryGetValue(key, out var text)) return text;
        text = Format(value, DetailFontSize, Secondary, width);
        if (_cellTexts.Count >= TextCacheLimit) _cellTexts.Remove(_cellTextOrder.Dequeue());
        _cellTexts.Add(key, text);
        _cellTextOrder.Enqueue(key);
        return text;
    }
    private void ClearTexts()
    {
        _texts.Clear(); _textLru.Clear(); _cellTexts.Clear(); _cellTextOrder.Clear(); _badges.Clear(); _headerTexts.Clear(); _gridNames.Clear();
    }
    private sealed record RowText(FileSystemEntry Entry, FormattedText Name, FormattedText Modified, FormattedText Size, FormattedText Kind)
    {
        public bool Matches(FileSystemEntry other) => Entry.Name == other.Name && Entry.Size == other.Size
            && Entry.LastModified == other.LastModified && Entry.Extension == other.Extension
            && Entry.IsDirectory == other.IsDirectory && Entry.IconKey == other.IconKey
            && Entry.IsVirtual == other.IsVirtual && Entry.VirtualItemCount == other.VirtualItemCount
            && Entry.VirtualFolderType == other.VirtualFolderType;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FastListPeer(this);
    private sealed class FastListPeer(FastFileList owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetClassNameCore() => nameof(FastFileList);
    }
}
