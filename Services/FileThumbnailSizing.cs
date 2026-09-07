namespace MacExplorer.Services;

public static class FileThumbnailSizing
{
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
