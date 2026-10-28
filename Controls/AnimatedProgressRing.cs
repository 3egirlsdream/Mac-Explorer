using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.VisualTree;
using SkiaSharp;

namespace MacExplorer.Controls;

/// <summary>A decorative progress indicator. Its containing button owns input and accessibility.</summary>
public sealed class AnimatedProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<AnimatedProgressRing, double>(nameof(Value), coerce: (_, value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0);
    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<AnimatedProgressRing, bool>(nameof(IsIndeterminate));
    public static readonly StyledProperty<bool> IsAnimatingProperty =
        AvaloniaProperty.Register<AnimatedProgressRing, bool>(nameof(IsAnimating));
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<AnimatedProgressRing, double>(nameof(StrokeThickness), 2,
            validate: value => double.IsFinite(value) && value > 0);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<AnimatedProgressRing, IBrush?>(nameof(TrackBrush), Brushes.LightGray);
    public static readonly StyledProperty<IBrush?> StartBrushProperty =
        AvaloniaProperty.Register<AnimatedProgressRing, IBrush?>(nameof(StartBrush), Brushes.DodgerBlue);
    public static readonly StyledProperty<IBrush?> EndBrushProperty =
        AvaloniaProperty.Register<AnimatedProgressRing, IBrush?>(nameof(EndBrush), Brushes.DarkTurquoise);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public new bool IsAnimating { get => GetValue(IsAnimatingProperty); set => SetValue(IsAnimatingProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? StartBrush { get => GetValue(StartBrushProperty); set => SetValue(StartBrushProperty, value); }
    public IBrush? EndBrush { get => GetValue(EndBrushProperty); set => SetValue(EndBrushProperty, value); }

    private CompositionCustomVisual? _visual;
    private Window? _window;
    private readonly List<Visual> _visibilityAncestors = [];
    private readonly RenderDiagnostics _diagnostics = new();
    private bool _closed;
    internal int AnimationFrameCount => Volatile.Read(ref _diagnostics.Frames);
    internal bool RenderResourcesReleased => Volatile.Read(ref _diagnostics.Released);
    internal bool IsAnimationActive => _visual != null && IsAnimating && CanRender;
    private bool CanRender => !_closed && IsEffectivelyVisible && Bounds.Width > 0 && Bounds.Height > 0
        && _visibilityAncestors.All(ancestor => ancestor.IsVisible)
        && (_window == null || (_window.IsVisible && _window.WindowState != WindowState.Minimized));

    public AnimatedProgressRing()
    {
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(20, 20);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _closed = false;
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
        }
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window != null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
            _window.Closed += OnWindowClosed;
        }
        if (ElementComposition.GetElementVisual(this) is { } parent)
        {
            _visual = parent.Compositor.CreateCustomVisual(new RingVisualHandler(_diagnostics));
            ElementComposition.SetElementChildVisual(this, _visual);
            SendSnapshot();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseVisual();
        DetachObservers();
        base.OnDetachedFromVisualTree(e);
    }

    private void DetachObservers()
    {
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _visibilityAncestors.Clear();
        if (_window != null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window.Closed -= OnWindowClosed;
            _window = null;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        ReleaseVisual();
        DetachObservers();
    }

    private void ReleaseVisual()
    {
        _visual?.SendHandlerMessage(RingVisualHandler.Stop);
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty) SendSnapshot();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) SendSnapshot();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == IsIndeterminateProperty
            || change.Property == IsAnimatingProperty || change.Property == StrokeThicknessProperty
            || change.Property == TrackBrushProperty || change.Property == StartBrushProperty
            || change.Property == EndBrushProperty || change.Property == BoundsProperty || change.Property == IsVisibleProperty)
            SendSnapshot();
    }

    private void SendSnapshot()
    {
        if (_visual == null) return;
        _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        _visual.SendHandlerMessage(new RingSnapshot(Value, IsIndeterminate, IsAnimating, CanRender,
            StrokeThickness, BrushColor(TrackBrush), BrushColor(StartBrush), BrushColor(EndBrush)));
    }

    private static SKColor BrushColor(IBrush? brush)
    {
        if (brush is not ISolidColorBrush solid) return SKColors.Transparent;
        var color = solid.Color;
        return new(color.R, color.G, color.B, (byte)Math.Round(color.A * solid.Opacity));
    }

    private sealed record RingSnapshot(double Value, bool Indeterminate, bool Animate, bool Visible,
        double Thickness, SKColor Track, SKColor Start, SKColor End);

    private sealed class RenderDiagnostics
    {
        public int Frames;
        public bool Released = true;
    }

    private sealed class RingVisualHandler(RenderDiagnostics diagnostics) : CompositionCustomVisualHandler
    {
        internal static readonly object Stop = new();
        private RingSnapshot? _state;
        private SKPaint? _track;
        private SKPaint? _arc;
        private SKShader? _gradient;
        private (float X, float Y, SKColor Start, SKColor End)? _gradientKey;
        private double _from, _target, _transitionStart, _phase, _lastFrame;
        private bool _framePending, _disposed;

        private double CurrentValue(double now)
        {
            var t = Math.Clamp((now - _transitionStart) / .12, 0, 1);
            return _from + (_target - _from) * (1 - Math.Pow(1 - t, 3));
        }

        public override void OnMessage(object message)
        {
            if (ReferenceEquals(message, Stop))
            {
                _disposed = true;
                _state = null;
                _arc?.Dispose(); _arc = null;
                _track?.Dispose(); _track = null;
                _gradient?.Dispose(); _gradient = null;
                Volatile.Write(ref diagnostics.Released, true);
                return;
            }
            if (_disposed || message is not RingSnapshot next) return;
            Volatile.Write(ref diagnostics.Released, false);
            var now = CompositionNow.TotalSeconds;
            if (_state == null || !next.Visible || (!next.Animate && next.Value != 100)
                || _state.Indeterminate != next.Indeterminate)
                _from = _target = next.Value;
            else if (next.Value != _target)
            {
                _from = CurrentValue(now);
                _target = next.Value;
                _transitionStart = now;
            }
            if (_state?.Animate != next.Animate || _state?.Visible != next.Visible) _lastFrame = now;
            _state = next;
            Invalidate();
            RequestFrame();
        }

        private void RequestFrame()
        {
            if (_framePending || !NeedsFrame()) return;
            _framePending = true;
            RegisterForNextAnimationFrameUpdate();
        }

        private bool NeedsFrame() => !_disposed && _state is { Visible: true } state
            && (state.Animate || (_from != _target && CompositionNow.TotalSeconds < _transitionStart + .12));

        public override void OnAnimationFrameUpdate()
        {
            _framePending = false;
            if (!NeedsFrame())
            {
                if (!_disposed && _state is { Visible: true }) Invalidate();
                return;
            }
            Interlocked.Increment(ref diagnostics.Frames);
            var now = CompositionNow.TotalSeconds;
            if (_state!.Animate) _phase = (_phase + Math.Max(0, now - _lastFrame) * 150) % 360;
            _lastFrame = now;
            Invalidate();
            RequestFrame();
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_disposed || _state is not { Visible: true } state) return;
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var cx = (float)EffectiveSize.X / 2; var cy = (float)EffectiveSize.Y / 2;
            var thickness = (float)Math.Min(state.Thickness, Math.Min(cx, cy));
            var radius = Math.Min(cx, cy) - thickness / 2 - .5f;
            if (radius <= 0) return;
            _track ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
            _arc ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
            _track.Color = state.Track; _track.StrokeWidth = thickness;
            _arc.StrokeWidth = thickness;
            canvas.DrawCircle(cx, cy, radius, _track);
            var sweep = state.Indeterminate ? 90f : (float)(CurrentValue(CompositionNow.TotalSeconds) * 3.6);
            if (sweep <= 0) return;
            var key = (cx, cy, state.Start, state.End);
            if (_gradientKey != key)
            {
                _arc.Shader = null;
                _gradient?.Dispose();
                _gradient = SKShader.CreateSweepGradient(new SKPoint(cx, cy), [state.Start, state.End, state.Start], [0, .5f, 1]);
                _arc.Shader = _gradient;
                _gradientKey = key;
            }
            canvas.Save();
            try
            {
                // Rotate the gradient's coordinate space, compensating the determinate arc's start.
                canvas.RotateDegrees((float)_phase, cx, cy);
                if (sweep >= 360) canvas.DrawCircle(cx, cy, radius, _arc);
                else canvas.DrawArc(new SKRect(cx - radius, cy - radius, cx + radius, cy + radius),
                    -90 - (state.Indeterminate ? 0 : (float)_phase), sweep, false, _arc);
            }
            finally { canvas.Restore(); }
        }
    }
}
