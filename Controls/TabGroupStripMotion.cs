using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Platforms.MacOS;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

internal sealed class TabGroupStripMotion(
    ListBox strip, Grid content, Button newTabButton, ChromiumTabStripSurface surface, Action<TabGroupViewModel> toggle)
    : IDisposable
{
    private CompositionCustomVisual? _visual;
    private TabGroupViewModel? _group;
    private int _revision;
    private bool _updating;
    internal bool IsRunning => _visual != null || _updating;
#if DEBUG
    private int _renderedFrames;
    internal int RenderedFrames => Volatile.Read(ref _renderedFrames);
#endif

    internal void Toggle(TabGroupViewModel group)
    {
        if (_group == group && _visual is { } current)
        {
            ApplyToggle(group);
            current.SendHandlerMessage(new Target(group.IsCollapsed, ++_revision));
            return;
        }

        Finish();
        if (MacWindowChrome.PrefersReducedMotion || ElementComposition.GetElementVisual(surface) is not { } parent ||
            content.GetVisualParent() is not Control area || area.TranslatePoint(default, surface) is not { } origin ||
            area.Bounds.Width <= 0 || surface.Bounds.Height <= 0)
        {
            toggle(group);
            return;
        }

        var scale = TopLevel.GetTopLevel(surface)?.RenderScaling ?? 1;
        var padding = ChromiumTabStripSurface.BottomRadius;
        var bounds = new Rect(Math.Floor((origin.X - padding) * scale) / scale, 0,
            area.Bounds.Width + padding * 2, surface.Bounds.Height);
        var first = Capture(group, bounds, scale);
        ApplyToggle(group);
        var second = Capture(group, bounds, scale);
        if (first == null || second == null)
        {
            first?.Bitmap.Dispose();
            second?.Bitmap.Dispose();
            return;
        }

        var collapsed = group.IsCollapsed ? second : first;
        var expanded = group.IsCollapsed ? first : second;
        var slots = expanded.Slots.Join(collapsed.Slots, slot => slot.Item, slot => slot.Item, (open, closed) =>
            new MovingSlot(open, closed, open.Item == group || closed.Right > collapsed.Tail && open.Right <= expanded.Tail))
            .ToArray();
        _group = group;
        _visual = parent.Compositor.CreateCustomVisual(new SlideVisual(expanded, collapsed, slots, scale, bounds.Height,
            revision => Dispatcher.UIThread.Post(() => { if (_revision == revision) Finish(); })
#if DEBUG
            , () => Interlocked.Increment(ref _renderedFrames)
#endif
        ));
        _visual.Offset = new Avalonia.Vector3D(bounds.X, 0, 0);
        _visual.Size = new Vector(bounds.Width, bounds.Height);
        ElementComposition.SetElementChildVisual(surface, _visual);
        content.SetCurrentValue(Visual.OpacityProperty, 0d);
        newTabButton.SetCurrentValue(InputElement.IsHitTestVisibleProperty, false);
        surface.HeadersSuppressed = true;
        _visual.SendHandlerMessage(new Target(group.IsCollapsed, ++_revision));
    }

    private void ApplyToggle(TabGroupViewModel group)
    {
        _updating = true;
        try
        {
            toggle(group);
            strip.UpdateLayout();
        }
        finally { _updating = false; }
    }

    private Snapshot? Capture(TabGroupViewModel group, Rect bounds, double scale)
    {
        if (strip.ItemsPanelRoot is not AdaptiveTabPanel panel ||
            strip.ContainerFromItem(group) is not { } label ||
            panel.TranslatePoint(default, surface) is not { } panelOrigin ||
            content.TranslatePoint(default, surface) is not { } contentOrigin ||
            strip.FindAncestorOfType<ScrollViewer>() is not { } scroller ||
            scroller.TranslatePoint(new Point(scroller.Bounds.Width, 0), surface) is not { } right)
            return null;

        var offset = panelOrigin.X - bounds.X;
        var viewportLeft = contentOrigin.X - bounds.X;
        var tail = Math.Clamp(right.X - bounds.X, 0, bounds.Width);
        var prefix = label.Bounds.Right + offset;
        var memberEnd = prefix;
        var slots = new List<Slot>();
        foreach (var child in panel.Children)
        {
            if (child.DataContext is ExplorerTabViewModel tab && tab.Group == group)
                memberEnd = child.Bounds.Right + offset;
            else if (child.DataContext is { } item)
                slots.Add(new Slot(item, child.Bounds.Left + offset, child.Bounds.Right + offset));
        }
        if (!group.IsCollapsed && (memberEnd <= viewportLeft || prefix >= tail || prefix == memberEnd))
            return null;

        var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        using (var drawing = bitmap.CreateDrawingContext())
        {
            using var clip = drawing.PushClip(new Rect(0, 0, bounds.Width,
                ChromiumTabStripSurface.GetJoin(bounds.Height, scale)));
            using (drawing.PushTransform(Matrix.CreateTranslation(-bounds.X, 0)))
                surface.Render(drawing);
            drawing.FillRectangle(new VisualBrush { Visual = content, Stretch = Stretch.None },
                new Rect(contentOrigin.X - bounds.X, contentOrigin.Y, content.Bounds.Width, content.Bounds.Height));
        }
        return new Snapshot(bitmap, slots, viewportLeft, prefix, memberEnd, tail);
    }

    internal void OnStripChanged()
    {
        if (!_updating) Finish();
    }

    internal void Finish()
    {
        if (_visual == null) return;
        _revision++;
        _visual.SendHandlerMessage(SlideVisual.Stop);
        ElementComposition.SetElementChildVisual(surface, null);
        _visual = null;
        _group = null;
        content.ClearValue(Visual.OpacityProperty);
        newTabButton.ClearValue(InputElement.IsHitTestVisibleProperty);
        surface.HeadersSuppressed = false;
    }

    public void Dispose() => Finish();

    private sealed record Slot(object Item, double Left, double Right);
    private sealed record MovingSlot(Slot Expanded, Slot Collapsed, bool UseExpanded);
    private sealed record Snapshot(RenderTargetBitmap Bitmap, List<Slot> Slots,
        double ViewportLeft, double Prefix, double MemberEnd, double Tail);
    private sealed record Target(bool Collapse, int Revision);

    // Move the already rendered header, member and trailing sections on the compositor clock.
    private sealed class SlideVisual(Snapshot expanded, Snapshot collapsed, MovingSlot[] slots, double scale, double height,
        Action<int> complete
#if DEBUG
        , Action frameRendered
#endif
        ) : CompositionCustomVisualHandler
    {
        internal static readonly object Stop = new();
        private double _from, _to;
        private TimeSpan _start, _duration;
        private int _revision;
        private bool _framePending, _stopped;

        private double Progress => _duration == TimeSpan.Zero ? _to
            : Math.Clamp((CompositionNow - _start) / _duration, 0, 1);
        private double Expansion
        {
            get
            {
                var t = 1 - Progress;
                return _to + (_from - _to) * t * t;
            }
        }

        public override void OnMessage(object message)
        {
            if (message == Stop)
            {
                _stopped = true;
                expanded.Bitmap.Dispose();
                collapsed.Bitmap.Dispose();
                return;
            }
            if (message is not Target target || _stopped) return;

            _from = _revision == 0 ? (target.Collapse ? 1 : 0) : Expansion;
            _to = target.Collapse ? 0 : 1;
            _start = CompositionNow;
            _duration = TimeSpan.FromMilliseconds((target.Collapse ? 200 : 230) * Math.Max(.25, Math.Abs(_to - _from)));
            _revision = target.Revision;
            Invalidate();
            RequestFrame();
        }

        private void RequestFrame()
        {
            if (_framePending) return;
            _framePending = true;
            RegisterForNextAnimationFrameUpdate();
        }

        public override void OnAnimationFrameUpdate()
        {
            _framePending = false;
            if (_stopped) return;
            Invalidate();
            if (Progress < 1) RequestFrame();
            else complete(_revision);
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_stopped || _revision == 0) return;
#if DEBUG
            frameRendered();
#endif
            var fraction = Expansion;
            var width = EffectiveSize.X;
            if (fraction is 0 or 1)
            {
                Draw(context, fraction == 1 ? expanded : collapsed, 0, width, 0, width);
                return;
            }

            var prefix = Interpolate(collapsed.Prefix, expanded.Prefix, fraction);
            var memberEnd = Interpolate(collapsed.Prefix, expanded.MemberEnd, fraction);
            var tail = Interpolate(collapsed.Tail, expanded.Tail, fraction);
            var viewportLeft = collapsed.ViewportLeft;

            using (context.PushClip(new Rect(0, 0, tail, height)))
            {
                // The members move together and are clipped at the group label.
                var sourceRight = Math.Min(expanded.MemberEnd, expanded.Tail);
                var offset = Math.Min(memberEnd, tail) - sourceRight;
                DrawPart(expanded, Math.Max(expanded.Prefix, viewportLeft), sourceRight, prefix, offset);

                foreach (var slot in slots)
                {
                    var source = slot.UseExpanded ? expanded : collapsed;
                    var original = slot.UseExpanded ? slot.Expanded : slot.Collapsed;
                    var left = Interpolate(slot.Collapsed.Left, slot.Expanded.Left, fraction);
                    var right = Interpolate(slot.Collapsed.Right, slot.Expanded.Right, fraction);

                    if (original.Item is TabGroupViewModel)
                    {
                        DrawPart(source, original.Left, original.Right, viewportLeft, left - original.Left);
                        continue;
                    }

                    // Keep the icon/title and close-button edges aligned as the slot narrows.
                    // The middle clips under the text, so glyphs never stretch or double up.
                    const double rightInset = 32;
                    var leftWidth = Math.Min(right - left, original.Right - original.Left) - rightInset;
                    DrawPart(source, original.Left, original.Left + leftWidth, viewportLeft, left - original.Left);
                    DrawPart(source, original.Right - rightInset, original.Right, viewportLeft, right - original.Right);
                }

                // Preserve the active outline's leading foot at the start of the strip.
                Draw(context, expanded, 0, viewportLeft, 0, viewportLeft);
            }

            // The add button follows the strip and retains its normal width.
            var buttonWidth = Math.Min(width - collapsed.Tail, width - tail);
            Draw(context, collapsed, collapsed.Tail, buttonWidth, tail, buttonWidth);

            void DrawPart(Snapshot source, double left, double right, double minimum, double offset)
            {
                var clippedLeft = Math.Max(left, Math.Max(source.ViewportLeft, minimum - offset));
                var clippedRight = Math.Min(right, source.Tail);
                var length = clippedRight - clippedLeft;
                Draw(context, source, clippedLeft, length, clippedLeft + offset, length);
            }
        }

        private static double Interpolate(double from, double to, double fraction) => from + (to - from) * fraction;

        private void Draw(ImmediateDrawingContext context, Snapshot snapshot, double x, double width, double left, double length)
        {
            if (width <= 0 || length <= 0) return;
            context.DrawBitmap(snapshot.Bitmap, new Rect(x * scale, 0, width * scale, height * scale),
                new Rect(left, 0, length, height));
        }
    }
}
