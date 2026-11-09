namespace MacExplorer.Services;

public static class FileThumbnailSizing
{
    internal const int DefaultGridLevel = 1;
    internal const int LargestGridLevel = 3;
    internal const int GalleryLevel = 4;
    internal const int DefaultGridSize = 56;
    internal const int StripSize = 48;

    internal static int GetGridSize(int level) => level switch
    {
        0 => 32,
        2 => 96,
        LargestGridLevel => 128,
        _ => DefaultGridSize
    };

    /// <summary>Physical pixels, rounded to cache-friendly buckets; no filesystem lookup.</summary>
    public static int GetPixelSize(double logicalSize, double renderScaling)
    {
        if (!double.IsFinite(logicalSize) || logicalSize <= 0) logicalSize = 56;
        if (!double.IsFinite(renderScaling) || renderScaling <= 0) renderScaling = 1;
        // Clamp before converting to int (including extreme/overflowing input).
        var physical = Math.Clamp(logicalSize * renderScaling, 32, 256);
        return (int)Math.Ceiling(physical / 32) * 32;
    }
}
