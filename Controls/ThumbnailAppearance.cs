using Avalonia;
using Avalonia.Media;

namespace MacExplorer.Controls;

/// <summary>Shared, centered edge separation for file thumbnails.</summary>
internal static class ThumbnailAppearance
{
    public static void DrawShadow(DrawingContext context, Rect bounds, BoxShadows shadow)
        => context.DrawRectangle(null, null, new RoundedRect(bounds, 0), shadow);
}
