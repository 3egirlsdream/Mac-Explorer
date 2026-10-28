using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LiquidGlassAvaloniaUI
{
    internal static class LiquidGlassVisualRenderer
    {
        private sealed class ClipRadiusAccessor
        {
            public ClipRadiusAccessor(Type type) => Property = FindProperty(type);
            public PropertyInfo? Property { get; }

            private static PropertyInfo? FindProperty(Type type)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var property = type.GetProperty("ClipToBoundsRadius", flags)
                    ?? type.GetProperties(flags).FirstOrDefault(p => p.Name.EndsWith(".ClipToBoundsRadius", StringComparison.Ordinal));
                return property?.PropertyType == typeof(CornerRadius) ? property : null;
            }
        }

        // Most visual types have no rounded-clip property. Cache that result too;
        // enumerating every property on every backdrop frame dominates capture time.
        private static readonly ConditionalWeakTable<Type, ClipRadiusAccessor> s_clipRadiusAccessors = new();

        public static void Render(DrawingContext context, Visual visual, Rect clipRect, ISet<Visual>? excludedRoots, bool captureForeground = false)
        {
            if (clipRect.Width <= 0 || clipRect.Height <= 0)
                return;

            Rect targetClip = new(clipRect.Size);
            using (context.PushTransform(Matrix.CreateTranslation(-clipRect.Position.X, -clipRect.Position.Y)))
            using (context.PushClip(targetClip))
            {
                Render(context, visual, new Rect(visual.Bounds.Size), Matrix.Identity, clipRect, excludedRoots, captureForeground);
            }
        }

        private static void Render(
            DrawingContext context,
            Visual visual,
            Rect bounds,
            Matrix parentTransform,
            Rect clipRect,
            ISet<Visual>? excludedRoots,
            bool captureForeground)
        {
            if (excludedRoots is not null && excludedRoots.Contains(visual))
                return;

            if (captureForeground
                ? LiquidGlassBackdrop.GetIsExcludedFromForegroundCapture(visual)
                : LiquidGlassBackdrop.GetIsExcludedFromCapture(visual))
                return;

            if (!visual.IsVisible || visual.Opacity <= 0)
                return;

            Rect rect = new(bounds.Size);
            Matrix transform;

            if (visual.RenderTransform?.Value is { } rt)
            {
                Point origin = visual.RenderTransformOrigin.ToPixels(visual.Bounds.Size);
                Matrix offset = Matrix.CreateTranslation(origin);
                transform = -offset * rt * offset * Matrix.CreateTranslation(bounds.Position);
            }
            else
            {
                transform = Matrix.CreateTranslation(bounds.Position);
            }

            using (context.PushTransform(transform))
            using (visual.HasMirrorTransform
                ? context.PushTransform(new Matrix(-1.0, 0.0, 0.0, 1.0, visual.Bounds.Width, 0))
                : default(DrawingContext.PushedState?))
            using (context.PushOpacity(visual.Opacity))
            using (PushClipToBounds(context, visual, rect))
            using (visual.Clip is { } clip ? context.PushGeometryClip(clip) : default(DrawingContext.PushedState?))
            using (visual.OpacityMask is { } opacityMask ? context.PushOpacityMask(opacityMask, rect) : default(DrawingContext.PushedState?))
            {
                Matrix totalTransform = transform * parentTransform;
                Rect visualBounds = rect.TransformToAABB(totalTransform);

                if (visualBounds.Intersects(clipRect))
                    visual.Render(context);

                IEnumerable<Visual> children = GetOrderedChildren(visual);

                if (visual.ClipToBounds)
                {
                    totalTransform = Matrix.Identity;
                    clipRect = rect;
                }

                foreach (Visual? child in children)
                {
                    Render(context, child, child.Bounds, totalTransform, clipRect, excludedRoots, captureForeground);
                }
            }
        }

        private static DrawingContext.PushedState? PushClipToBounds(DrawingContext context, Visual visual, Rect rect)
        {
            if (!visual.ClipToBounds)
                return default;

            if (TryGetClipToBoundsRadius(visual, out CornerRadius radius))
                return context.PushClip(new RoundedRect(rect, radius));

            return context.PushClip(rect);
        }

        private static bool TryGetClipToBoundsRadius(Visual visual, out CornerRadius radius)
        {
            radius = default;

            PropertyInfo? prop = s_clipRadiusAccessors.GetValue(visual.GetType(), static type => new ClipRadiusAccessor(type)).Property;
            if (prop is null)
                return false;

            if (prop.GetValue(visual) is not CornerRadius value)
                return false;

            if (value == default)
                return false;

            radius = value;
            return true;
        }

        private static IEnumerable<Visual> GetOrderedChildren(Visual visual)
        {
            IEnumerable<Visual> children = visual.GetVisualChildren();
            int? firstZIndex = null;
            foreach (Visual? child in children)
            {
                if (firstZIndex is null)
                    firstZIndex = child.ZIndex;
                else if (child.ZIndex != firstZIndex.Value)
                    return children.OrderBy(x => x.ZIndex);
            }
            // The common case already has painting order. Don't allocate a list
            // for every node on every capture; stable OrderBy is only needed for ZIndex.
            return children;
        }
    }
}
