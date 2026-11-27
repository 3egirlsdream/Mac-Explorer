using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiquidGlassAvaloniaUI;
using Icons = MacExplorer.Assets.Icons;
using MacExplorer.Models;
using MacExplorer.Performance;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

/// <summary>Visible files and group headers share geometry for painting, selection and scrolling.</summary>
public sealed class FastFileList : Control, ILogicalScrollable
{
    public const double RowHeight = 30;
    internal const double Inset = 13;
    internal const double IconSlot = 22;
    internal const double TreeStep = 16;
    internal const double TreeDisclosureWidth = 16;
    private const int TextCacheLimit = 1024;

    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Background));
    public static readonly StyledProperty<bool> IsLoadingProperty = AvaloniaProperty.Register<FastFileList, bool>(nameof(IsLoading));
    public static readonly StyledProperty<bool> IsGridProperty = AvaloniaProperty.Register<FastFileList, bool>(nameof(IsGrid));
    public static readonly StyledProperty<int> GridIconSizeProperty =
        AvaloniaProperty.Register<FastFileList, int>(nameof(GridIconSize), FileThumbnailSizing.DefaultGridSize,
            coerce: (_, value) => Math.Clamp(value, 32, 128));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Foreground), Brushes.Black);
    public static readonly StyledProperty<IBrush?> SecondaryProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Secondary), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> HoverProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Hover));
    public static readonly StyledProperty<IBrush?> SelectedProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(Selected));
    public static readonly StyledProperty<IBrush?> SelectedHoverProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(SelectedHover));
    public static readonly StyledProperty<IBrush?> AlternateRowProperty = AvaloniaProperty.Register<FastFileList, IBrush?>(nameof(AlternateRow));
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
    public static readonly StyledProperty<BoxShadows> ThumbnailShadowProperty = AvaloniaProperty.Register<FastFileList, BoxShadows>(nameof(ThumbnailShadow));
    public static readonly StyledProperty<BoxShadows> SelectionOutlineProperty = AvaloniaProperty.Register<FastFileList, BoxShadows>(nameof(SelectionOutline));
    public static readonly StyledProperty<BoxShadows> FocusOutlineProperty = AvaloniaProperty.Register<FastFileList, BoxShadows>(nameof(FocusOutline));
    public static readonly StyledProperty<CornerRadius> RowCornerRadiusProperty = AvaloniaProperty.Register<FastFileList, CornerRadius>(nameof(RowCornerRadius), new CornerRadius(4));

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public bool IsLoading { get => GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public bool IsGrid { get => GetValue(IsGridProperty); set => SetValue(IsGridProperty, value); }
    public int GridIconSize { get => GetValue(GridIconSizeProperty); set => SetValue(GridIconSizeProperty, value); }
    internal double GridNameWidth => GridIconSize + 36;
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? Secondary { get => GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }
    public IBrush? Hover { get => GetValue(HoverProperty); set => SetValue(HoverProperty, value); }
    public IBrush? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public IBrush? SelectedHover { get => GetValue(SelectedHoverProperty); set => SetValue(SelectedHoverProperty, value); }
    public IBrush? AlternateRow { get => GetValue(AlternateRowProperty); set => SetValue(AlternateRowProperty, value); }
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
    public BoxShadows ThumbnailShadow { get => GetValue(ThumbnailShadowProperty); set => SetValue(ThumbnailShadowProperty, value); }
    public BoxShadows SelectionOutline { get => GetValue(SelectionOutlineProperty); set => SetValue(SelectionOutlineProperty, value); }
    public BoxShadows FocusOutline { get => GetValue(FocusOutlineProperty); set => SetValue(FocusOutlineProperty, value); }
    public CornerRadius RowCornerRadius { get => GetValue(RowCornerRadiusProperty); set => SetValue(RowCornerRadiusProperty, value); }

    // Non-file locations reuse row geometry and painting without requesting file images.
    internal Func<FileSystemEntry, IReadOnlyList<string>>? DetailTextProvider { get; set; }
    internal Geometry? DetailIcon { get; set; }
    internal IReadOnlyList<double>? DetailColumnWidths { get; set; }
    internal bool NameOnly
    {
        get => _images.TypeIconsOnly;
        set => _images.TypeIconsOnly = value;
    }
    internal bool ShowArchiveThumbnails
    {
        set { _images.ShowArchiveThumbnails = value; ScheduleVisibleUpdate(); InvalidateVisual(); }
    }
    internal string? SelectedPath { get; set; }
    private IReadOnlyList<FileSystemEntry> _rows = Array.Empty<FileSystemEntry>();
    private IReadOnlyList<FileTreeRow> _treeRows = [];
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<RowText>> _texts = new(StringComparer.Ordinal);
    private readonly LinkedList<RowText> _textLru = new();
    private readonly Dictionary<(FileListColumn Column, string Value), CachedText> _cellTexts = [];
    private readonly Queue<(FileListColumn Column, string Value)> _cellTextOrder = new();
    private readonly HashSet<FileSystemEntry> _observed = [];
    private readonly Dictionary<(bool IsGrid, string Text), CachedText> _badges = [];
    private static readonly Dictionary<string, IBrush> GitBadgeBrushes = new(StringComparer.Ordinal);
    private static readonly IPen GitBadgePenGrid = new Pen(Brushes.White, 1.5);
    private static readonly IPen GitBadgePenList = new Pen(Brushes.White, 1);
    private readonly FastFileListImages _images = new();
    internal FastFileListImages Images => _images;
    private readonly FastFileListLayout _layout = new();
    private IReadOnlyList<FastFileListGroup> _groups = [];
    private readonly Dictionary<string, (CachedText Title, CachedText Count)> _headerTexts = [];
    private readonly Dictionary<string, CachedText> _gridNames = [];
    private readonly Dictionary<(string Name, double Width), CachedText> _listNames = [];
    private bool _hasVirtualRows;
    private readonly Geometry _fileFallback = Geometry.Parse(Icons.File);
    private readonly Geometry _folderFallback = Geometry.Parse(Icons.Folder);
    private readonly Geometry _chevronRight = Geometry.Parse(Icons.ChevronRight);
    private readonly Geometry _chevronDown = Geometry.Parse(Icons.ChevronDown);
    private FileListColumnWidths _columns = FileListColumnLayoutService.Defaults;
    private FastFileListDetailColumn[] _detailColumns =
        FastFileListDetailColumn.Build(FileListColumnLayoutService.Defaults, Inset + IconSlot);
    private double _columnOverflow;
    private double _horizontalOffset;
    private double _offset;
    private bool _visibleUpdateQueued;
    private bool _attached;
    private TopLevel? _topLevel;
    private Control[] _visibilityAncestors = [];
    private Point? _pointerPosition;
    private string? _dropPath;
    private string? _editingPath;
    private bool _folderCoversEnabled;
    private Dictionary<string, MorphStart>? _morphStarts;
    private bool _morphFromGrid;
    private double _morphProgress = 1;
    private long _morphStartTimestamp;
    private bool _morphTicked;
    private int _morphGeneration;
    private bool _morphDeferredClearTexts;
    private bool _morphDeferredClearGridNames;
    private bool _morphDeferredClearListTexts;
    private bool _morphDeferredVisibleUpdate;
    private IDisposable? _morphGlassSuppression;
    private IDisposable? _pendingGlassSuppressionRelease;
    private readonly List<MorphDrawItem> _morphItems = new(64);
    private readonly List<MorphAppearItem> _morphAppearItems = new(64);
    internal double PendingMorphStartOffsetY { get; set; }

    private readonly record struct MorphStart(Rect Icon, Rect Name, Rect Row, Rect IconHighlight, Rect NameHighlight, CachedText? CachedName);
    private readonly record struct MorphDrawItem(
        int Index,
        FileSystemEntry Entry,
        Rect Row,
        Rect Icon,
        Rect IconHighlight,
        Rect NameHighlight,
        Point NamePos,
        CachedText? StartName,
        RowText? TargetText,
        IBrush? Fill,
        IBrush? NameFill,
        bool IsCut,
        bool DrawRowOutline,
        bool DrawNameHighlight,
        bool DrawDisclosure);
    private readonly record struct MorphAppearItem(int Index, FileSystemEntry Entry, Rect Row, Rect Icon, RowText? TargetText);
    internal bool IsLayoutMorphActive => _morphStarts != null && _morphProgress < 1;

    internal Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>>? ThumbnailProvider
    {
        get => _images.ThumbnailProvider;
        set { _images.ThumbnailProvider = value; ScheduleVisibleUpdate(); }
    }
    internal Func<FileSystemEntry, int, CancellationToken, Task<ThumbnailResult?>>? FolderCoverProvider
    {
        get => _images.FolderCoverProvider;
        set { _images.FolderCoverProvider = value; InvalidateFolderCovers(); }
    }
    internal bool FolderCoversEnabled
    {
        get => _folderCoversEnabled;
        set
        {
            if (_folderCoversEnabled == value) return;
            _folderCoversEnabled = value;
            if (!value) _images.CancelFolderCoverRequests();
            ScheduleVisibleUpdate();
            InvalidateVisual();
        }
    }

    internal void InvalidateFolderCovers()
    {
        _images.InvalidateFolderCovers();
        ScheduleVisibleUpdate();
    }
    internal FolderPhotoCoverStyle FolderIconStyle
    {
        get => _images.FolderStyle;
        set
        {
            if (_images.FolderStyle == value) return;
            _images.FolderStyle = value;
            ScheduleVisibleUpdate();
            InvalidateVisual();
        }
    }
    public IReadOnlyList<FileSystemEntry> Rows => _rows;
    public bool IsTree => _treeRows.Count == _rows.Count && _treeRows.Count > 0;
    internal int CachedTextCount => _texts.Count;
    internal int CachedGridNameCount => _gridNames.Count;
    internal int ObservedRowCount => _observed.Count;
    internal int LastRenderedRowCount { get; private set; }
#if DEMO_AUTOMATION
    internal int DemoPendingImages => _images.PendingCount;
#endif
    internal int LastRenderedSkeletonRowCount { get; private set; }
    internal int LastRenderedHeaderCount { get; private set; }
    internal bool HasThumbnail(FileSystemEntry entry)
    {
        _images.Get(entry, IsGrid && _folderCoversEnabled, out var isThumbnail);
        return isThumbnail;
    }
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
        set => SetFileColumns(value, _columnOverflow);
    }

    internal void SetFileColumns(FileListColumnWidths widths, double overflow)
    {
        if (_columns == widths && _columnOverflow == overflow) return;
        if (_columns != widths)
        {
            var details = FastFileListDetailColumn.Build(widths, Inset + IconSlot);
            var detailColumnsChanged = _columns.Modified != widths.Modified
                || _columns.Size != widths.Size
                || _columns.Type != widths.Type;
            var detailVisibilityChanged = (_columns.Modified == 0) != (widths.Modified == 0)
                || (_columns.Size == 0) != (widths.Size == 0)
                || (_columns.Type == 0) != (widths.Type == 0);
            _columns = widths;
            _detailColumns = details;
            if (detailColumnsChanged || detailVisibilityChanged)
            {
                ClearCellTexts();
                PruneStaleListTexts(clearAllListNames: false, evictAllListRows: true);
            }
            else if (IsLayoutMorphActive)
            {
                _morphDeferredClearListTexts = true;
            }
            else
            {
                PruneStaleListTexts(clearAllListNames: false, evictAllListRows: false);
            }
        }
        _columnOverflow = overflow;
        UpdateScrollGeometry();
        if (_pointerPosition is { } position) ToolTip.SetTip(this, GetPointerTip(position));
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    public FastFileList()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        _images.Changed += () => { if (!IsLayoutMorphActive) InvalidateVisual(); };
    }

    static FastFileList()
    {
        AffectsRender<FastFileList>(IsGridProperty, GridIconSizeProperty, IsLoadingProperty, BackgroundProperty, ForegroundProperty, SecondaryProperty, HoverProperty,
            SelectedProperty, SelectedHoverProperty, AlternateRowProperty, DropBrushProperty, FontFamilyProperty, FontWeightProperty,
            FontSizeProperty, DetailFontSizeProperty, CaptionFontSizeProperty, MetaFontSizeProperty, GroupMinHeightProperty,
            MutedProperty, DividerProperty, FocusRingProperty, SelectionOutlineProperty, FocusOutlineProperty, RowCornerRadiusProperty, ThumbnailShadowProperty);
    }

    public void SetRows(IReadOnlyList<FileSystemEntry> rows)
        => SetRows(rows, []);

    internal void SetRows(IReadOnlyList<FileSystemEntry> rows, IReadOnlyList<FastFileListGroup> groups)
    {
        var hadTree = _treeRows.Count > 0;
        _treeRows = [];
        if (HasSameEntries(rows) && HasSameGroups(groups))
        {
            if (hadTree) InvalidateVisual();
            return;
        }
        ApplyRows(rows, groups);
    }

    internal void SetTreeRows(IReadOnlyList<FileTreeRow> rows, IReadOnlyList<FastFileListGroup> groups)
    {
        _treeRows = rows;
        if (HasSameTreeEntries(rows) && HasSameGroups(groups))
        {
            InvalidateVisual();
            return;
        }
        ApplyRows(rows.Select(row => row.Entry).ToArray(), groups);
    }

    private bool HasSameEntries(IReadOnlyList<FileSystemEntry> rows)
    {
        if (_layout.Count != _rows.Count || rows.Count != _rows.Count) return false;
        for (var i = 0; i < rows.Count; i++)
            if (!ReferenceEquals(rows[i], _rows[i])) return false;
        return true;
    }

    private bool HasSameTreeEntries(IReadOnlyList<FileTreeRow> rows)
    {
        if (_layout.Count != _rows.Count || rows.Count != _rows.Count) return false;
        for (var i = 0; i < rows.Count; i++)
            if (!ReferenceEquals(rows[i].Entry, _rows[i])) return false;
        return true;
    }

    private bool HasSameGroups(IReadOnlyList<FastFileListGroup> groups)
    {
        if (groups.Count != _groups.Count) return false;
        for (var i = 0; i < groups.Count; i++)
            if (groups[i] != _groups[i]) return false;
        return true;
    }

    private void ApplyRows(IReadOnlyList<FileSystemEntry> rows, IReadOnlyList<FastFileListGroup> groups)
    {
        if (!ShouldPreserveMorph(rows))
            CancelLayoutMorph();
        _rows = rows;
        _groups = groups;
        ClearHeaderTexts();
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
        // Reserve two filename lines without shaping every offscreen filename.
        // Actual glyphs are still created only for the viewport, with stable cell geometry.
        double nameHeight = 0, countHeight = 0;
        if (IsGrid)
        {
            var name = Format("Ag\nAg", DetailFontSize, Foreground, GridNameWidth, 2);
            nameHeight = Math.Ceiling(name.Height);
            name.Release();
            if (_hasVirtualRows)
            {
                var count = Format(MacExplorer.Services.Impl.LocalizationText.Get("{0} 张照片", 0), MetaFontSize, Secondary, GridNameWidth + 8);
                countHeight = Math.Ceiling(count.Height);
                count.Release();
            }
        }
        var gridRowHeight = GridIconSize + 50 + nameHeight;
        _layout.Build(_rows.Count, _groups, IsGrid, Bounds.Width, _hasVirtualRows,
            IsGrid && _hasVirtualRows ? index => gridRowHeight + (_rows[index].IsVirtual ? 2 + countHeight : 0) : null,
            GroupMinHeight + 6, GridIconSize + 64, gridRowHeight);
        ClearHeaderTexts();
    }

    private bool HasHorizontalFileScroll => CanHorizontallyScroll && !IsGrid && !NameOnly && DetailTextProvider == null;
    public Size Extent => new(HasHorizontalFileScroll
        ? Viewport.Width + _columnOverflow : Viewport.Width, _layout.Height);
    public Size Viewport => Bounds.Size;
    public Vector Offset
    {
        get => new(_horizontalOffset, _offset);
        set
        {
            var x = ClampHorizontalOffset(value.X);
            var y = ClampOffset(value.Y);
            if (_horizontalOffset == x && _offset == y) return;
            if (_morphTicked) CancelLayoutMorph();
            var movedHorizontally = _horizontalOffset != x;
            var movedVertically = _offset != y;
            _horizontalOffset = x;
            _offset = y;
            if (movedVertically) ScheduleVisibleUpdate();
            if (movedHorizontally && _pointerPosition is { } position) ToolTip.SetTip(this, GetPointerTip(position));
            InvalidateVisual();
            ViewportChanged?.Invoke();
        }
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public bool IsLogicalScrollEnabled => true;
    public Size ScrollSize => new(HasHorizontalFileScroll ? 32 : 0, _layout.ItemHeight);
    public Size PageScrollSize => new(HasHorizontalFileScroll ? Viewport.Width : 0, Math.Max(_layout.ItemHeight, Viewport.Height - _layout.ItemHeight));
    public event EventHandler? ScrollInvalidated;
    public event Action? ViewportChanged;
    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);
    public bool BringIntoView(Control target, Rect targetRect) => false;
    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;
    private double ClampOffset(double value) => double.IsFinite(value)
        ? Math.Clamp(value, 0, Math.Max(0, Extent.Height - Viewport.Height)) : 0;

    private double ClampHorizontalOffset(double value) => double.IsFinite(value)
        ? Math.Clamp(value, 0, Math.Max(0, Extent.Width - Viewport.Width)) : 0;

    private void UpdateScrollGeometry()
    {
        _horizontalOffset = ClampHorizontalOffset(_horizontalOffset);
        RaiseScrollInvalidated(EventArgs.Empty);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? availableSize.Width : Inset * 2 + IconSlot + _columns.Total,
        double.IsFinite(availableSize.Height) ? availableSize.Height : Math.Min(Extent.Height, RowHeight * 20));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var typographyChanged = change.Property == FontFamilyProperty || change.Property == FontWeightProperty || change.Property == FontSizeProperty
            || change.Property == DetailFontSizeProperty || change.Property == MetaFontSizeProperty
            || change.Property == CaptionFontSizeProperty;
        var gridSizeChanged = IsGrid && change.Property == GridIconSizeProperty;
        var canMorph = (change.Property == IsGridProperty || gridSizeChanged)
            && ViewMotion.ShouldAnimate(this) && !IsLoading && _rows.Count > 0
            && Bounds.Width > 16 && Bounds.Height > 16 && _layout.Count == _rows.Count;
        var oldIsGrid = _layout.IsGrid;
        var oldIconSize = change.Property == GridIconSizeProperty ? change.GetOldValue<int>() : GridIconSize;
        var morphSnapshot = canMorph ? CaptureMorphSnapshot(oldIsGrid, oldIconSize) : null;
        var willMorph = morphSnapshot is { Count: > 0 };

        if (typographyChanged || change.Property == ForegroundProperty || change.Property == SecondaryProperty
            || change.Property == MutedProperty)
        {
            ClearTexts();
        }
        else if (gridSizeChanged)
        {
            if (willMorph)
                _morphDeferredClearGridNames = true;
            else
                ClearGridNames();
        }
        if (change.Property == IsGridProperty)
            RenderOptions.SetBitmapInterpolationMode(this, !willMorph && IsGrid ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.LowQuality);
        if (change.Property == IsLoadingProperty)
        {
            if (IsLoading) CancelLayoutMorph();
            _pointerPosition = null;
            ToolTip.SetTip(this, null);
            ScheduleVisibleUpdate();
        }
        if (change.Property == BoundsProperty || change.Property == IsGridProperty || gridSizeChanged || typographyChanged || change.Property == GroupMinHeightProperty)
        {
            var heightOnlyBoundsChange = change.Property == BoundsProperty
                && _layout.Count == _rows.Count
                && Math.Abs(Bounds.Width - _layout.Width) < 1;
            if (heightOnlyBoundsChange)
            {
                _offset = ClampOffset(_offset);
                UpdateScrollGeometry();
                if (!IsLayoutMorphActive)
                    ScheduleVisibleUpdate();
                ViewportChanged?.Invoke();
            }
            else
            {
                if (!canMorph) CancelLayoutMorph();

                var anchor = VisibleRange.First;
                var y = anchor < _rows.Count ? _layout.Bounds(anchor).Y - _offset : 0;
                BuildLayout();
                if (anchor < _rows.Count) _offset = _layout.Bounds(anchor).Y - y;
                _offset = ClampOffset(_offset);
                UpdateScrollGeometry();
                if (willMorph)
                {
                    _morphDeferredVisibleUpdate = true;
                    StartLayoutMorph(morphSnapshot!, oldIsGrid);
                }
                else
                {
                    ScheduleVisibleUpdate();
                }
                ViewportChanged?.Invoke();
            }
        }
        if (change.Property == IsVisibleProperty)
        {
            if (!IsVisible) CancelLayoutMorph();
            ScheduleVisibleUpdate();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (MacExplorer.Services.Impl.LocalizationService.Current is { } language)
            language.LanguageChanged += RefreshLanguage;
        _attached = true;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null) _topLevel.ScalingChanged += OnScalingChanged;
        _visibilityAncestors = this.GetVisualAncestors().OfType<Control>().ToArray();
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged += OnAncestorChanged;
        ScheduleVisibleUpdate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelLayoutMorph();
        _attached = false;
        if (_topLevel != null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorChanged;
        _visibilityAncestors = [];
        ObserveRows([]);
        _images.Clear();
        ClearTexts();
        if (MacExplorer.Services.Impl.LocalizationService.Current is { } language)
            language.LanguageChanged -= RefreshLanguage;
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

    internal Bitmap? GetEntryBitmap(FileSystemEntry entry) => _images.Get(entry);

    public int IndexOf(FileSystemEntry entry) => _indices.GetValueOrDefault(entry.FullPath, -1);
    public int IndexOfPath(string path) => _indices.GetValueOrDefault(path, -1);
    private double ContentRight => NameOnly ? Bounds.Width : Inset + IconSlot + (DetailColumnWidths?.Sum() ?? _columns.Total);
    internal double ListRowRight => ContentRight - _horizontalOffset;
    private bool DrawFileDetails => !NameOnly && DetailTextProvider == null && _detailColumns.Length != 0;
    private double ListInset => Inset - _horizontalOffset;
    private double NameColumnWidth => NameOnly ? Math.Max(0, Bounds.Width - Inset - IconSlot) : _columns.Name;

    public int RowIndexAt(Point point)
    {
        if (IsLoading) return -1;
        if (point.X < 0 || point.X >= Bounds.Width || point.Y < 0 || point.Y >= Bounds.Height) return -1;
        if (!IsGrid && point.X >= ListRowRight) return -1;
        return _layout.IndexAt(new Point(point.X, point.Y + _offset));
    }

    public FileSystemEntry? EntryAt(Point point, bool contentOnly = false)
    {
        var index = RowIndexAt(point);
        if (index < 0) return null;
        return !contentOnly || ContentBounds(index).Any(rect => rect.Contains(point)) ? _rows[index] : null;
    }

    internal bool IsGroupHeaderAt(Point point)
    {
        var y = point.Y + _offset;
        return _layout.VisibleHeaders(y, y + 1).Any(section => y < section.ContentTop);
    }

    public Rect RowBounds(int index)
    {
        var row = _layout.Bounds(index).Translate(new Vector(0, -_offset));
        return IsGrid ? row : new Rect(row.X, row.Y, Math.Min(row.Width, ListRowRight), row.Height);
    }

    public Rect NameBounds(int index)
    {
        var text = Texts(_rows[index], index).Name;
        var row = RowBounds(index);
        var width = Math.Ceiling(Math.Min(IsGrid ? GridNameWidth : Math.Max(1, NameColumnWidth - 16), text.WidthIncludingTrailingWhitespace));
        var height = Math.Ceiling(text.Height);
        if (IsGrid)
            return new Rect(Math.Round(row.Center.X - width / 2), row.Y + GridIconSize + 35, width, height);
        return new Rect(NameX(index), row.Y + Math.Round((RowHeight - height) / 2), width, height);
    }

    internal Rect GridIconTargetBounds(int index)
    {
        var row = RowBounds(index);
        return GridImageBounds(row.Center.X, row.Y).Inflate(8);
    }

    private Rect GridImageBounds(double centerX, double rowY)
        => new(centerX - GridIconSize / 2d, rowY + 22, GridIconSize, GridIconSize);

    internal Rect GridNameTargetBounds(int index) => NameBounds(index).Inflate(new Thickness(4, 1));

    private CachedText GridName(FileSystemEntry entry)
    {
        var name = entry.DisplayName;
        if (_gridNames.TryGetValue(name, out var text)) return text;
        if (_gridNames.Count >= TextCacheLimit)
        {
            foreach (var cached in _gridNames.Values) cached.Release();
            _gridNames.Clear();
        }
        return _gridNames[name] = FormatName(name, DetailFontSize, GridNameWidth, 2);
    }

    private IEnumerable<Rect> ContentBounds(int index)
    {
        var text = Texts(_rows[index], index);
        var row = RowBounds(index);
        if (IsGrid)
        {
            yield return GridIconTargetBounds(index);
            yield return GridNameTargetBounds(index);
            yield break;
        }
        var y = row.Y;
        if (IsTree && _treeRows[index].CanExpand) yield return TreeDisclosureBounds(index);
        yield return new Rect(ListInset + TreeIndent(index), y + 4, IconSlot, IconSlot);
        yield return NameBounds(index);
        if (!DrawFileDetails) yield break;
        foreach (var column in _detailColumns)
            if (text.Cell(column.Column) is { } cell)
                yield return new Rect(column.TextX(cell.Width) - _horizontalOffset, y + 4, cell.Width, 22);
    }

    public IEnumerable<FileSystemEntry> RowsWithCentersIn(double top, double bottom)
        => EntriesInRectangle(new Rect(_horizontalOffset, top, Bounds.Width, Math.Max(0, bottom - top)));

    internal IEnumerable<FileSystemEntry> EntriesInRectangle(Rect rectangle)
    {
        if (IsLoading) yield break;
        // A drag wholly inside the right-hand canvas must not select invisible row area.
        if (!IsGrid && (rectangle.Left >= Math.Min(_horizontalOffset + Bounds.Width, ContentRight) || rectangle.Right <= 0))
            yield break;
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

    public int TreeDisclosureIndexAt(Point point)
    {
        var index = RowIndexAt(point);
        return index >= 0 && IsTree && _treeRows[index].CanExpand
            && TreeDisclosureBounds(index).Contains(point) ? index : -1;
    }

    public FileTreeRow? TreeRowAt(int index)
        => IsTree && index >= 0 && index < _treeRows.Count ? _treeRows[index] : null;

    private double TreeIndent(int index)
    {
        if (!IsTree) return 0;
        return Math.Min(TreeDisclosureWidth + _treeRows[index].Depth * TreeStep,
            Math.Max(TreeDisclosureWidth, NameColumnWidth - 96));
    }

    private double NameX(int index) => ListInset + IconSlot + 8 + TreeIndent(index);

    private Rect TreeDisclosureBounds(int index)
    {
        var row = RowBounds(index);
        return new Rect(ListInset + TreeIndent(index) - TreeDisclosureWidth, row.Y + 4,
            TreeDisclosureWidth, IconSlot);
    }

    public void ScrollToOffset(double y)
    {
        // Publish geometry before asking the presenter to coerce the desired offset.
        RaiseScrollInvalidated(EventArgs.Empty);
        if (this.FindAncestorOfType<ScrollViewer>() is { } owner)
            owner.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(_horizontalOffset, ClampOffset(y)));
        else Offset = new Vector(_horizontalOffset, y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        UpdatePointerPosition(position);
        ToolTip.SetTip(this, GetPointerTip(position));
    }

    private string? GetPointerTip(Point position)
    {
        if (EditingPath != null) return null;
        var disclosure = TreeDisclosureIndexAt(position);
        if (disclosure >= 0 && _treeRows[disclosure].HasError)
            return LocalizationText.Get("读取失败，点击重试");
        if (disclosure >= 0 && _treeRows[disclosure].IsLoading)
            return LocalizationText.Get("正在读取文件夹");

        var entry = EntryAt(position);
        return !IsGrid && DrawFileDetails && _columns.Type > 0 && position.X >= ListRowRight - _columns.Type
            ? entry?.KindText
            : entry?.DisplayName;
    }

    internal void UpdatePointerPosition(Point point)
    {
        _pointerPosition = point;
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
        if (IsLayoutMorphActive)
        {
            _morphDeferredVisibleUpdate = true;
            return;
        }
        _visibleUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _visibleUpdateQueued = false;
            if (!_attached) return;
            var (first, end) = VisibleRange;
            var visible = IsEffectivelyVisible && !IsLoading ? Enumerable.Range(first, end - first).Select(i => _rows[i]).ToArray() : [];
            ObserveRows(visible);
            var pixels = FileThumbnailSizing.GetPixelSize(IsGrid ? GridIconSize : 18, _topLevel?.RenderScaling ?? 1);
            _images.UpdateVisible(DetailTextProvider == null ? visible : [], pixels,
                IsGrid ? Math.Max(128, pixels) : 48, IsGrid && _folderCoversEnabled, reuseSizes: IsGrid);
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
    private void RefreshLanguage()
    {
        ClearTexts();
        if (_pointerPosition is { } position)
            ToolTip.SetTip(this, GetPointerTip(position));
        InvalidateVisual();
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
                    var center = FastFileListLayout.GridInset + (column + 0.5) * _layout.CellWidth;
                    var image = GridImageBounds(center, y);
                    Bar(image.X, image.Y, image.Width, image.Height);
                    var captionWidth = GridNameWidth - 12;
                    Bar(center - captionWidth / 2, y + GridIconSize + 37, captionWidth, 10);
                }
                continue;
            }
            Bar(ListInset + 2, y + 6, 18, 18);
            var nameX = ListInset + IconSlot + 8;
            Bar(nameX, y + 10, Math.Max(0, NameColumnWidth - 24) * (0.40 + i % 4 * 0.12), 10);
            if (!DrawFileDetails) continue;
            foreach (var column in _detailColumns)
            {
                var width = column.Column switch
                {
                    FileListColumn.Modified => (column.Width - 16) * 0.70,
                    FileListColumn.Size => (column.Width - 24) * 0.60,
                    _ => (column.Width - 16) * 0.55
                };
                Bar(column.TextX(width) - _horizontalOffset, y + 10, width, 10);
            }
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
            var group = _groups.FirstOrDefault(candidate => candidate.Name == section.Name);
            var displayedCount = group.DisplayCount ?? section.Count;
            var label = MacExplorer.Services.Impl.LocalizationText.Get(section.Name!) + " · " + MacExplorer.Services.Impl.LocalizationText.Get("{0} 项", displayedCount);
            if (!_headerTexts.TryGetValue(label, out var text))
            {
                if (_headerTexts.Count >= TextCacheLimit) ClearHeaderTexts();
                var title = Format(MacExplorer.Services.Impl.LocalizationText.Get(section.Name!), DetailFontSize, Secondary, Math.Max(1, Bounds.Width - 28), weight: FontWeight.SemiBold);
                _headerTexts[label] = text = (title, Format("· " + MacExplorer.Services.Impl.LocalizationText.Get("{0} 项", displayedCount), CaptionFontSize, Muted, Math.Max(1, Bounds.Width - 28)));
            }
            var y = section.Top - _offset + 6;
            text.Title.Draw(context, new Point(14, y + Math.Round((GroupMinHeight - Math.Ceiling(text.Title.Height)) / 2)));
            var countX = 14 + Math.Ceiling(text.Title.Width) + 6;
            text.Count.Draw(context, new Point(countX, y + Math.Round((GroupMinHeight - Math.Ceiling(text.Count.Height)) / 2)));
            var lineX = countX + Math.Ceiling(text.Count.Width) + 10;
            if (lineX < Bounds.Width - 14)
                context.DrawRectangle(Divider, null, new Rect(lineX, y + Math.Round((GroupMinHeight - 1) / 2), Bounds.Width - 14 - lineX, 1));
            LastRenderedHeaderCount++;
        }
        var hover = _pointerPosition is { } position ? RowIndexAt(position) : -1;
        if (IsLayoutMorphActive)
        {
            RenderMorphEntries(context, first, end, hover);
            LastRenderedRowCount = end - first;
            LastRenderMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FileListPerformanceMetrics.FastListRendered(LastRenderMilliseconds, LastRenderedRowCount);
            return;
        }
        for (var index = first; index < end; index++)
        {
            var entry = _rows[index];
            var row = RowBounds(index);
            var fill = IsRowSelected(entry) ? index == hover ? SelectedHover : Selected
                : index == hover ? Hover : index % 2 == 1 ? AlternateRow : null;
            if (IsGrid)
            {
                RenderGridEntry(context, index);
                continue;
            }
            context.DrawRectangle(fill, HasKeyboardFocus(entry) ? new Pen(FocusRing, 1) : null, new RoundedRect(row.Deflate(0.5), Math.Max(0, RowCornerRadius.TopLeft - 0.5)));
            using var opacity = context.PushOpacity(!NameOnly && entry.IsCut ? 0.45 : 1);
            context.DrawRectangle(null, null, new RoundedRect(row.Deflate(1), RowCornerRadius), OutlineFor(entry));
            if (entry.FullPath == _dropPath)
                foreach (var content in ContentBounds(index)) context.DrawRectangle(DropBrush, null, content);
            if (IsTree && _treeRows[index].CanExpand)
                DrawTreeDisclosure(context, index);
            var iconRect = new Rect(ListInset + TreeIndent(index) + 2, row.Y + 6, 18, 18);
            DrawIcon(context, entry, iconRect);
            if (DetailTextProvider != null && DetailColumnWidths is { } widths)
            {
                var values = DetailTextProvider(entry);
                var x = Inset + IconSlot;
                for (var column = 0; column < Math.Min(values.Count, widths.Count); column++)
                {
                    var value = Format(values[column], column == 0 ? FontSize : DetailFontSize,
                        column == 0 ? Foreground : Secondary, Math.Max(1, widths[column] - 16));
                    value.Draw(context, new Point(Math.Round(x + (column == 0 ? 8 : 0)),
                        row.Y + Math.Round((RowHeight - Math.Ceiling(value.Height)) / 2)));
                    value.Release();
                    x += widths[column];
                }
                continue;
            }
            var text = Texts(entry, index);
            if (entry.FullPath != EditingPath) DrawRowCell(context, text.Name, NameX(index), row.Y);
            if (DrawFileDetails) DrawDetailCells(context, text, row.Y);
        }
        LastRenderedRowCount = end - first;
        LastRenderMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        FileListPerformanceMetrics.FastListRendered(LastRenderMilliseconds, LastRenderedRowCount);
    }

    private bool HasKeyboardFocus(FileSystemEntry entry)
        => IsFocused && PseudoClasses.Contains(":focus-visible") && entry.FullPath == KeyboardFocusPath;

    private bool IsRowSelected(FileSystemEntry entry) => NameOnly ? entry.FullPath == SelectedPath : entry.IsSelected;

    private BoxShadows OutlineFor(FileSystemEntry entry)
        => HasKeyboardFocus(entry) ? FocusOutline : IsRowSelected(entry) ? SelectionOutline : default;

    private void RenderGridEntry(DrawingContext context, int index)
    {
        var entry = _rows[index];
        var row = RowBounds(index);
        var name = NameBounds(index);
        using var opacity = context.PushOpacity(entry.IsCut ? 0.45 : 1);
        DrawTarget(GridIconTargetBounds(index), RowCornerRadius);
        if (entry.FullPath != EditingPath) DrawTarget(GridNameTargetBounds(index), new CornerRadius(4));
        DrawIcon(context, entry, GridImageBounds(row.Center.X, row.Y));
        var text = Texts(entry, index);
        if (entry.FullPath != EditingPath) text.Name.Draw(context, name.Position);
        if (entry.IsVirtual && text.Size is { } count)
            count.Draw(context, new Point(Math.Round(row.Center.X - Math.Ceiling(count.Width) / 2), name.Bottom + 3));

        void DrawTarget(Rect target, CornerRadius radius)
        {
            var hovered = _pointerPosition is { } point && target.Contains(point);
            var fill = entry.FullPath == _dropPath ? DropBrush
                : entry.IsSelected ? hovered ? SelectedHover : Selected : hovered ? Hover : null;
            context.DrawRectangle(fill, null, new RoundedRect(target, radius), OutlineFor(entry));
        }
    }

    private bool ShouldPreserveMorph(IReadOnlyList<FileSystemEntry> rows)
    {
        if (!IsLayoutMorphActive || _morphStarts == null || rows.Count == 0) return false;
        for (var i = 0; i < rows.Count; i++)
            if (_morphStarts.ContainsKey(rows[i].FullPath))
                return true;
        return false;
    }

    private Dictionary<string, MorphStart> CaptureMorphSnapshot(bool oldIsGrid, int oldIconSize)
    {
        var snapshot = new Dictionary<string, MorphStart>(StringComparer.Ordinal);
        var (first, end) = _layout.VisibleRange(_offset, _offset + Viewport.Height);
        var from = Math.Max(0, first - 8);
        var to = Math.Min(_rows.Count, Math.Min(end + 8, from + 120));
        var wasMorphing = IsLayoutMorphActive;
        var prevT = wasMorphing ? ViewMotion.EaseOutCubic(_morphProgress) : 1;
        var offsetY = PendingMorphStartOffsetY;

        for (var i = from; i < to; i++)
        {
            var entry = _rows[i];
            var raw = _layout.Bounds(i).Translate(new Vector(0, -_offset));
            var row = oldIsGrid ? raw : new Rect(raw.X, raw.Y, Math.Min(raw.Width, ListRowRight), raw.Height);
            Rect icon;
            Rect name;
            Rect iconHighlight;
            Rect nameHighlight;
            CachedText? cachedName = null;
            if (oldIsGrid)
            {
                icon = new Rect(row.Center.X - oldIconSize / 2d, row.Y + 22, oldIconSize, oldIconSize);
                var oldNameMaxWidth = oldIconSize + 36;
                double nameWidth = oldNameMaxWidth;
                double nameHeight = 16;
                if (_texts.TryGetValue(entry.FullPath, out var cached) && cached.Value.IsGrid)
                {
                    cachedName = cached.Value.Name.Retain();
                    nameWidth = Math.Ceiling(Math.Min(oldNameMaxWidth, cached.Value.Name.WidthIncludingTrailingWhitespace));
                    nameHeight = Math.Ceiling(cached.Value.Name.Height);
                }
                else if (_gridNames.TryGetValue(entry.DisplayName, out var gridCached))
                {
                    cachedName = gridCached.Retain();
                    nameWidth = Math.Ceiling(Math.Min(oldNameMaxWidth, gridCached.WidthIncludingTrailingWhitespace));
                    nameHeight = Math.Ceiling(gridCached.Height);
                }
                name = new Rect(Math.Round(row.Center.X - nameWidth / 2), row.Y + oldIconSize + 35, nameWidth, nameHeight);
                iconHighlight = icon.Inflate(8);
                nameHighlight = name.Inflate(new Thickness(4, 1));
            }
            else
            {
                icon = new Rect(ListInset + TreeIndent(i) + 2, row.Y + 6, 18, 18);
                double nameWidth = Math.Max(1, NameColumnWidth - 16 - TreeIndent(i));
                double nameHeight = 16;
                if (_texts.TryGetValue(entry.FullPath, out var cached) && !cached.Value.IsGrid)
                {
                    cachedName = cached.Value.Name.Retain();
                    nameWidth = Math.Ceiling(Math.Min(nameWidth, cached.Value.Name.WidthIncludingTrailingWhitespace));
                    nameHeight = Math.Ceiling(cached.Value.Name.Height);
                }
                else if (_listNames.TryGetValue((entry.DisplayName, nameWidth), out var listCached))
                {
                    cachedName = listCached.Retain();
                    nameWidth = Math.Ceiling(Math.Min(nameWidth, listCached.WidthIncludingTrailingWhitespace));
                    nameHeight = Math.Ceiling(listCached.Height);
                }
                name = new Rect(NameX(i), row.Y + Math.Round((RowHeight - nameHeight) / 2), nameWidth, nameHeight);
                iconHighlight = row;
                nameHighlight = row;
            }

            if (wasMorphing && _morphStarts!.TryGetValue(entry.FullPath, out var existing))
            {
                icon = ViewMotion.LerpRect(existing.Icon, icon, prevT);
                name = ViewMotion.LerpRect(existing.Name, name, prevT);
                row = ViewMotion.LerpRect(existing.Row, row, prevT);
                iconHighlight = ViewMotion.LerpRect(existing.IconHighlight, iconHighlight, prevT);
                nameHighlight = ViewMotion.LerpRect(existing.NameHighlight, nameHighlight, prevT);
                if (cachedName == null && existing.CachedName != null)
                    cachedName = existing.CachedName.Retain();
            }

            if (Math.Abs(offsetY) > 0.5)
            {
                var shift = new Vector(0, offsetY);
                icon = icon.Translate(shift);
                name = name.Translate(shift);
                row = row.Translate(shift);
                iconHighlight = iconHighlight.Translate(shift);
                nameHighlight = nameHighlight.Translate(shift);
            }

            snapshot[entry.FullPath] = new MorphStart(icon, name, row, iconHighlight, nameHighlight, cachedName);
        }
        return snapshot;
    }

    private void ReleaseMorphStarts()
    {
        if (_morphStarts == null) return;
        foreach (var item in _morphStarts.Values)
            item.CachedName?.Release();
        _morphStarts = null;
    }

    private void StartLayoutMorph(Dictionary<string, MorphStart> starts, bool fromGrid)
    {
        ReleaseMorphStarts();
        _morphStarts = starts;
        _morphFromGrid = fromGrid;
        _morphProgress = 0;
        _morphTicked = false;
        _morphStartTimestamp = Stopwatch.GetTimestamp();
        ReleasePendingGlassSuppression();
        _morphGlassSuppression?.Dispose();
        _morphGlassSuppression = LiquidGlassBackdrop.BeginExcludedSceneSuppression(this);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        var generation = ++_morphGeneration;
        if (_topLevel is { } topLevel)
            topLevel.RequestAnimationFrame(ts => OnLayoutMorphFrame(ts, generation));
        else
            CancelLayoutMorph();
    }

    private void OnLayoutMorphFrame(TimeSpan timestamp, int generation)
    {
        if (generation != _morphGeneration || _morphStarts == null) return;
        if (!_attached || !IsEffectivelyVisible || ViewMotion.PrefersReducedMotion)
        {
            CancelLayoutMorph();
            return;
        }
        _morphTicked = true;
        var elapsed = Stopwatch.GetElapsedTime(_morphStartTimestamp).TotalMilliseconds;
        if (elapsed < 1) elapsed = 8;
        _morphProgress = Math.Clamp(elapsed / ViewMotion.MorphDurationMs, 0, 1);
        if (_morphProgress >= 1)
        {
            FinishLayoutMorph(deferGlassRelease: true);
            return;
        }
        InvalidateVisual();
        if (_topLevel is { } topLevel)
            topLevel.RequestAnimationFrame(ts => OnLayoutMorphFrame(ts, generation));
        else
            CancelLayoutMorph();
    }

    private void ReleasePendingGlassSuppression()
    {
        _pendingGlassSuppressionRelease?.Dispose();
        _pendingGlassSuppressionRelease = null;
    }

    private void FinishLayoutMorph(bool deferGlassRelease = false)
    {
        ReleaseMorphStarts();
        var glassSuppression = _morphGlassSuppression;
        _morphGlassSuppression = null;
        if (deferGlassRelease && _attached && glassSuppression != null)
        {
            ReleasePendingGlassSuppression();
            _pendingGlassSuppressionRelease = glassSuppression;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_pendingGlassSuppressionRelease, glassSuppression))
                {
                    _pendingGlassSuppressionRelease = null;
                    glassSuppression.Dispose();
                }
            }, DispatcherPriority.Background);
        }
        else
        {
            ReleasePendingGlassSuppression();
            glassSuppression?.Dispose();
        }
        _morphTicked = false;
        _morphProgress = 1;
        var generation = _morphGeneration;
        if (_morphDeferredClearTexts)
        {
            _morphDeferredClearTexts = false;
            _morphDeferredClearGridNames = false;
            _morphDeferredClearListTexts = false;
            ClearTexts();
        }
        if (_morphDeferredClearGridNames)
        {
            _morphDeferredClearGridNames = false;
            ClearGridNames();
        }
        if (_morphDeferredClearListTexts)
        {
            _morphDeferredClearListTexts = false;
            PruneStaleListTexts(clearAllListNames: false, evictAllListRows: false);
        }
        if (_morphDeferredVisibleUpdate)
        {
            _morphDeferredVisibleUpdate = false;
            ScheduleVisibleUpdate();
        }
        if (!IsGrid)
        {
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        }
        else if (_attached)
        {
            // Keep the landing frame cheap; upgrade grid thumbnails to high-quality sampling on the next idle pass.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_attached || !IsGrid || IsLayoutMorphActive || generation != _morphGeneration) return;
                RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
                InvalidateVisual();
            }, DispatcherPriority.Background);
        }
        InvalidateVisual();
    }

    internal void CancelLayoutMorph()
    {
        _morphGeneration++;
        if (_morphStarts == null && _morphProgress >= 1)
        {
            ReleasePendingGlassSuppression();
            _morphGlassSuppression?.Dispose();
            _morphGlassSuppression = null;
            return;
        }
        FinishLayoutMorph(deferGlassRelease: false);
    }

    private void RenderMorphEntries(DrawingContext context, int first, int end, int hover)
    {
        const int warmTextBudgetPerFrame = 6;
        var t = ViewMotion.EaseOutCubic(_morphProgress);
        var modeSwitched = IsGrid != _morphFromGrid;
        var startNameAlpha = modeSwitched ? Math.Clamp(1 - t / 0.38, 0, 1) : 1;
        var targetNameAlpha = modeSwitched ? Math.Clamp((t - 0.38) / 0.42, 0, 1) : 0;
        var detailAlpha = Math.Clamp((t - 0.38) / 0.42, 0, 1);
        var warmBudget = warmTextBudgetPerFrame;
        var drawListColumns = !IsGrid && DrawFileDetails;

        _morphItems.Clear();
        _morphAppearItems.Clear();
        var hasCutItems = false;
        var hasNameHighlights = false;
        var hasDisclosures = false;
        var hasStartNames = false;
        var hasTargetNames = false;
        var hasListDetailColumns = false;

        for (var index = first; index < end; index++)
        {
            var entry = _rows[index];
            var targetRow = RowBounds(index);
            var targetIcon = IsGrid
                ? GridImageBounds(targetRow.Center.X, targetRow.Y)
                : new Rect(ListInset + TreeIndent(index) + 2, targetRow.Y + 6, 18, 18);
            var targetText = modeSwitched && DetailTextProvider == null
                ? TryGetOrWarmTargetText(entry, index, ref warmBudget)
                : null;

            if (!_morphStarts!.TryGetValue(entry.FullPath, out var start))
            {
                _morphAppearItems.Add(new MorphAppearItem(index, entry, targetRow, targetIcon, targetText));
                continue;
            }

            var lerpedRow = ViewMotion.LerpRect(start.Row, targetRow, t);
            var lerpedIcon = ViewMotion.LerpRect(start.Icon, targetIcon, t);
            var startName = entry.FullPath != EditingPath ? start.CachedName : null;
            var metricText = targetText?.Name ?? startName;
            var maxNameWidth = IsGrid ? GridNameWidth : Math.Max(1, NameColumnWidth - 16 - TreeIndent(index));
            var nameWidth = metricText != null
                ? Math.Ceiling(Math.Min(maxNameWidth, metricText.WidthIncludingTrailingWhitespace))
                : start.Name.Width;
            var nameHeight = metricText != null ? Math.Ceiling(metricText.Height) : start.Name.Height;
            var targetName = IsGrid
                ? new Rect(Math.Round(targetRow.Center.X - nameWidth / 2), targetRow.Y + GridIconSize + 35, nameWidth, nameHeight)
                : new Rect(Math.Round(NameX(index)), targetRow.Y + Math.Round((RowHeight - nameHeight) / 2), nameWidth, nameHeight);
            var lerpedNamePos = new Point(
                Math.Round(start.Name.X + (targetName.X - start.Name.X) * t),
                Math.Round(start.Name.Y + (targetName.Y - start.Name.Y) * t));
            var isCut = !NameOnly && entry.IsCut;
            if (isCut) hasCutItems = true;
            var drawRowOutline = IsRowSelected(entry) || HasKeyboardFocus(entry);
            if (startName != null) hasStartNames = true;
            if (entry.FullPath != EditingPath && targetText != null) hasTargetNames = true;
            if (drawListColumns && targetText != null) hasListDetailColumns = true;

            if (IsGrid)
            {
                var targetIconHighlight = GridIconTargetBounds(index);
                var iconHighlightRect = ViewMotion.LerpRect(start.IconHighlight, targetIconHighlight, t);
                var iconHovered = _pointerPosition is { } pt && targetIconHighlight.Contains(pt);
                var iconFill = entry.FullPath == _dropPath ? DropBrush
                    : entry.IsSelected ? iconHovered ? SelectedHover : Selected : iconHovered ? Hover : null;

                Rect nameHighlightRect = default;
                IBrush? nameFill = null;
                var drawNameHighlight = false;
                if (entry.FullPath != EditingPath)
                {
                    var targetNameHighlight = targetName.Inflate(new Thickness(4, 1));
                    var nameHovered = _pointerPosition is { } namePt && targetNameHighlight.Contains(namePt);
                    nameFill = entry.FullPath == _dropPath ? DropBrush
                        : entry.IsSelected ? nameHovered ? SelectedHover : Selected : nameHovered ? Hover : null;
                    if (nameFill != null || drawRowOutline)
                    {
                        drawNameHighlight = true;
                        hasNameHighlights = true;
                        nameHighlightRect = modeSwitched
                            ? new Rect(lerpedNamePos, targetName.Size).Inflate(new Thickness(4, 1))
                            : ViewMotion.LerpRect(start.NameHighlight, targetNameHighlight, t);
                    }
                }

                _morphItems.Add(new MorphDrawItem(index, entry, lerpedRow, lerpedIcon, iconHighlightRect, nameHighlightRect,
                    lerpedNamePos, startName, targetText, iconFill, nameFill, isCut,
                    drawRowOutline, drawNameHighlight, false));
            }
            else
            {
                var fill = IsRowSelected(entry) ? index == hover ? SelectedHover : Selected
                    : index == hover ? Hover : index % 2 == 1 ? AlternateRow : null;
                var drawDisclosure = IsTree && _treeRows[index].CanExpand && detailAlpha > 0.01;
                if (drawDisclosure) hasDisclosures = true;

                _morphItems.Add(new MorphDrawItem(index, entry, lerpedRow, lerpedIcon, default, default,
                    lerpedNamePos, startName, targetText, fill, null, isCut,
                    drawRowOutline, false, drawDisclosure));
            }
        }

        // Pass 1: Backgrounds & highlights (batch opacity layers instead of per-item SaveLayer).
        if (IsGrid)
        {
            DrawGridIconHighlights(cutOnly: false);
            if (hasCutItems)
            {
                using var cutOpacity = context.PushOpacity(0.45);
                DrawGridIconHighlights(cutOnly: true);
            }
            if (hasNameHighlights)
            {
                var nameBgAlpha = modeSwitched ? detailAlpha : 1;
                if (nameBgAlpha > 0.001)
                {
                    using var nameBgOpacity = nameBgAlpha < 0.999 ? context.PushOpacity(nameBgAlpha) : default;
                    DrawGridNameHighlights(cutOnly: false);
                }
                if (hasCutItems && nameBgAlpha * 0.45 > 0.001)
                {
                    using var cutNameBgOpacity = context.PushOpacity(nameBgAlpha * 0.45);
                    DrawGridNameHighlights(cutOnly: true);
                }
            }
        }
        else
        {
            var radius = Math.Max(0, RowCornerRadius.TopLeft - 0.5);
            for (var i = 0; i < _morphItems.Count; i++)
            {
                var item = _morphItems[i];
                var hasFocus = HasKeyboardFocus(item.Entry);
                if (item.Fill != null || hasFocus)
                    context.DrawRectangle(item.Fill, hasFocus ? new Pen(FocusRing, 1) : null, new RoundedRect(item.Row.Deflate(0.5), radius));
                if (item.DrawRowOutline && !item.IsCut)
                    context.DrawRectangle(null, null, new RoundedRect(item.Row.Deflate(1), RowCornerRadius), OutlineFor(item.Entry));
            }
            if (hasCutItems)
            {
                using var cutOutlineOpacity = context.PushOpacity(0.45);
                for (var i = 0; i < _morphItems.Count; i++)
                {
                    var item = _morphItems[i];
                    if (item.IsCut && item.DrawRowOutline)
                        context.DrawRectangle(null, null, new RoundedRect(item.Row.Deflate(1), RowCornerRadius), OutlineFor(item.Entry));
                }
            }
        }

        // Pass 2: Icons for morphed items (0 SaveLayer for normal items).
        for (var i = 0; i < _morphItems.Count; i++)
        {
            var item = _morphItems[i];
            if (!item.IsCut) DrawIcon(context, item.Entry, item.Icon, drawShadow: false);
        }
        if (hasCutItems)
        {
            using var cutIconOpacity = context.PushOpacity(0.45);
            for (var i = 0; i < _morphItems.Count; i++)
            {
                var item = _morphItems[i];
                if (item.IsCut) DrawIcon(context, item.Entry, item.Icon, drawShadow: false);
            }
        }

        // Pass 3A: Outgoing source labels (fades out cleanly before target text fades in, avoiding dual-layer overlap).
        if (hasStartNames && startNameAlpha > 0.001)
        {
            using (startNameAlpha < 0.999 ? context.PushOpacity(startNameAlpha) : default)
            {
                for (var i = 0; i < _morphItems.Count; i++)
                {
                    var item = _morphItems[i];
                    if (!item.IsCut && item.StartName != null) item.StartName.Draw(context, item.NamePos);
                }
            }
            if (hasCutItems)
            {
                using var cutStartLabelOpacity = context.PushOpacity(startNameAlpha * 0.45);
                for (var i = 0; i < _morphItems.Count; i++)
                {
                    var item = _morphItems[i];
                    if (item.IsCut && item.StartName != null) item.StartName.Draw(context, item.NamePos);
                }
            }
        }

        // Pass 3B: Incoming target labels, tree disclosures, and list detail columns combined in 1 shared opacity layer (0 SaveLayer once settled).
        var drawTargetNames = modeSwitched && hasTargetNames && targetNameAlpha > 0.001;
        var drawTargetDetails = (hasDisclosures || hasListDetailColumns) && detailAlpha > 0.001;
        if (drawTargetNames || drawTargetDetails)
        {
            var incomingAlpha = drawTargetNames ? targetNameAlpha : detailAlpha;
            using (incomingAlpha < 0.999 ? context.PushOpacity(incomingAlpha) : default)
            {
                if (drawTargetDetails) DrawListDetailsAndDisclosures(cutOnly: false);
                if (drawTargetNames) DrawTargetLabels(cutOnly: false);
            }
            if (hasCutItems)
            {
                using var cutIncomingOpacity = context.PushOpacity(incomingAlpha * 0.45);
                if (drawTargetDetails) DrawListDetailsAndDisclosures(cutOnly: true);
                if (drawTargetNames) DrawTargetLabels(cutOnly: true);
            }
        }

        // Pass 5: Newly appearing items without snapshot (1 shared SaveLayer including icon & target text).
        if (_morphAppearItems.Count > 0 && t > 0.001)
        {
            var scale = 0.92 + 0.08 * t;
            var radius = Math.Max(0, RowCornerRadius.TopLeft - 0.5);
            using (t < 0.999 ? context.PushOpacity(t) : default)
            {
                for (var i = 0; i < _morphAppearItems.Count; i++)
                {
                    var item = _morphAppearItems[i];
                    var center = item.Icon.Center;
                    if (!IsGrid)
                    {
                        var fill = IsRowSelected(item.Entry) ? item.Index == hover ? SelectedHover : Selected
                            : item.Index == hover ? Hover : item.Index % 2 == 1 ? AlternateRow : null;
                        if (fill != null)
                            context.DrawRectangle(fill, null, new RoundedRect(item.Row.Deflate(0.5), radius));
                        if (IsTree && _treeRows[item.Index].CanExpand)
                            DrawTreeDisclosure(context, item.Index);
                    }
                    using (context.PushTransform(
                        Matrix.CreateTranslation(-center.X, -center.Y)
                        * Matrix.CreateScale(scale, scale)
                        * Matrix.CreateTranslation(center.X, center.Y)))
                    {
                        DrawIcon(context, item.Entry, item.Icon, drawShadow: false);
                    }
                    if (item.TargetText is { } appearText && item.Entry.FullPath != EditingPath)
                    {
                        if (IsGrid)
                        {
                            var width = Math.Ceiling(Math.Min(GridNameWidth, appearText.Name.WidthIncludingTrailingWhitespace));
                            appearText.Name.Draw(context, new Point(Math.Round(item.Row.Center.X - width / 2), item.Row.Y + GridIconSize + 35));
                        }
                        else
                        {
                            DrawRowCell(context, appearText.Name, NameX(item.Index), item.Row.Y);
                            if (drawListColumns) DrawDetailCells(context, appearText, item.Row.Y);
                        }
                    }
                }
            }
        }

        void DrawTargetLabels(bool cutOnly)
        {
            for (var i = 0; i < _morphItems.Count; i++)
            {
                var item = _morphItems[i];
                if (item.IsCut == cutOnly && item.TargetText != null && item.Entry.FullPath != EditingPath)
                    item.TargetText.Name.Draw(context, item.NamePos);
            }
        }

        void DrawListDetailsAndDisclosures(bool cutOnly)
        {
            for (var i = 0; i < _morphItems.Count; i++)
            {
                var item = _morphItems[i];
                if (item.IsCut != cutOnly) continue;
                if (item.DrawDisclosure) DrawTreeDisclosure(context, item.Index);
                if (drawListColumns && item.TargetText is { } text) DrawDetailCells(context, text, item.Row.Y);
            }
        }

        void DrawGridIconHighlights(bool cutOnly)
        {
            for (var i = 0; i < _morphItems.Count; i++)
            {
                var item = _morphItems[i];
                if (item.IsCut != cutOnly) continue;
                if (item.Fill != null || item.DrawRowOutline)
                    context.DrawRectangle(item.Fill, null, new RoundedRect(item.IconHighlight, RowCornerRadius), OutlineFor(item.Entry));
            }
        }

        void DrawGridNameHighlights(bool cutOnly)
        {
            for (var i = 0; i < _morphItems.Count; i++)
            {
                var item = _morphItems[i];
                if (item.IsCut != cutOnly || !item.DrawNameHighlight) continue;
                context.DrawRectangle(item.NameFill, null, new RoundedRect(item.NameHighlight, new CornerRadius(4)), OutlineFor(item.Entry));
            }
        }
    }

    private RowText? TryGetOrWarmTargetText(FileSystemEntry entry, int index, ref int warmBudget)
    {
        var nameWidth = IsGrid ? GridNameWidth : Math.Max(1, NameColumnWidth - 16 - TreeIndent(index));
        if (_texts.TryGetValue(entry.FullPath, out var node) && node.Value.Matches(entry, nameWidth, IsGrid))
            return node.Value;

        var allCached = IsGrid
            ? _gridNames.ContainsKey(entry.DisplayName)
            : _listNames.ContainsKey((entry.DisplayName, nameWidth)) && AreDetailCellsCached(entry);
        if (allCached)
            return Texts(entry, index);

        if (warmBudget <= 0)
            return null;

        warmBudget--;
        return Texts(entry, index);
    }

    private bool AreDetailCellsCached(FileSystemEntry entry)
    {
        if (!DrawFileDetails) return true;
        foreach (var column in _detailColumns)
        {
            var value = column.Column switch
            {
                FileListColumn.Modified => entry.ModifiedText,
                FileListColumn.Size => entry.FormattedSize,
                FileListColumn.Type => entry.KindText,
                _ => null
            };
            if (value != null && !_cellTexts.ContainsKey((column.Column, value)))
                return false;
        }
        return true;
    }

    private void DrawIcon(DrawingContext context, FileSystemEntry entry, Rect bounds, bool drawShadow = true)
    {
        if (DetailIcon != null)
        {
            using var detailTransform = context.PushTransform(Matrix.CreateScale(bounds.Width / 24, bounds.Height / 24)
                * Matrix.CreateTranslation(bounds.X, bounds.Y));
            context.DrawGeometry(Secondary, null, DetailIcon);
            return;
        }
        var image = _images.Get(entry, IsGrid && _folderCoversEnabled, out var isThumbnail);
        if (image != null)
        {
            var scale = Math.Min(bounds.Width / image.Size.Width, bounds.Height / image.Size.Height);
            var size = new Size(image.Size.Width * scale, image.Size.Height * scale);
            var destination = new Rect(bounds.Center - new Vector(size.Width / 2, size.Height / 2), size);
            if (drawShadow && isThumbnail) ThumbnailAppearance.DrawShadow(context, destination, ThumbnailShadow);
            context.DrawImage(image, destination);
        }
        else
        {
            using var transform = context.PushTransform(Matrix.CreateScale(bounds.Width / 24, bounds.Height / 24)
                * Matrix.CreateTranslation(bounds.X, bounds.Y));
            context.DrawGeometry(Secondary, null, entry.DetailsIconSource.IsDirectory ? _folderFallback : _fileFallback);
        }
        if (NameOnly || !entry.HasGitBadge) return;
        var badgeSize = IsGrid ? 16 : 10;
        var outset = IsGrid ? 4 : 2;
        var badge = new Rect(bounds.Right - badgeSize + outset, bounds.Bottom - badgeSize + outset, badgeSize, badgeSize);
        var stroke = IsGrid ? 1.5 : 1;
        if (!GitBadgeBrushes.TryGetValue(entry.GitBadgeColor, out var badgeBrush))
            GitBadgeBrushes[entry.GitBadgeColor] = badgeBrush = Brush.Parse(entry.GitBadgeColor);
        context.DrawRectangle(badgeBrush, IsGrid ? GitBadgePenGrid : GitBadgePenList,
            badge.Deflate(stroke / 2), (badgeSize - stroke) / 2, (badgeSize - stroke) / 2);
        var badgeKey = (IsGrid, entry.GitBadgeText);
        if (!_badges.TryGetValue(badgeKey, out var glyph))
            _badges[badgeKey] = glyph = Format(entry.GitBadgeText, IsGrid ? 9 : 6, Brushes.White, double.PositiveInfinity, weight: FontWeight.Bold);
        glyph.Draw(context, new Point(Math.Round(badge.Center.X - Math.Ceiling(glyph.Width) / 2), Math.Round(badge.Center.Y - Math.Ceiling(glyph.Height) / 2)));
    }

    private void DrawTreeDisclosure(DrawingContext context, int index)
    {
        var state = _treeRows[index];
        var bounds = TreeDisclosureBounds(index);
        if (state.IsLoading)
        {
            context.DrawEllipse(null, new Pen(Secondary, 1.4), bounds.Center, 4, 4);
            return;
        }
        if (state.HasError)
        {
            var marker = Format("!", DetailFontSize, Secondary, 12, weight: FontWeight.SemiBold);
            marker.Draw(context, new Point(bounds.X + 6, bounds.Y + 3));
            marker.Release();
            return;
        }
        using var transform = context.PushTransform(Matrix.CreateScale(0.5, 0.5)
            * Matrix.CreateTranslation(bounds.X + 2, bounds.Y + 5));
        context.DrawGeometry(Secondary, null, state.IsExpanded ? _chevronDown : _chevronRight);
    }

    private void DrawDetailCells(DrawingContext context, RowText text, double rowY)
    {
        foreach (var column in _detailColumns)
            if (text.Cell(column.Column) is { } cell)
                DrawRowCell(context, cell, column.TextX(cell.Width) - _horizontalOffset, rowY);
    }

    private static void DrawRowCell(DrawingContext context, CachedText value, double x, double rowY)
        => value.Draw(context, new Point(Math.Round(x), rowY + Math.Round((RowHeight - Math.Ceiling(value.Height)) / 2)));

    private RowText Texts(FileSystemEntry entry, int index)
    {
        var nameWidth = IsGrid ? GridNameWidth : Math.Max(1, NameColumnWidth - 16 - TreeIndent(index));
        if (_texts.TryGetValue(entry.FullPath, out var node))
        {
            if (node.Value.Matches(entry, nameWidth, IsGrid))
            {
                _textLru.Remove(node);
                _textLru.AddFirst(node);
                return node.Value;
            }
            _texts.Remove(entry.FullPath);
            _textLru.Remove(node);
            node.Value.Release();
        }
        CachedText? modified = null, size = null, kind = null;
        if (IsGrid)
        {
            if (entry.IsVirtual) size = Format(entry.VirtualCountText, MetaFontSize, Secondary, GridNameWidth + 8);
        }
        else if (DrawFileDetails)
        {
            foreach (var column in _detailColumns)
            {
                switch (column.Column)
                {
                    case FileListColumn.Modified:
                        modified = CellText(column.Column, entry.ModifiedText, column.TextWidth).Retain();
                        break;
                    case FileListColumn.Size:
                        size = CellText(column.Column, entry.FormattedSize, column.TextWidth).Retain();
                        break;
                    case FileListColumn.Type:
                        kind = CellText(column.Column, entry.KindText, column.TextWidth).Retain();
                        break;
                }
            }
        }
        var text = new RowText(entry,
            IsGrid ? GridName(entry).Retain() : ListName(entry.DisplayName, nameWidth).Retain(),
            modified, size, kind, nameWidth, IsGrid);
        _texts[entry.FullPath] = _textLru.AddFirst(text);
        if (_texts.Count > TextCacheLimit && _textLru.Last is { } last)
        {
            _texts.Remove(last.Value.Entry.FullPath);
            _textLru.RemoveLast();
            last.Value.Release();
        }
        return text;
    }

    private CachedText ListName(string name, double width)
    {
        var key = (name, width);
        if (_listNames.TryGetValue(key, out var text)) return text;
        if (_listNames.Count >= TextCacheLimit)
        {
            foreach (var cached in _listNames.Values) cached.Release();
            _listNames.Clear();
        }
        return _listNames[key] = FormatName(name, FontSize, width);
    }

    private CachedText FormatName(string value, double size, double width, int lines = 1)
        => new(FileNameText.CreateLayout(value, name => new TextLayout(name,
            new Typeface(FontFamily, weight: FontWeight), size, Foreground,
            textWrapping: lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            maxWidth: Math.Max(1, width), maxLines: lines)));

    private CachedText Format(string value, double size, IBrush? foreground, double width, int lines = 1, FontWeight? weight = null)
        => new(new TextLayout(value, new Typeface(FontFamily, weight: weight ?? FontWeight), size, foreground,
            textWrapping: lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            textTrimming: lines > 1 ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            maxWidth: Math.Max(1, width), maxLines: lines));
    private CachedText CellText(FileListColumn column, string value, double width)
    {
        var key = (column, value);
        if (_cellTexts.TryGetValue(key, out var text)) return text;
        text = Format(value, DetailFontSize, Secondary, width);
        if (_cellTexts.Count >= TextCacheLimit && _cellTexts.Remove(_cellTextOrder.Dequeue(), out var expired))
            expired.Release();
        _cellTexts.Add(key, text);
        _cellTextOrder.Enqueue(key);
        return text;
    }
    private void ClearHeaderTexts()
    {
        foreach (var (title, count) in _headerTexts.Values) { title.Release(); count.Release(); }
        _headerTexts.Clear();
    }

    private void ClearGridNames()
    {
        foreach (var text in _gridNames.Values) text.Release();
        _gridNames.Clear();
        var node = _textLru.First;
        while (node != null)
        {
            var next = node.Next;
            if (node.Value.IsGrid)
            {
                _texts.Remove(node.Value.Entry.FullPath);
                _textLru.Remove(node);
                node.Value.Release();
            }
            node = next;
        }
    }

    private void ClearCellTexts()
    {
        foreach (var text in _cellTexts.Values) text.Release();
        _cellTexts.Clear();
        _cellTextOrder.Clear();
    }

    private void PruneStaleListTexts(bool clearAllListNames, bool evictAllListRows)
    {
        var currentBaseWidth = Math.Max(1, NameColumnWidth - 16);
        if (clearAllListNames)
        {
            foreach (var text in _listNames.Values) text.Release();
            _listNames.Clear();
        }
        else if (_listNames.Count > 0)
        {
            foreach (var key in _listNames.Keys.ToArray())
            {
                if (Math.Abs(key.Width - currentBaseWidth) < 0.5) continue;
                if (IsTree && key.Width < currentBaseWidth && Math.Abs((currentBaseWidth - key.Width) % TreeStep) < 0.5)
                    continue;
                _listNames[key].Release();
                _listNames.Remove(key);
            }
        }
        var node = _textLru.First;
        while (node != null)
        {
            var next = node.Next;
            if (!node.Value.IsGrid)
            {
                var matchesWidth = !evictAllListRows && (Math.Abs(node.Value.NameWidth - currentBaseWidth) < 0.5
                    || (IsTree && node.Value.NameWidth < currentBaseWidth
                        && Math.Abs((currentBaseWidth - node.Value.NameWidth) % TreeStep) < 0.5));
                var matchesVisibility = !evictAllListRows && (!DrawFileDetails
                    || ((_columns.Modified > 0) == (node.Value.Modified != null)
                        && (_columns.Size > 0) == (node.Value.Size != null)
                        && (_columns.Type > 0) == (node.Value.Kind != null)));
                if (!matchesWidth || !matchesVisibility)
                {
                    _texts.Remove(node.Value.Entry.FullPath);
                    _textLru.Remove(node);
                    node.Value.Release();
                }
            }
            node = next;
        }
    }

    private void ClearListTexts()
    {
        ClearCellTexts();
        PruneStaleListTexts(clearAllListNames: true, evictAllListRows: true);
    }

    private void ClearTexts()
    {
        foreach (var row in _textLru) row.Release();
        foreach (var text in _cellTexts.Values) text.Release();
        foreach (var text in _badges.Values) text.Release();
        foreach (var text in _gridNames.Values) text.Release();
        foreach (var text in _listNames.Values) text.Release();
        ClearHeaderTexts();
        _texts.Clear(); _textLru.Clear(); _cellTexts.Clear(); _cellTextOrder.Clear(); _badges.Clear(); _gridNames.Clear(); _listNames.Clear();
    }

    // FormattedText reshapes lines on each DrawText. TextLayout retains the shaped
    // runs; shared labels stay alive until both the row and value caches release them.
    private sealed class CachedText(TextLayout layout)
    {
        private int _references = 1;
        public double Width => layout.Width;
        public double WidthIncludingTrailingWhitespace => layout.WidthIncludingTrailingWhitespace;
        public double Height => layout.Height;
        public void Draw(DrawingContext context, Point origin) => layout.Draw(context, origin);
        public CachedText Retain() { _references++; return this; }
        public void Release() { if (--_references == 0) layout.Dispose(); }
    }

    private sealed record RowText(FileSystemEntry Entry, CachedText Name, CachedText? Modified, CachedText? Size, CachedText? Kind, double NameWidth, bool IsGrid)
    {
        public CachedText? Cell(FileListColumn column) => column switch
        {
            FileListColumn.Modified => Modified,
            FileListColumn.Size => Size,
            FileListColumn.Type => Kind,
            _ => null
        };

        public void Release() { Name.Release(); Modified?.Release(); Size?.Release(); Kind?.Release(); }
        public bool Matches(FileSystemEntry other, double nameWidth, bool isGrid)
        {
            if (IsGrid != isGrid || NameWidth != nameWidth) return false;
            if (ReferenceEquals(Entry, other)) return true;
            if (Entry.Name != other.Name || Entry.IsApplication != other.IsApplication || Entry.IconKey != other.IconKey)
                return false;

            var sameVirtualStatus = Entry.IsVirtual == other.IsVirtual;
            if (IsGrid)
                return sameVirtualStatus && (!other.IsVirtual || Entry.VirtualItemCount == other.VirtualItemCount);

            var sameSize = Size == null || sameVirtualStatus && (other.IsVirtual
                ? Entry.VirtualItemCount == other.VirtualItemCount
                : Entry.Size == other.Size && Entry.IsDirectory == other.IsDirectory);
            var sameKind = Kind == null || sameVirtualStatus && (other.IsVirtual
                ? Entry.VirtualFolderType == other.VirtualFolderType
                : Entry.Extension == other.Extension && Entry.IsDirectory == other.IsDirectory);
            return sameSize && sameKind && (Modified == null || Entry.LastModified == other.LastModified);
        }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FastListPeer(this);
    private sealed class FastListPeer(FastFileList owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetClassNameCore() => nameof(FastFileList);
    }
}
