using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiquidGlassAvaloniaUI;

namespace MacExplorer.Controls;

/// <summary>A glass folder grows from its source into a centered Launchpad-style sheet.</summary>
internal sealed class HomeFolderTransition : Grid, IDisposable
{
    private readonly Control _source;
    private readonly Control _content;
    private readonly LiquidGlassSurface _glass;
    private readonly Image _preview;
    private readonly Border _glassHost;
    private readonly MatrixTransform _glassTransform = new();
    private readonly MatrixTransform _previewTransform = new();
    private readonly Border _scrim;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RenderTargetBitmap? _snapshot;
    private Rect _origin;
    private double _progress;
    private double _start;
    private bool _opened;
    private bool _closing;
    private bool _disposed;
    private readonly double _sourceOpacity;
    public Task Completion => _completion.Task;
    internal double Progress => _progress;

    public HomeFolderTransition(Control source, Control content)
    {
        _source = source; _content = content; _sourceOpacity = source.Opacity;
        Focusable = true;
        Opacity = 0;
        Background = Brushes.Transparent;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        _scrim = new Border { Opacity = 0 };
        _scrim.Bind(Border.BackgroundProperty, this.GetResourceObservable("HomeLaunchpadScrimBrush"));
        _scrim.PointerPressed += (_, e) => { Close(); e.Handled = true; };
        _glass = new LiquidGlassSurface { Classes = { "home-glass", "home-expanded-glass" }, RenderTransformOrigin = RelativePoint.TopLeft,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        _glassHost = new Border { Padding = new Thickness(16), Child = _glass,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsHitTestVisible = false,
            RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = _glassTransform };
        _glass.PointerPressed += (_, e) => e.Handled = true;
        _preview = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = _previewTransform };
        _content.Margin = default;
        _content.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        _content.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _content.Opacity = 0;
        // Sample the opaque scrim, but never the folder or its animation frames.
        foreach (var visual in new Control[] { _glassHost, _preview, content })
            LiquidGlassBackdrop.SetIsExcludedFromCapture(visual, true);
        Children.Add(_scrim); Children.Add(_glassHost); Children.Add(_preview); Children.Add(content);
        _timer.Tick += OnTick;
        SizeChanged += (_, _) => ApplyProgress();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    public void Open()
    {
        if (_disposed || _opened) return;
        _opened = true;
        var point = _source.TranslatePoint(default, this);
        _origin = point.HasValue ? new Rect(point.Value, _source.Bounds.Size) : new Rect(8, 8, 240, 180);
        if (_source.Bounds.Width > 0 && _source.Bounds.Height > 0)
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            _snapshot = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_source.Bounds.Width * scale),
                (int)Math.Ceiling(_source.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
            // Snapshot only the content. The same live glass supplies the material
            // throughout the animation; compositing another glass image changes alpha.
            var sourceGlass = _source.GetVisualDescendants().OfType<LiquidGlassSurface>()
                .Select(glass => (Glass: glass, Opacity: glass.Opacity)).ToArray();
            try
            {
                foreach (var item in sourceGlass) item.Glass.Opacity = 0;
                _snapshot.Render(_source);
            }
            finally
            {
                foreach (var item in sourceGlass) item.Glass.Opacity = item.Opacity;
            }
            _preview.Source = _snapshot;
            _preview.Width = _origin.Width; _preview.Height = _origin.Height;
        }
        ApplyProgress();
        UpdateLayout();
        _source.Opacity = 0;
        Opacity = 1;
        Focus();
        ApplyProgress();
        _clock.Restart(); _timer.Start();
    }

    public void Close()
    {
        if (_closing || _disposed) return;
        _closing = true; _start = _progress;
        var point = _source.TranslatePoint(default, this);
        if (point.HasValue && _source.IsEffectivelyVisible && TopLevel.GetTopLevel(_source) != null)
            _origin = new Rect(point.Value, _source.Bounds.Size);
        else _preview.IsVisible = false;
        _content.IsHitTestVisible = false;
        _clock.Restart(); _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / (_closing ? 240 : 320), 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        _progress = _closing ? _start * (1 - eased) : eased;
        ApplyProgress();
        if (t < 1) return;
        _timer.Stop();
        if (_closing) Dispose();
        else
        {
            _content.Opacity = 1;
            _content.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
        }
    }

    internal static Size GetSheetSize(Size available)
    {
        const double aspectRatio = 1.5;
        var width = Math.Min(960, Math.Min(Math.Max(1, available.Width - 64), Math.Max(1, available.Height - 64) * aspectRatio));
        return new Size(width, width / aspectRatio);
    }

    private void ApplyProgress()
    {
        if (Bounds.Width <= 16 || Bounds.Height <= 16) return;
        var size = GetSheetSize(Bounds.Size);
        var width = size.Width;
        var height = size.Height;
        var target = new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        if (_content.Width != width) _content.Width = _glass.Width = width;
        if (_content.Height != height) _content.Height = _glass.Height = height;
        _glassHost.Width = width + 32;
        _glassHost.Height = height + 32;
        _scrim.Opacity = _progress;
        if (_closing && !_preview.IsVisible)
        {
            _glassHost.Opacity = _progress;
            _content.Opacity = _progress;
            return;
        }
        var p = _progress;
        var x = _origin.X + (target.X - _origin.X) * p;
        var y = _origin.Y + (target.Y - _origin.Y) * p;
        var w = _origin.Width + (target.Width - _origin.Width) * p;
        var h = _origin.Height + (target.Height - _origin.Height) * p;
        var sx = (w + 32) / (target.Width + 32);
        var sy = (h + 32) / (target.Height + 32);
        _glassTransform.Matrix = new Matrix(sx, 0, 0, sy, x - target.X, y - target.Y);
        _previewTransform.Matrix = new Matrix(w / Math.Max(1, _origin.Width), 0, 0,
            h / Math.Max(1, _origin.Height), x, y);
        _preview.Opacity = Math.Clamp(1 - p * 2.5, 0, 1);
        _content.Opacity = Math.Clamp((p - .55) / .45, 0, 1);
        _content.IsHitTestVisible = !_closing && p >= 1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= OnTick;
        _source.Opacity = _sourceOpacity;
        _preview.Source = null; _snapshot?.Dispose(); _snapshot = null;
        _completion.TrySetResult();
    }
}
