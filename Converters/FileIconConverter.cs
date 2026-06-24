using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Converters;

/// <summary>
/// Converts a FileSystemEntry to a rendered SVG Bitmap (via SvgIconCache).
/// Replaces the old PathIcon + Geometry approach with full-colour Fluent-style icons.
/// </summary>
public class FileEntryToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileSystemEntry entry)
            return null;

        var size = ParseIconSize(parameter, culture);

        // Return thumbnail if available (e.g. face crop for AI face clusters)
        if (!string.IsNullOrWhiteSpace(entry.ThumbnailUrl))
        {
            var bitmap = LoadBitmapFromUrl(entry.ThumbnailUrl);
            if (bitmap != null) return bitmap;
        }

        if (!entry.IsDirectory)
            return SvgIconCache.GetFileIcon(entry.IconKey, entry.Extension, size);

        return entry.IconKey is { Length: > 3 } && entry.IconKey.StartsWith("ai-")
            ? SvgIconCache.GetAiIcon(entry.IconKey, size)
            : SvgIconCache.GetFolderIcon(size);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Bitmap? LoadBitmapFromUrl(string url)
    {
        try
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = url.IndexOf(',');
                if (comma < 0) return null;
                using var dataStream = new MemoryStream(System.Convert.FromBase64String(url[(comma + 1)..]));
                return new Bitmap(dataStream);
            }

            if (File.Exists(url))
            {
                using var fileStream = File.OpenRead(url);
                return new Bitmap(fileStream);
            }
        }
        catch
        {
            // Fall through to icon
        }
        return null;
    }

    private static int ParseIconSize(object? parameter, CultureInfo culture)
    {
        var size = parameter switch
        {
            int value => value,
            double value => (int)Math.Round(value),
            decimal value => (int)Math.Round(value),
            string value when int.TryParse(value, NumberStyles.Integer, culture, out var parsed) => parsed,
            string value when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 48
        };

        return Math.Clamp(size, 16, 512);
    }
}

/// <summary>
/// Legacy color converter — kept for backward compatibility.
/// The SVG bitmaps already carry their own colours; this returns a neutral fallback.
/// </summary>
public class FileEntryToIconColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Brushes.Gray; // unused when Image replaces PathIcon
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
