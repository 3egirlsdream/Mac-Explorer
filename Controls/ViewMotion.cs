using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using LiquidGlassAvaloniaUI;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Controls;

/// <summary>Shared, reduced-motion-aware compositor and frame animation helpers for view transitions.</summary>
internal static class ViewMotion
{
    internal const int FastDurationMs = 160;
    internal const int StandardDurationMs = 200;
    internal const int MorphDurationMs = 240;

    internal static bool? ForceReducedMotionForTesting { get; set; }

    internal static bool PrefersReducedMotion
        => ForceReducedMotionForTesting ?? MacWindowChrome.PrefersReducedMotion;

    internal static bool ShouldAnimate(Visual? visual)
        => !PrefersReducedMotion
           && visual is { IsEffectivelyVisible: true }
           && TopLevel.GetTopLevel(visual) != null;

    internal static double EaseOutCubic(double t)
    {
        var clamped = Math.Clamp(t, 0, 1);
        var inv = 1 - clamped;
        return 1 - inv * inv * inv;
    }

    internal static Rect LerpRect(Rect from, Rect to, double t)
        => new(
            from.X + (to.X - from.X) * t,
            from.Y + (to.Y - from.Y) * t,
            Math.Max(0, from.Width + (to.Width - from.Width) * t),
            Math.Max(0, from.Height + (to.Height - from.Height) * t));

    /// <summary>Runs a GPU-composited entrance animation without triggering layout passes.</summary>
    internal static void AnimateEnter(
        Visual? visual,
        double fromX = 0,
        double fromY = 0,
        double fromScale = 1,
        double fromOpacity = 0,
        int durationMs = StandardDurationMs)
    {
        if (visual == null) return;
        var composition = ElementComposition.GetElementVisual(visual);
        if (composition == null) return;

        StopCompositionAnimations(composition);

        if (!ShouldAnimate(visual))
        {
            ResetCompositionVisual(composition);
            return;
        }

        var duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));
        var easing = new CubicEaseOut();
        var compositor = composition.Compositor;

        if (fromOpacity < 0.999)
        {
            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Duration = duration;
            opacityAnim.InsertKeyFrame(0f, (float)Math.Clamp(fromOpacity, 0, 1));
            opacityAnim.InsertKeyFrame(1f, 1f, easing);
            composition.Opacity = 1f;
            composition.StartAnimation(nameof(CompositionVisual.Opacity), opacityAnim);
        }
        else
        {
            composition.Opacity = 1f;
        }

        if (Math.Abs(fromX) > 0.01 || Math.Abs(fromY) > 0.01)
        {
            var translationAnim = compositor.CreateVector3DKeyFrameAnimation();
            translationAnim.Duration = duration;
            translationAnim.InsertKeyFrame(0f, new Vector3D(fromX, fromY, 0));
            translationAnim.InsertKeyFrame(1f, default, easing);
            composition.Translation = default;
            composition.StartAnimation(nameof(CompositionVisual.Translation), translationAnim);
        }
        else
        {
            composition.Translation = default;
        }

        if (Math.Abs(fromScale - 1) > 0.001 && visual.Bounds.Width > 1 && visual.Bounds.Height > 1)
        {
            composition.CenterPoint = new Vector3D(visual.Bounds.Width / 2, visual.Bounds.Height / 2, 0);
            var scaleAnim = compositor.CreateVector3DKeyFrameAnimation();
            scaleAnim.Duration = duration;
            scaleAnim.InsertKeyFrame(0f, new Vector3D(fromScale, fromScale, 1));
            scaleAnim.InsertKeyFrame(1f, new Vector3D(1, 1, 1), easing);
            composition.Scale = new Vector3D(1, 1, 1);
            composition.StartAnimation(nameof(CompositionVisual.Scale), scaleAnim);
        }
        else
        {
            composition.Scale = new Vector3D(1, 1, 1);
        }
    }

    /// <summary>Animates a visual from an old rect to its new bounds inside the same parent coordinate space.</summary>
    internal static void AnimateFromRect(Visual? visual, Rect fromRect, Rect toRect, int durationMs = 220)
    {
        if (visual == null) return;
        var composition = ElementComposition.GetElementVisual(visual);
        if (composition == null) return;

        StopCompositionAnimations(composition);

        if (!ShouldAnimate(visual) || toRect.Width <= 1 || toRect.Height <= 1 || fromRect.Width <= 1 || fromRect.Height <= 1)
        {
            ResetCompositionVisual(composition);
            return;
        }

        var dx = fromRect.X - toRect.X;
        var dy = fromRect.Y - toRect.Y;
        var sx = Math.Clamp(fromRect.Width / toRect.Width, 0.25, 4.0);
        var sy = Math.Clamp(fromRect.Height / toRect.Height, 0.25, 4.0);
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5 && Math.Abs(sx - 1) < 0.005 && Math.Abs(sy - 1) < 0.005)
        {
            ResetCompositionVisual(composition);
            return;
        }

        var duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));
        var easing = new CubicEaseOut();
        var compositor = composition.Compositor;

        composition.CenterPoint = default;

        var translationAnim = compositor.CreateVector3DKeyFrameAnimation();
        translationAnim.Duration = duration;
        translationAnim.InsertKeyFrame(0f, new Vector3D(dx, dy, 0));
        translationAnim.InsertKeyFrame(1f, default, easing);
        composition.Translation = default;
        composition.StartAnimation(nameof(CompositionVisual.Translation), translationAnim);

        var scaleAnim = compositor.CreateVector3DKeyFrameAnimation();
        scaleAnim.Duration = duration;
        scaleAnim.InsertKeyFrame(0f, new Vector3D(sx, sy, 1));
        scaleAnim.InsertKeyFrame(1f, new Vector3D(1, 1, 1), easing);
        composition.Scale = new Vector3D(1, 1, 1);
        composition.StartAnimation(nameof(CompositionVisual.Scale), scaleAnim);
    }

    /// <summary>Slides a visual along X on the compositor thread without layout invalidation.</summary>
    internal static void AnimateTranslationX(Visual? visual, double fromX, double toX, int durationMs = 180)
    {
        if (visual == null) return;
        var composition = ElementComposition.GetElementVisual(visual);
        if (composition == null) return;

        composition.StopAnimation(nameof(CompositionVisual.Translation));

        if (!ShouldAnimate(visual) || Math.Abs(toX - fromX) < 0.25)
        {
            composition.Translation = new Vector3D(toX, 0, 0);
            return;
        }

        var animation = composition.Compositor.CreateVector3DKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));
        animation.InsertKeyFrame(0f, new Vector3D(fromX, 0, 0));
        animation.InsertKeyFrame(1f, new Vector3D(toX, 0, 0), new CubicEaseOut());
        composition.Translation = new Vector3D(toX, 0, 0);
        composition.StartAnimation(nameof(CompositionVisual.Translation), animation);
    }

    internal static void Reset(Visual? visual)
    {
        if (visual == null) return;
        var composition = ElementComposition.GetElementVisual(visual);
        if (composition == null) return;
        StopCompositionAnimations(composition);
        ResetCompositionVisual(composition);
    }

    private static void StopCompositionAnimations(CompositionVisual composition)
    {
        composition.StopAnimation(nameof(CompositionVisual.Opacity));
        composition.StopAnimation(nameof(CompositionVisual.Translation));
        composition.StopAnimation(nameof(CompositionVisual.Scale));
    }

    private static void ResetCompositionVisual(CompositionVisual composition)
    {
        composition.Opacity = 1f;
        composition.Translation = default;
        composition.Scale = new Vector3D(1, 1, 1);
    }

    /// <summary>
    /// Captures a lightweight snapshot of a closing overlay before it hides synchronously,
    /// animating the ghost out inside <paramref name="host"/> so IsVisible transitions remain immediate.
    /// </summary>
    internal static IDisposable? PlayDismissGhost(
        Panel? host,
        Control? source,
        double toX = 0,
        double toY = 10,
        double toScale = 0.97,
        int durationMs = FastDurationMs)
    {
        if (host == null || source == null || !ShouldAnimate(source)) return null;
        if (source.Bounds.Width <= 1 || source.Bounds.Height <= 1) return null;
        var topLevel = TopLevel.GetTopLevel(host);
        if (topLevel == null) return null;
        var origin = source.TranslatePoint(default, host);
        if (origin == null) return null;

        var scale = Math.Clamp(topLevel.RenderScaling, 1, 3);
        var pixelWidth = (int)Math.Ceiling(source.Bounds.Width * scale);
        var pixelHeight = (int)Math.Ceiling(source.Bounds.Height * scale);
        if (pixelWidth <= 0 || pixelHeight <= 0 || pixelWidth > 4096 || pixelHeight > 4096) return null;

        RenderTargetBitmap? bitmap = null;
        try
        {
            bitmap = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight), new Vector(96 * scale, 96 * scale));
            bitmap.Render(source);
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }

        var ghost = new Image
        {
            Source = bitmap,
            Width = source.Bounds.Width,
            Height = source.Bounds.Height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(origin.Value.X, origin.Value.Y, 0, 0),
            RenderTransformOrigin = RelativePoint.Center
        };
        LiquidGlassBackdrop.SetIsExcludedFromCapture(ghost, true);
        host.Children.Add(ghost);

        return new GhostAnimation(host, topLevel, ghost, bitmap, toX, toY, toScale, durationMs);
    }

    /// <summary>Drives a cancelable frame-paced numeric interpolation via TopLevel.RequestAnimationFrame.</summary>
    internal static IDisposable StartFrameAnimation(
        Control owner,
        double from,
        double to,
        int durationMs,
        Action<double> apply,
        Action? onCompleted = null)
    {
        var topLevel = TopLevel.GetTopLevel(owner);
        if (!ShouldAnimate(owner) || topLevel == null || Math.Abs(to - from) < 0.01)
        {
            apply(to);
            onCompleted?.Invoke();
            return EmptyDisposable.Instance;
        }

        var runner = new FrameAnimationRunner(owner, topLevel, from, to, durationMs, apply, onCompleted);
        runner.Start();
        return runner;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class FrameAnimationRunner(
        Control owner,
        TopLevel topLevel,
        double from,
        double to,
        int durationMs,
        Action<double> apply,
        Action? onCompleted) : IDisposable
    {
        private TimeSpan? _startTime;
        private bool _disposed;

        public void Start()
        {
            apply(from);
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan timestamp)
        {
            if (_disposed) return;
            if (!owner.IsEffectivelyVisible || TopLevel.GetTopLevel(owner) == null || PrefersReducedMotion)
            {
                Finish(jumpToEnd: true);
                return;
            }

            _startTime ??= timestamp;
            var elapsedMs = (timestamp - _startTime.Value).TotalMilliseconds;
            var progress = Math.Clamp(elapsedMs / Math.Max(1, durationMs), 0, 1);
            var eased = EaseOutCubic(progress);
            apply(from + (to - from) * eased);

            if (progress >= 1)
                Finish(jumpToEnd: false);
            else
                topLevel.RequestAnimationFrame(OnFrame);
        }

        private void Finish(bool jumpToEnd)
        {
            if (_disposed) return;
            _disposed = true;
            if (jumpToEnd) apply(to);
            onCompleted?.Invoke();
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    private sealed class GhostAnimation : IDisposable
    {
        private readonly Panel _host;
        private readonly TopLevel _topLevel;
        private readonly Image _ghost;
        private RenderTargetBitmap? _bitmap;
        private readonly double _toX;
        private readonly double _toY;
        private readonly double _toScale;
        private readonly int _durationMs;
        private TimeSpan? _startTime;
        private bool _disposed;

        public GhostAnimation(Panel host, TopLevel topLevel, Image ghost, RenderTargetBitmap bitmap,
            double toX, double toY, double toScale, int durationMs)
        {
            _host = host;
            _topLevel = topLevel;
            _ghost = ghost;
            _bitmap = bitmap;
            _toX = toX;
            _toY = toY;
            _toScale = toScale;
            _durationMs = Math.Max(1, durationMs);
            _topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan timestamp)
        {
            if (_disposed) return;
            if (TopLevel.GetTopLevel(_host) == null || PrefersReducedMotion)
            {
                Dispose();
                return;
            }

            _startTime ??= timestamp;
            var progress = Math.Clamp((timestamp - _startTime.Value).TotalMilliseconds / _durationMs, 0, 1);
            var eased = EaseOutCubic(progress);
            _ghost.Opacity = 1 - eased;
            var scale = 1 + (_toScale - 1) * eased;
            var cx = _ghost.Width / 2;
            var cy = _ghost.Height / 2;
            _ghost.RenderTransform = new MatrixTransform(
                Matrix.CreateTranslation(-cx, -cy)
                * Matrix.CreateScale(scale, scale)
                * Matrix.CreateTranslation(cx + _toX * eased, cy + _toY * eased));

            if (progress >= 1)
                Dispose();
            else
                _topLevel.RequestAnimationFrame(OnFrame);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ghost.Source = null;
            _host.Children.Remove(_ghost);
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}
