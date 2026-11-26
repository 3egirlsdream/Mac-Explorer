using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Preview;

namespace MacExplorer.Controls;

internal sealed class MindMapCanvas : Control
{
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<MindMapCanvas, double>(nameof(FontSize), 14);

    private const double GapX = 52;
    private const double GapY = 12;
    private const double Padding = 12;
    private const double MinZoom = 0.001;
    private const double MaxZoom = 4;
    private const double OverviewThreshold = 0.25;

    private readonly HashSet<MindMapNode> _collapsed = new(ReferenceEqualityComparer.Instance);
    private readonly List<Topic> _topics = [];
    private readonly List<double> _columnWidths = [];
    private readonly Dictionary<MindMapNode, TextLayout> _labels = new(ReferenceEqualityComparer.Instance);
    private MindMapSheet? _sheet;
    private MindMapNode? _selected;
    private MindMapDocument? _document;
    private Rect _mapBounds;
    private double _scale = 1;
    private Vector _offset;
    private IPointer? _dragPointer;
    private Point _dragStart;
    private Vector _dragOffset;
    private bool _dragMoved;
    private bool _fitOnResize;
    private bool _needsInitialView;
    private Topic? _hovered;
    private bool _hoveredDisclosure;

    private double HorizontalScale => _scale < OverviewThreshold
        ? Math.Max(_scale, Math.Min(0.22, Math.Max(1, Bounds.Width - 48) / _mapBounds.Width))
        : _scale;

    public MindMapCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        AutomationProperties.SetName(this, LocalizationText.Get("preview.mind-map.canvas"));
        ActualThemeVariantChanged += (_, _) => RefreshAppearance();
    }

    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty) RefreshAppearance();
        if (change.Property == IsKeyboardFocusWithinProperty) InvalidateVisual();
    }

    internal void SetDocument(MindMapDocument document)
    {
        _document = document;
        foreach (var sheet in document.Sheets)
            foreach (var root in sheet.Roots)
                ReadInitialCollapse(root);

        void ReadInitialCollapse(MindMapNode node)
        {
            if (node.IsCollapsed && node.Children.Count > 0) _collapsed.Add(node);
            foreach (var child in node.Children) ReadInitialCollapse(child);
        }
    }

    internal void SetSheet(int index)
    {
        if (_document == null) return;
        ClearLabels();
        ClearHover();
        _sheet = _document.Sheets[index];
        _selected = null;
        AutomationProperties.SetName(this, LocalizationText.Get("preview.mind-map.canvas"));
        RebuildLayout();
        _needsInitialView = true;
        ApplyInitialView();
    }

    internal void Clear()
    {
        _document = null;
        _sheet = null;
        _selected = null;
        _topics.Clear();
        _columnWidths.Clear();
        _collapsed.Clear();
        ClearLabels();
        ClearHover();
        _dragPointer?.Capture(null);
    }

    private void ClearLabels()
    {
        foreach (var layout in _labels.Values) layout.Dispose();
        _labels.Clear();
    }

    private void RefreshAppearance()
    {
        ClearLabels();
        if (_sheet == null) return;
        RebuildLayout();
        if (_fitOnResize) Fit();
    }

    internal void Fit()
    {
        if (_topics.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _needsInitialView = false;
        _fitOnResize = true;
        _scale = Math.Min(1.05, Math.Min(
            Math.Max(1, Bounds.Width - 48) / _mapBounds.Width,
            Math.Max(1, Bounds.Height - 38) / _mapBounds.Height));
        CenterAt(_mapBounds.Center);
    }

    internal void Zoom(bool increase) => ZoomAt(increase ? 1.25 : 0.8, new Rect(Bounds.Size).Center);

    private void ApplyInitialView()
    {
        if (!_needsInitialView || _topics.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        Fit();
        if (_scale >= 0.65) return;
        // Keep the first topic legible on dense maps. Fit is still available for the full overview.
        _fitOnResize = false;
        _scale = 0.9;
        CenterAt(_topics[0].Bounds.Center, new Point(Bounds.Width * 0.35, Bounds.Height * 0.5));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_needsInitialView) ApplyInitialView();
        else if (_fitOnResize) Fit();
        else if (e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0)
        {
            _offset += new Vector(e.NewSize.Width - e.PreviousSize.Width, e.NewSize.Height - e.PreviousSize.Height) / 2;
            InvalidateVisual();
        }
    }

    private void ZoomAt(double factor, Point anchor)
    {
        var position = ToMap(anchor);
        _fitOnResize = false;
        var next = _scale < 0.15 && factor > 1 ? Math.Max(0.55, _scale * factor) : _scale * factor;
        _scale = Math.Clamp(next, Math.Min(_scale, MinZoom), MaxZoom);
        CenterAt(position, anchor);
    }

    private void CenterAt(Point position, Point? anchor = null)
    {
        _offset = (anchor ?? new Rect(Bounds.Size).Center) - new Point(position.X * HorizontalScale, position.Y * _scale);
        InvalidateVisual();
    }

    private Point ToMap(Point point)
        => new((point.X - _offset.X) / HorizontalScale, (point.Y - _offset.Y) / _scale);

    private Point ToScreen(Point point)
        => new(point.X * HorizontalScale + _offset.X, point.Y * _scale + _offset.Y);

    private void RebuildLayout()
    {
        _topics.Clear();
        _columnWidths.Clear();
        if (_sheet == null) return;

        var top = 0.0;
        foreach (var root in _sheet.Roots)
            top += Place(root, null, 0, top) + GapY * 2;

        var columns = new double[_columnWidths.Count];
        for (var depth = 1; depth < columns.Length; depth++)
            columns[depth] = columns[depth - 1] + _columnWidths[depth - 1] + GapX;

        var width = 0.0;
        var height = 0.0;
        foreach (var topic in _topics)
        {
            topic.Bounds = topic.Bounds.WithX(columns[topic.Depth]);
            width = Math.Max(width, topic.Bounds.Right + (topic.HasChildren ? 24 : 0));
            height = Math.Max(height, topic.Bounds.Bottom);
        }
        _mapBounds = new Rect(0, 0, width, height);
        InvalidateVisual();

        double Place(MindMapNode node, Topic? parent, int depth, double start)
        {
            var fontSize = FontSize + (depth == 0 ? 2 : 0);
            var width = MeasureTopicWidth(Title(node), fontSize, out var lines);
            var height = Math.Ceiling(fontSize * 1.35) * lines + 14;
            if (_columnWidths.Count <= depth) _columnWidths.Add(width);
            else _columnWidths[depth] = Math.Max(_columnWidths[depth], width);

            var topic = new Topic(node, parent, depth, lines);
            _topics.Add(topic);
            var next = start;
            if (!_collapsed.Contains(node))
            {
                foreach (var child in node.Children)
                    next += Place(child, topic, depth + 1, next) + GapY;
            }
            var extent = Math.Max(height, next - start - GapY);
            topic.Bounds = new Rect(0, start + (extent - height) / 2, width, height);
            return extent;
        }
    }

    // Geometry is cheap to build for the whole tree. Text shaping is reserved for visible topics.
    private static double MeasureTopicWidth(string title, double fontSize, out int lines)
    {
        var width = 0.0;
        var lineWidth = 0.0;
        var lineCount = 1;
        foreach (var character in title)
        {
            if (character == '\r') continue;
            if (character == '\n')
            {
                width = Math.Max(width, lineWidth);
                lineWidth = 0;
                lineCount++;
                continue;
            }
            lineWidth += fontSize * (character < 128 ? 0.64 : 1);
        }
        width = Math.Max(width, lineWidth);
        lines = Math.Min(3, lineCount + (width > 230 ? 1 : 0) + (width > 460 ? 1 : 0));
        return Math.Clamp(width + Padding * 2, 88, 254);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_topics.Count == 0) return;

        var surface = Brush("SurfaceBrush", Brushes.White);
        var selected = Brush("InteractionSelectedBrush", Brushes.LightBlue);
        var border = new Pen(Brush("ColorBorderSubtle", Brushes.LightGray));
        var accentBrush = Brush("AccentBrush", Brushes.DodgerBlue);
        var connectorBrush = Brush("TextMutedBrush", Brushes.Gray);
        var cornerRadius = this.TryFindResource("RadiusMd", out var radius) && radius is CornerRadius corners
            ? corners.TopLeft : 6;
        var viewport = new Rect(ToMap(default), ToMap(new Point(Bounds.Width, Bounds.Height))).Inflate(24);
        if (_scale < OverviewThreshold)
        {
            DrawOverview(context, viewport, accentBrush, connectorBrush);
            return;
        }

        var accent = new Pen(accentBrush, 1.5);
        var connector = new Pen(connectorBrush, 1);
        using var transform = context.PushTransform(Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_offset));
        using (context.PushOpacity(0.48))
        {
            foreach (var topic in _topics)
            {
                if (topic.Parent is not { } parent) continue;
                var start = new Point(parent.Bounds.Right, parent.Bounds.Center.Y);
                var end = new Point(topic.Bounds.Left, topic.Bounds.Center.Y);
                if (!new Rect(start, end).Inflate(1).Intersects(viewport)) continue;
                var joinX = end.X - GapX / 2;
                var middleTop = new Point(joinX, start.Y);
                var middleBottom = new Point(joinX, end.Y);
                var pen = parent.Depth == 0 ? accent : connector;
                context.DrawLine(pen, start, middleTop);
                context.DrawLine(pen, middleTop, middleBottom);
                context.DrawLine(pen, middleBottom, end);
            }
        }

        foreach (var topic in _topics)
        {
            if (!topic.Bounds.Intersects(viewport)) continue;
            var isSelected = ReferenceEquals(topic.Node, _selected) && IsKeyboardFocusWithin;
            var isRoot = topic.Depth == 0;
            context.DrawRectangle(isRoot ? selected : surface, isSelected ? accent : border,
                new RoundedRect(topic.Bounds, cornerRadius));
            if (_scale >= 0.32)
            {
                var label = GetLabel(topic);
                label.Draw(context, new Point(topic.Bounds.X + Padding, topic.Bounds.Center.Y - label.Height / 2));
            }

            if (!topic.HasChildren) continue;
            var toggle = topic.Disclosure.Center;
            var collapsed = _collapsed.Contains(topic.Node);
            context.DrawEllipse(collapsed || ReferenceEquals(topic, _hovered) ? selected : surface,
                collapsed ? accent : border, toggle, 8, 8);
            context.DrawLine(connector, toggle - new Vector(3, 0), toggle + new Vector(3, 0));
            if (collapsed) context.DrawLine(connector, toggle - new Vector(0, 3), toggle + new Vector(0, 3));
        }
    }

    private void DrawOverview(DrawingContext context, Rect viewport, IBrush accent, IBrush secondary)
    {
        var line = new Pen(secondary, 0.7);
        var scale = HorizontalScale;
        using var opacity = context.PushOpacity(0.55);
        foreach (var topic in _topics)
        {
            // At fit scale a large tree can have sub-pixel rows. Keep each
            // topic visible as a mark, grouped into its corresponding level.
            if (!topic.Bounds.Intersects(viewport)) continue;
            var start = ToScreen(new Point(topic.Bounds.Left, topic.Bounds.Center.Y));
            if (topic.Parent is { } parent && topic.HasChildren)
            {
                var from = ToScreen(new Point(parent.Bounds.Right, parent.Bounds.Center.Y));
                var joinX = start.X - GapX / 2 * scale;
                context.DrawLine(line, from, new Point(joinX, from.Y));
                context.DrawLine(line, new Point(joinX, from.Y), new Point(joinX, start.Y));
                context.DrawLine(line, new Point(joinX, start.Y), start);
            }
            context.FillRectangle(topic.Depth == 0 ? accent : secondary,
                new Rect(start.X, start.Y, Math.Max(1, topic.Bounds.Width * scale), Math.Max(1, topic.Bounds.Height * _scale)));
        }
    }

    private TextLayout GetLabel(Topic topic)
    {
        if (_labels.TryGetValue(topic.Node, out var label)) return label;
        if (_labels.Count >= 384) ClearLabels();
        var font = this.TryFindResource("FontFamilyUi", out var family) && family is FontFamily value ? value : FontFamily.Default;
        var fontSize = FontSize + (topic.Depth == 0 ? 2 : 0);
        label = new TextLayout(Title(topic.Node), new Typeface(font, weight: topic.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal),
            fontSize, Brush("TextPrimaryBrush", Brushes.Black), textWrapping: TextWrapping.Wrap,
            textTrimming: TextTrimming.CharacterEllipsis, maxWidth: topic.Bounds.Width - Padding * 2, maxLines: topic.Lines);
        _labels.Add(topic.Node, label);
        return label;
    }

    private IBrush Brush(string key, IBrush fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : fallback;

    private static string Title(MindMapNode node) => node.Title.Length > 0 ? node.Title : LocalizationText.Get("preview.mind-map.untitled");

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_topics.Count == 0 || e.Delta.Y == 0) return;
        ZoomAt(Math.Pow(1.25, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_topics.Count == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _dragStart = e.GetPosition(this);
        _dragOffset = _offset;
        _dragMoved = false;
        _dragPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        if (ReferenceEquals(e.Pointer, _dragPointer))
        {
            var delta = new Vector(position.X - _dragStart.X, position.Y - _dragStart.Y);
            if (!_dragMoved && delta.Length < 4) return;
            _dragMoved = true;
            _fitOnResize = false;
            _offset = _dragOffset + delta;
            ClearHover();
            InvalidateVisual();
            return;
        }

        var topic = HitTestTopic(position);
        var disclosure = topic?.HasChildren == true && topic.Disclosure.Contains(ToMap(position));
        if (ReferenceEquals(topic, _hovered) && disclosure == _hoveredDisclosure) return;
        _hovered = topic;
        _hoveredDisclosure = disclosure;
        ToolTip.SetTip(this, topic == null ? null : disclosure
            ? LocalizationText.Get(_collapsed.Contains(topic.Node) ? "preview.action.expand" : "preview.action.collapse")
            : Title(topic.Node));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _dragPointer)) return;
        var position = e.GetPosition(this);
        if (!_dragMoved && HitTestTopic(position) is { } topic)
        {
            Select(topic);
            if (topic.HasChildren && topic.Disclosure.Contains(ToMap(position))) Toggle(topic);
        }
        else if (!_dragMoved)
        {
            _selected = null;
            AutomationProperties.SetName(this, LocalizationText.Get("preview.mind-map.canvas"));
            InvalidateVisual();
        }
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => _dragPointer = null;
    protected override void OnPointerExited(PointerEventArgs e) => ClearHover();

    private void ClearHover()
    {
        if (_hovered == null) return;
        _hovered = null;
        _hoveredDisclosure = false;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    private Topic? HitTestTopic(Point position)
    {
        var point = ToMap(position);
        return _topics.Find(topic => topic.Bounds.Contains(point)
            || (_scale >= OverviewThreshold && topic.HasChildren && topic.Disclosure.Contains(point)));
    }

    private void Select(Topic topic)
    {
        _selected = topic.Node;
        AutomationProperties.SetName(this, Title(topic.Node));
        InvalidateVisual();
    }

    private void Toggle(Topic topic)
    {
        if (!_collapsed.Remove(topic.Node)) _collapsed.Add(topic.Node);
        var anchor = ToScreen(topic.Bounds.Center);
        _fitOnResize = false;
        RebuildLayout();
        ClearHover();
        if (_topics.Find(current => ReferenceEquals(current.Node, topic.Node)) is { } moved)
            CenterAt(moved.Bounds.Center, anchor);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_topics.Count == 0 || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        var index = _topics.FindIndex(topic => ReferenceEquals(topic.Node, _selected));
        switch (e.Key)
        {
            case Key.Up or Key.Down:
                var next = _topics[index < 0 ? 0 : Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, _topics.Count - 1)];
                Select(next);
                var center = next.Bounds.Center;
                if (_scale < 0.65)
                {
                    _scale = 0.9;
                    _fitOnResize = false;
                    CenterAt(center);
                }
                else if (!new Rect(Bounds.Size).Deflate(30).Contains(ToScreen(center)))
                {
                    _fitOnResize = false;
                    CenterAt(center);
                }
                break;
            case Key.Enter when index >= 0 && _topics[index].HasChildren:
                Toggle(_topics[index]);
                break;
            case Key.Add or Key.OemPlus:
                Zoom(true);
                break;
            case Key.Subtract or Key.OemMinus:
                Zoom(false);
                break;
            case Key.D0 or Key.NumPad0:
                Fit();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private sealed class Topic(MindMapNode node, Topic? parent, int depth, int lines)
    {
        internal MindMapNode Node { get; } = node;
        internal Topic? Parent { get; } = parent;
        internal int Depth { get; } = depth;
        internal int Lines { get; } = lines;
        internal Rect Bounds { get; set; }
        internal bool HasChildren => Node.Children.Count > 0;
        internal Rect Disclosure => new(Bounds.Right + 1, Bounds.Center.Y - 11, 22, 22);
    }
}
